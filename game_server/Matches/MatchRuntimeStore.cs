using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.infrastructure.redis;

namespace game_server.matches;

/// <summary>
///     matchingId별 매치의 조회·초기화·등록·제거를 담당한다.
///     동시 생성 요청은 하나의 초기화 작업을 공유하며, 참가자·봇·스폰·문 구성 후 등록 알림으로 틱을 시작한다.
///     ID로 잠금 진입을 요청하면 해당 매치를 찾아 위임하며,
///     실제 잠금과 종료 정리는 MatchRuntime이 담당한다.
/// </summary>
internal sealed class MatchRuntimeStore
{
    private static long _botIdCounter;
    private readonly IRedisOperations _redisOperations;
    private readonly ConcurrentDictionary<long, Lazy<Task<MatchRuntime>>> _initializations = new();
    private readonly MatchSessionCleanupService _matchSessionCleanup;
    private readonly ILogger<MatchRuntime> _runtimeLogger;
    private readonly ConcurrentDictionary<long, MatchRuntime> _runtimes = new();

    internal event Action<MatchRuntime>? MatchCreated;

    internal MatchRuntimeStore(ILogger<MatchRuntime> runtimeLogger, MatchSessionCleanupService matchSessionCleanup, IRedisOperations redisOperations)
    {
        _redisOperations = redisOperations ?? throw new ArgumentNullException(nameof(redisOperations));
        _runtimeLogger = runtimeLogger;
        _matchSessionCleanup = matchSessionCleanup ?? throw new ArgumentNullException(nameof(matchSessionCleanup));
    }

    public int Count => _runtimes.Count;

    public async Task<MatchRuntime> GetOrCreateAsync(long matchingId, MatchManifest manifest)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(matchingId);
        ArgumentNullException.ThrowIfNull(manifest);
        var existing = GetOrNull(matchingId);
        if (existing != null)
        {
            if (existing.IsEnded)
            {
                throw new OperationCanceledException("Match became terminal during game entry.");
            }
            return existing;
        }

        var initialization = _initializations.GetOrAdd(matchingId,
            _ => new Lazy<Task<MatchRuntime>>(() => InitializeMatchAsync(matchingId, manifest)));
        try
        {
            return await initialization.Value;
        }
        finally
        {
            _initializations.TryRemove(new KeyValuePair<long, Lazy<Task<MatchRuntime>>>(matchingId, initialization));
        }
    }

    private async Task<MatchRuntime> InitializeMatchAsync(long matchingId, MatchManifest manifest)
    {
        // 조회와 초기화 작업 등록 사이에 다른 요청이 등록을 마쳤을 수 있다.
        var existing = GetOrNull(matchingId);
        if (existing != null)
        {
            return existing;
        }

        var mode = manifest.Mode;
        var humanPlayerIds = manifest.HumanPlayerIds.ToList();
        var botPlayerIds = Enumerable.Range(0, manifest.BotCount).Select(_ => Interlocked.Decrement(ref _botIdCounter)).ToList();
        List<long> participantIds = [.. humanPlayerIds, .. botPlayerIds];
        var spawnCells = MatchSpawnData.CreatePhaseRoomAssignments(matchingId, participantIds);
        var roster = new List<PlayerInfo>();

        foreach (long playerId in humanPlayerIds)
        {
            var info = await PlayerInfo.Load(_redisOperations, playerId);
            if (info == null)
            {
                _runtimeLogger.LogWarning("Match entry rejected: PlayerInfo missing ({PlayerId})", playerId);
                throw new InvalidOperationException($"PlayerInfo not found for match participant {playerId}.");
            }
            roster.Add(new PlayerInfo
            {
                PlayerId = playerId,
                Name = info.Name,
                WearItemIdList = info.WearItemIdList.ToList()
            });
        }

        var runtime = Create(matchingId);
        using (runtime.Enter())
        {
            if (botPlayerIds.Count > 0)
            {
                runtime.Bots.RegisterBots(runtime.MatchingId, botPlayerIds, spawnCells);
            }
            foreach (long botPlayerId in botPlayerIds)
            {
                var botProfile = runtime.Bots.GetPlayerProfile(botPlayerId);
                if (botProfile == null)
                {
                    throw new InvalidOperationException($"Bot {botPlayerId} was not initialized.");
                }
                roster.Add(botProfile);
            }
            runtime.Doors.Initialize();
            runtime.InitializeMatch(mode, spawnCells, roster);

            _runtimeLogger.LogInformation("Match initialized: MatchingId={MatchingId}, Participants={ParticipantCount}, Bots={BotCount}", matchingId, participantIds.Count, botPlayerIds.Count);
            return Register(runtime);
        }
    }

    // 생성한 객체는 초기화가 끝나기 전까지 조회·틱 실행 대상에 포함하지 않는다.
    public MatchRuntime Create(long matchingId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(matchingId);
        return new MatchRuntime(this, matchingId, _runtimeLogger, _matchSessionCleanup);
    }

    // 초기화를 마친 매치를 등록하고 틱 생성 알림을 보낸다.
    public MatchRuntime Register(MatchRuntime newRuntime)
    {
        long matchingId = newRuntime.MatchingId;
        using (newRuntime.Enter())
        {
            if (newRuntime.IsEnded)
            {
                throw new OperationCanceledException("Cannot register an ended match.");
            }
            var runtime = _runtimes.GetOrAdd(matchingId, newRuntime);
            if (!ReferenceEquals(runtime, newRuntime))
            {
                return runtime;
            }

            try
            {
                MatchCreated?.Invoke(newRuntime);
            }
            catch
            {
                newRuntime.TickLoop?.Stop();
                _runtimes.TryRemove(new KeyValuePair<long, MatchRuntime>(matchingId, newRuntime));
                throw;
            }

            return newRuntime;
        }
    }

    public MatchRuntime GetOrThrow(long matchingId) => GetOrNull(matchingId) ?? throw new InvalidOperationException($"Match is not available: {matchingId}");

    public MatchRuntime? GetOrNull(long matchingId) => matchingId > 0 && _runtimes.TryGetValue(matchingId, out var runtime) ? runtime : null;

    public IReadOnlyList<long> ActiveIds() => _runtimes.Keys.OrderBy(id => id).ToList();

    public bool Enter(long matchingId, out MatchLockScope scope)
    {
        var runtime = GetOrNull(matchingId);
        if (runtime == null)
        {
            scope = default;
            return false;
        }

        scope = runtime.Enter();
        return true;
    }

    public static MatchLockScope Enter(MatchRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return runtime.Enter();
    }

    public bool TryEnter(long matchingId, out MatchLockScope scope)
    {
        scope = default;
        var runtime = GetOrNull(matchingId);
        return runtime != null && runtime.TryEnter(out scope);
    }

    public bool Remove(long matchingId)
    {
        var runtime = GetOrNull(matchingId);
        if (runtime == null)
        {
            return false;
        }
        using (runtime.Enter())
        {
            if (!_runtimes.TryRemove(new KeyValuePair<long, MatchRuntime>(matchingId, runtime)))
            {
                return false;
            }
            runtime.TryMarkEnded();
            return true;
        }
    }

    internal void RemoveCompleted(MatchRuntime runtime) =>
        _runtimes.TryRemove(new KeyValuePair<long, MatchRuntime>(runtime.MatchingId, runtime));
}
