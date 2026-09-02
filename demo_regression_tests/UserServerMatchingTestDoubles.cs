using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;
using network.contracts.authentication;
using network.interfaces;
using RedLockNet;
using StackExchange.Redis;
using user_server.services;

namespace demo_regression_tests;

/// <summary>
///     user_server 매칭 테스트가 공유하는 CSV 로드 헬퍼. 스폰 배정은 School2 map 데이터가 필요하다.
/// </summary>
internal static class UserServerMatchingTestData
{
    private static readonly object _lock = new();
    private static bool _loaded;

    public static void EnsureGameDataLoaded()
    {
        lock (_lock)
        {
            if (_loaded) return;

            GameDataHelper.SetBasePath(FindNetworkBasePath());
            GameDataHelper.Initialize();
            _loaded = true;
        }
    }

    private static string FindNetworkBasePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "network", "Common", "csv");
            if (Directory.Exists(candidate))
                return Path.Combine(directory.FullName, "network");
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate network/Common/csv.");
    }

    public static MatchingQueueEntry HumanEntry(long playerId, DateTime? requestTime = null, string? requestId = null)
    {
        return MatchingQueueEntry.FromData(new MatchingQueueData
        {
            PlayerId = playerId,
            RequestTime = requestTime ?? new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(playerId),
            RequestId = requestId ?? $"req{playerId}"
        });
    }
}

/// <summary>
///     매칭 경로가 쓰는 String/Hash/SortedSet 연산만 메모리로 구현한 ICacheHelper. 나머지는 NotSupported.
///     한 lock으로 직렬화하며, 실패 주입은 <see cref="HashGetError" />로 한다.
/// </summary>
internal sealed class InMemoryCacheHelper : ICacheHelper
{
    private readonly object _sync = new();
    private readonly Dictionary<string, RedisValue> _strings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, byte[]>> _hashes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(byte[] Value, double Score)>> _sortedSets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TimeSpan?> _expiries = new(StringComparer.Ordinal);

    public Exception? HashGetError { get; set; }

    public IReadOnlyDictionary<string, TimeSpan?> Expiries
    {
        get
        {
            lock (_sync) return new Dictionary<string, TimeSpan?>(_expiries, StringComparer.Ordinal);
        }
    }

    public string? GetString(string key)
    {
        lock (_sync) return _strings.TryGetValue(key, out RedisValue value) ? value.ToString() : null;
    }

    public byte[]? GetHash(string key, string field)
    {
        lock (_sync)
        {
            return _hashes.TryGetValue(key, out var hash) && hash.TryGetValue(field, out byte[]? value) ? value : null;
        }
    }

    public byte[]? GetHash(string key, long field) => GetHash(key, field.ToString());

    public bool SortedSetContains(string key, byte[] value)
    {
        lock (_sync)
        {
            return _sortedSets.TryGetValue(key, out var set) && set.Any(item => item.Value.AsSpan().SequenceEqual(value));
        }
    }

    public int SortedSetCount(string key)
    {
        lock (_sync) return _sortedSets.TryGetValue(key, out var set) ? set.Count : 0;
    }

    public IReadOnlyList<string> StringKeys
    {
        get
        {
            lock (_sync) return _strings.Keys.ToList();
        }
    }

    public IRedLockFactory GetRedLockFactory() => throw new NotSupportedException();

    public Task<bool> HashSetAsync(string key, long field, byte[] value, int db = -1) =>
        HashSetAsync(key, field.ToString(), value, db);

    public Task<bool> HashSetAsync(string key, string field, byte[] value, int db = -1)
    {
        lock (_sync)
        {
            if (!_hashes.TryGetValue(key, out var hash))
            {
                hash = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                _hashes[key] = hash;
            }

            bool created = !hash.ContainsKey(field);
            hash[field] = value.ToArray();
            return Task.FromResult(created);
        }
    }

    public async Task HashSetWithExpiryAsync(string key, string field, byte[] value, TimeSpan expiry, int db = -1)
    {
        await HashSetAsync(key, field, value, db);
        lock (_sync) _expiries[key] = expiry;
    }

    public Task HashSetPairAtomicAsync(string firstKey, string firstField, RedisValue firstValue, string secondKey,
        string secondField, RedisValue secondValue, int db = -1) => throw new NotSupportedException();

    public Task<RedisValue> HashGetAsync(string key, string field, int db = -1)
    {
        if (HashGetError != null) throw HashGetError;
        byte[]? value = GetHash(key, field);
        return Task.FromResult(value == null ? RedisValue.Null : (RedisValue)value);
    }

    public Task<RedisValue> HashGetAsync(string key, long field, int db = -1) => HashGetAsync(key, field.ToString(), db);

    public Task<RedisValue> HashGetDeleteFirstAsync(string firstKey, RedisValue firstField, string secondKey,
        RedisValue secondField, int db = -1) => throw new NotSupportedException();

    public Task<RedisValue[]> HashGetAsync(string key, RedisValue[] fields, int db = -1) => throw new NotSupportedException();

    public Task<bool> HashDeleteAsync(string key, string field, int db = -1)
    {
        lock (_sync)
        {
            return Task.FromResult(_hashes.TryGetValue(key, out var hash) && hash.Remove(field));
        }
    }

