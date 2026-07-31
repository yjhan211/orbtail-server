using System.Collections.Concurrent;
using network.interfaces;
using StackExchange.Redis;

namespace standalone_server.infrastructure;

/// <summary>
/// Match-lifetime cache used by the submission server. User and game servers share
/// this singleton, so their normal Redis-backed repository contracts stay intact.
/// </summary>
public sealed class InMemoryCacheHelper(IRedLockFactory redLockFactory) : ICacheHelper
{
    private readonly ConcurrentDictionary<CacheKey, ConcurrentDictionary<string, byte[]>> _hashes = new();
    private readonly ConcurrentDictionary<CacheKey, LockedList> _lists = new();
    private readonly ConcurrentDictionary<CacheKey, long> _strings = new();
    private readonly ConcurrentDictionary<CacheKey, LockedSortedSet> _sortedSets = new();
    private readonly ConcurrentDictionary<CacheKey, CancellationTokenSource> _expirations = new();

    public IRedLockFactory GetRedLockFactory() => redLockFactory;

    public Task<bool> HashSetAsync(string key, long field, byte[] value, int db = -1) =>
        HashSetAsync(key, field.ToString(), value, db);

    public Task<bool> HashSetAsync(string key, string field, byte[] value, int db = -1)
    {
        var hash = _hashes.GetOrAdd(MakeKey(key, db), _ => new ConcurrentDictionary<string, byte[]>());
        byte[] copy = value.ToArray();
        bool added = hash.TryAdd(field, copy);
        if (!added) hash[field] = copy;
        return Task.FromResult(added);
    }

    public Task<RedisValue> HashGetAsync(string key, string field, int db = -1)
    {
        if (_hashes.TryGetValue(MakeKey(key, db), out var hash) && hash.TryGetValue(field, out byte[]? value))
            return Task.FromResult((RedisValue)value.ToArray());

        return Task.FromResult(RedisValue.Null);
    }

    public Task<RedisValue> HashGetAsync(string key, long field, int db = -1) =>
        HashGetAsync(key, field.ToString(), db);

    public async Task<RedisValue[]> HashGetAsync(string key, RedisValue[] fields, int db = -1)
    {
        var result = new RedisValue[fields.Length];
        for (int i = 0; i < fields.Length; i++)
            result[i] = await HashGetAsync(key, fields[i].ToString(), db);
        return result;
    }

    public Task<HashEntry[]> HashGetAllAsync(string key, int db = -1)
    {
        if (!_hashes.TryGetValue(MakeKey(key, db), out var hash))
            return Task.FromResult(Array.Empty<HashEntry>());

        HashEntry[] result = hash
            .Select(pair => new HashEntry(pair.Key, pair.Value.ToArray()))
            .ToArray();
        return Task.FromResult(result);
    }

    public Task<bool> HashDeleteAsync(string key, string field, int db = -1)
    {
        bool removed = _hashes.TryGetValue(MakeKey(key, db), out var hash) && hash.TryRemove(field, out _);
        return Task.FromResult(removed);
    }

    public Task<bool> HashDeleteAsync(string key, long field, int db = -1) =>
        HashDeleteAsync(key, field.ToString(), db);

    public Task<bool> HashExistsAsync(string key, string field, int db = -1)
    {
        bool exists = _hashes.TryGetValue(MakeKey(key, db), out var hash) && hash.ContainsKey(field);
        return Task.FromResult(exists);
    }

    public Task<RedisValue[]> ListRangeAsync(string key, int start = 0, int end = -1, int db = -1)
    {
        if (!_lists.TryGetValue(MakeKey(key, db), out var list))
            return Task.FromResult(Array.Empty<RedisValue>());

        lock (list.Gate)
            return Task.FromResult(Slice(list.Values, start, end));
    }

    public async Task<RedisValue[]> ListRangeAsync(List<string> keys, int start = 0, int end = -1, int db = -1)
    {
        var result = new List<RedisValue>();
        foreach (string key in keys)
            result.AddRange(await ListRangeAsync(key, start, end, db));
        return result.ToArray();
    }

    public async Task<List<(string key, RedisValue value)>> ListRangeWithKeyAsync(
        List<string> keys,
        int start = 0,
        int end = -1,
        int db = -1)
    {
        var result = new List<(string key, RedisValue value)>();
        foreach (string key in keys)
        {
            RedisValue[] values = await ListRangeAsync(key, start, end, db);
            result.AddRange(values.Select(value => (key, value)));
        }

        return result;
    }

    public Task<long> ListPushAsync(string key, string value, int db = -1)
    {
        var list = _lists.GetOrAdd(MakeKey(key, db), _ => new LockedList());
        lock (list.Gate)
        {
            list.Values.Add(value);
            return Task.FromResult((long)list.Values.Count);
        }
    }

    public Task<long> ListRemoveAsync(string key, string value, int db = -1)
    {
        if (!_lists.TryGetValue(MakeKey(key, db), out var list)) return Task.FromResult(0L);

        lock (list.Gate)
        {
            long removed = list.Values.RemoveAll(item => item == value);
            return Task.FromResult(removed);
        }
    }

    public Task<long> EnqueueAsync(string key, byte[] value, int db = -1)
    {
        var list = _lists.GetOrAdd(MakeKey(key, db), _ => new LockedList());
        lock (list.Gate)
        {
            list.Values.Insert(0, value.ToArray());
            return Task.FromResult((long)list.Values.Count);
        }
    }