    public Task<bool> HashDeleteAsync(string key, long field, int db = -1) => HashDeleteAsync(key, field.ToString(), db);
    public Task<bool> HashExistsAsync(string key, string field, int db = -1) => Task.FromResult(GetHash(key, field) != null);

    public Task<long> StringIncrementAsync(string key, int db = -1) => StringIncrementByAsync(key, 1, db);

    public Task<long> StringIncrementByAsync(string key, long increment, int db = -1)
    {
        lock (_sync)
        {
            long current = _strings.TryGetValue(key, out RedisValue value) ? (long)value : 0;
            current += increment;
            _strings[key] = current;
            return Task.FromResult(current);
        }
    }

    public Task<RedisValue> StringGetAsync(string key, int db = -1)
    {
        lock (_sync)
        {
            return Task.FromResult(_strings.TryGetValue(key, out RedisValue value) ? value : RedisValue.Null);
        }
    }

    public Task<RedisValue> StringGetDeleteAsync(string key, int db = -1) => throw new NotSupportedException();

    public Task<bool> StringSetAsync(string key, RedisValue value, TimeSpan? expiry = null, int db = -1)
    {
        lock (_sync)
        {
            _strings[key] = value;
            _expiries[key] = expiry;
            return Task.FromResult(true);
        }
    }

    public Task<bool> StringSetIfNotExistsAsync(string key, RedisValue value, TimeSpan? expiry = null, int db = -1)
    {
        lock (_sync)
        {
            if (_strings.ContainsKey(key)) return Task.FromResult(false);
            _strings[key] = value;
            _expiries[key] = expiry;
            return Task.FromResult(true);
        }
    }

    public Task<bool> StringSetIfEqualsAsync(string key, string expectedValue, string newValue, TimeSpan expiry, int db = -1)
    {
        lock (_sync)
        {
            if (!_strings.TryGetValue(key, out RedisValue value) ||
                !string.Equals(value.ToString(), expectedValue, StringComparison.Ordinal))
                return Task.FromResult(false);
            _strings[key] = newValue;
            _expiries[key] = expiry;
            return Task.FromResult(true);
        }
    }

    public Task<bool> StringDeleteIfEqualsAsync(string key, string expectedValue, int db = -1)
    {
        lock (_sync)
        {
            if (!_strings.TryGetValue(key, out RedisValue value) ||
                !string.Equals(value.ToString(), expectedValue, StringComparison.Ordinal))
                return Task.FromResult(false);
            _strings.Remove(key);
            return Task.FromResult(true);
        }
    }

    public Task<bool> KeyDeleteAsync(string key, int db = -1)
    {
        lock (_sync)
        {
            bool removed = _strings.Remove(key) | _hashes.Remove(key) | _sortedSets.Remove(key);
            _expiries.Remove(key);
            return Task.FromResult(removed);
        }
    }

    public Task<bool> KeyExpireAsync(string key, TimeSpan? expiry, int db = -1)
    {
        lock (_sync)
        {
            _expiries[key] = expiry;
            return Task.FromResult(true);
        }
    }

    public Task<bool> SortedSetAddAsync(string key, byte[] value, double score, int db = -1)
    {
        lock (_sync)
        {
            if (!_sortedSets.TryGetValue(key, out var set))
            {
                set = new List<(byte[] Value, double Score)>();
                _sortedSets[key] = set;
            }

            int index = set.FindIndex(item => item.Value.AsSpan().SequenceEqual(value));
            if (index >= 0)
            {
                set[index] = (set[index].Value, score);
                return Task.FromResult(false);
            }

            set.Add((value.ToArray(), score));
            return Task.FromResult(true);
        }
    }

    public Task<byte[][]> SortedSetRangeByScoreAsync(string key, double start = double.NegativeInfinity,
        double stop = double.PositiveInfinity, int db = -1)
    {
        lock (_sync)
        {
            if (!_sortedSets.TryGetValue(key, out var set)) return Task.FromResult(Array.Empty<byte[]>());
            byte[][] result = set
                .Where(item => item.Score >= start && item.Score <= stop)
                .OrderBy(item => item.Score)
                .ThenBy(item => item.Value, LexicographicByteComparer.Instance)
                .Select(item => item.Value.ToArray())
                .ToArray();
            return Task.FromResult(result);
        }
    }

    public Task<bool> SortedSetRemoveAsync(string key, byte[] value, int db = -1)
    {
        lock (_sync)
        {
            if (!_sortedSets.TryGetValue(key, out var set)) return Task.FromResult(false);
            int removed = set.RemoveAll(item => item.Value.AsSpan().SequenceEqual(value));
            return Task.FromResult(removed > 0);
        }
    }

    private sealed class LexicographicByteComparer : IComparer<byte[]>
    {
        public static readonly LexicographicByteComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y.AsSpan());
    }
}

/// <summary>
///     RedisMatchingQueueClaimStore의 Lua(ZSCORE 확인 + SET NX)를 InMemoryCacheHelper 위에서 재현한다.
/// </summary>
internal sealed class InMemoryMatchingClaimStore(InMemoryCacheHelper cache) : IMatchingQueueClaimStore
{
    public int ClaimAttempts { get; private set; }

    public Task<bool> TryClaimQueueEntryAsync(byte[] queueEntry, long playerId, string claimId, TimeSpan expiry)
    {
        ClaimAttempts++;
        if (!cache.SortedSetContains(MatchingQueue.QueueKey, queueEntry))
            return Task.FromResult(false);
        return cache.StringSetIfNotExistsAsync(MatchingHandoffRedisKeys.ClaimKey(playerId), claimId, expiry);
    }
}

/// <summary>
///     항상 즉시 획득되는 RedLock. 자원 이름만 기록한다.
/// </summary>
internal sealed class FakeRedLockFactory : IRedLockFactory
{
    public ConcurrentQueue<string> AcquiredResources { get; } = new();

    public Task<IRedLock> AcquireLockAsync(string resource, TimeSpan expiryTime)
    {
        AcquiredResources.Enqueue(resource);
        return Task.FromResult<IRedLock>(new FakeRedLock(resource));
    }

    public async Task ExecuteWithLockAsync(string resource, TimeSpan expiryTime, Func<Task> action)
    {
        await using var _ = await AcquireLockAsync(resource, expiryTime);
        await action();
    }

    public async Task<T> ExecuteWithLockAsync<T>(string resource, TimeSpan expiryTime, Func<Task<T>> action)
    {
        await using var _ = await AcquireLockAsync(resource, expiryTime);
        return await action();
    }

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class FakeRedLock(string resource) : IRedLock
    {
        public string Resource { get; } = resource;
        public string LockId { get; } = Guid.NewGuid().ToString("N");
        public bool IsAcquired => true;
        public RedLockStatus Status => RedLockStatus.Acquired;
        public RedLockInstanceSummary InstanceSummary => new(1, 0, 0);
        public int ExtendCount => 0;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
///     MatchmakingPass가 handoff port를 호출한 순서를 기록하는 fake. 전달 결과·예외·admission 취소 결과를 주입한다.
/// </summary>
internal sealed class RecordingHandoffPublisher : IMatchHandoffPublisher
{
    public List<string> Events { get; } = new();
    public Dictionary<long, IReadOnlyList<BotMatchingInfo>> StoredBots { get; } = new();
    public List<(long MatchingId, RosterChainLink Link, int RosterCount, int HumanRosterCount)> Deliveries { get; } = new();
    public Func<long, bool> DeliverResult { get; set; } = _ => true;
    public HashSet<long> ThrowOnDeliver { get; } = new();
    public bool CancelAdmissionResult { get; set; } = true;
    public bool WatchdogResult { get; set; } = true;

    public Task StoreBotHandoffAsync(long matchingId, IReadOnlyList<BotMatchingInfo> bots)
    {
        StoredBots[matchingId] = bots;
        Events.Add($"bots:{matchingId}:{bots.Count}");
        return Task.CompletedTask;
    }

    public Task<bool> DeliverMatchingSuccessAsync(RosterChainLink link, long matchingId, List<PlayerInfo> playerRoster,
        List<GameHandoffRosterEntry> humanHandoffRoster)
    {
        Deliveries.Add((matchingId, link, playerRoster.Count, humanHandoffRoster.Count));
        Events.Add($"deliver:{matchingId}:{link.PlayerId}");
        if (ThrowOnDeliver.Contains(link.PlayerId))
            throw new InvalidOperationException($"delivery failed for {link.PlayerId}");
        return Task.FromResult(DeliverResult(link.PlayerId));
    }

    public Task MarkHandoffReadyAsync(long matchingId)
    {
        Events.Add($"ready:{matchingId}");
        return Task.CompletedTask;
    }

    public bool StartAdmissionWatchdog(long matchingId, IReadOnlyCollection<long> humanPlayerIds)
    {
        Events.Add($"watchdog:{matchingId}:{string.Join(",", humanPlayerIds.OrderBy(id => id))}");
        return WatchdogResult;
    }

    public Task<bool> TryCancelAdmissionForRollbackAsync(long matchingId)
    {
        Events.Add($"cancel:{matchingId}");
        return Task.FromResult(CancelAdmissionResult);
    }

    public Task DeleteHandoffBestEffortAsync(long matchingId)
    {
        Events.Add($"delete:{matchingId}");
        return Task.CompletedTask;
    }

    public Task NotifyBatchFailedAsync(IEnumerable<MatchingQueueEntry> players, long matchingId)
    {
        Events.Add($"notify_failed:{matchingId}:{string.Join(",", players.Select(p => p.PlayerId).OrderBy(id => id))}");
        return Task.CompletedTask;
    }
}

/// <summary>
///     레벨별 로그 메시지를 모아 두는 로거.
/// </summary>
internal sealed class RecordingLogger : ILogger
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull =>
        null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        _entries.Enqueue((logLevel, formatter(state, exception)));

    public bool Contains(LogLevel level, string text) =>
        _entries.Any(entry => entry.Level == level && entry.Message.Contains(text, StringComparison.Ordinal));
}