    public Task<byte[]?> DequeueAsync(string key, int db = -1)
    {
        if (!_lists.TryGetValue(MakeKey(key, db), out var list)) return Task.FromResult<byte[]?>(null);

        lock (list.Gate)
        {
            if (list.Values.Count == 0) return Task.FromResult<byte[]?>(null);
            int index = list.Values.Count - 1;
            RedisValue value = list.Values[index];
            list.Values.RemoveAt(index);
            return Task.FromResult(value.HasValue ? ((byte[])value!).ToArray() : null);
        }
    }

    public Task<long> ListLengthAsync(string key, int db = -1)
    {
        if (!_lists.TryGetValue(MakeKey(key, db), out var list)) return Task.FromResult(0L);
        lock (list.Gate) return Task.FromResult((long)list.Values.Count);
    }

    public Task<long> StringIncrementAsync(string key, int db = -1)
    {
        long value = _strings.AddOrUpdate(MakeKey(key, db), 1, (_, current) => checked(current + 1));
        return Task.FromResult(value);
    }

    public Task<bool> KeyDeleteAsync(string key, int db = -1)
    {
        CacheKey cacheKey = MakeKey(key, db);
        CancelExpiration(cacheKey);
        bool removed = _hashes.TryRemove(cacheKey, out _);
        removed |= _lists.TryRemove(cacheKey, out _);
        removed |= _strings.TryRemove(cacheKey, out _);
        removed |= _sortedSets.TryRemove(cacheKey, out _);
        return Task.FromResult(removed);
    }

    public Task<bool> KeyExpireAsync(string key, TimeSpan? expiry, int db = -1)
    {
        CacheKey cacheKey = MakeKey(key, db);
        if (!KeyExists(cacheKey)) return Task.FromResult(false);

        if (expiry is null)
        {
            CancelExpiration(cacheKey);
            return Task.FromResult(true);
        }

        if (expiry <= TimeSpan.Zero) return KeyDeleteAsync(key, db);

        var cancellation = new CancellationTokenSource();
        CancelExpiration(cacheKey);
        _expirations[cacheKey] = cancellation;
        _ = ExpireKeyAsync(cacheKey, expiry.Value, cancellation);
        return Task.FromResult(true);
    }

    public Task<bool> SortedSetAddAsync(string key, byte[] value, double score, int db = -1)
    {
        var set = _sortedSets.GetOrAdd(MakeKey(key, db), _ => new LockedSortedSet());
        string memberKey = Convert.ToBase64String(value);
        lock (set.Gate)
        {
            bool added = !set.Values.ContainsKey(memberKey);
            set.Values[memberKey] = (value.ToArray(), score);
            return Task.FromResult(added);
        }
    }

    public Task<byte[][]> SortedSetRangeByScoreAsync(
        string key,
        double start = double.NegativeInfinity,
        double stop = double.PositiveInfinity,
        int db = -1)
    {
        if (!_sortedSets.TryGetValue(MakeKey(key, db), out var set))
            return Task.FromResult(Array.Empty<byte[]>());

        lock (set.Gate)
        {
            byte[][] result = set.Values.Values
                .Where(entry => entry.Score >= start && entry.Score <= stop)
                .OrderBy(entry => entry.Score)
                .Select(entry => entry.Value.ToArray())
                .ToArray();
            return Task.FromResult(result);
        }
    }

    public Task<bool> SortedSetRemoveAsync(string key, byte[] value, int db = -1)
    {
        if (!_sortedSets.TryGetValue(MakeKey(key, db), out var set)) return Task.FromResult(false);
        lock (set.Gate) return Task.FromResult(set.Values.Remove(Convert.ToBase64String(value)));
    }

    private static CacheKey MakeKey(string key, int db) => new(db < 0 ? 0 : db, key);

    private bool KeyExists(CacheKey cacheKey) =>
        _hashes.ContainsKey(cacheKey) ||
        _lists.ContainsKey(cacheKey) ||
        _strings.ContainsKey(cacheKey) ||
        _sortedSets.ContainsKey(cacheKey);

    private void CancelExpiration(CacheKey cacheKey)
    {
        if (_expirations.TryRemove(cacheKey, out CancellationTokenSource? cancellation))
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    private async Task ExpireKeyAsync(CacheKey cacheKey, TimeSpan expiry, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(expiry, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_expirations.TryGetValue(cacheKey, out CancellationTokenSource? current) &&
            ReferenceEquals(current, cancellation))
        {
            await KeyDeleteAsync(cacheKey.Key, cacheKey.Database);
        }
    }

    private static RedisValue[] Slice(List<RedisValue> values, int start, int end)
    {
        if (values.Count == 0) return Array.Empty<RedisValue>();

        int first = start < 0 ? values.Count + start : start;
        int last = end < 0 ? values.Count + end : end;
        first = Math.Max(first, 0);
        last = Math.Min(last, values.Count - 1);
        if (first >= values.Count || first > last) return Array.Empty<RedisValue>();
        return values.GetRange(first, last - first + 1).ToArray();
    }

    private readonly record struct CacheKey(int Database, string Key);

    private sealed class LockedList
    {
        public object Gate { get; } = new();
        public List<RedisValue> Values { get; } = [];
    }

    private sealed class LockedSortedSet
    {
        public object Gate { get; } = new();
        public Dictionary<string, (byte[] Value, double Score)> Values { get; } = new();
    }
}
