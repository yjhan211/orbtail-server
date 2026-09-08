using game_server.services;
using Microsoft.Extensions.Logging;
using network.common.data;
using network.common.data.models;
using System.Collections.Concurrent;

namespace game_server.matches;

/// <summary>매치 구성: 누가 오는지(사람·봇 ID), 어떤 모드인지, 어디서 시작하는지. 첫 접속 때 확정한다.</summary>
internal sealed record MatchComposition(
    IReadOnlyList<long> HumanPlayerIds,
    IReadOnlyList<long> BotPlayerIds,
    MatchMode Mode,
    IReadOnlyDictionary<long, Cell> SpawnCells,
    IReadOnlyList<PlayerInfo> PlayerRoster);

/// <summary>매치 정리 단계 하나 — 실패해도 다음 단계를 막지 않도록 이름과 함께 격리 실행된다.</summary>
internal sealed record MatchCleanupStep(string Name, Action<long> Cleanup);

/// <summary>
///     <see cref="MatchRuntimeStore.Enter"/>/<see cref="MatchRuntimeStore.TryEnter"/>가 돌려주는 잠금 스코프.
///     Dispose가 깊이를 줄이고, 최외곽(깊이 0)에서 터미널이면 정리를 한 번 돌린 뒤 모니터를 놓고
///     <see cref="MatchRuntime.AfterRelease"/>를 잠금 밖에서 실행한다.
/// </summary>
internal readonly struct MatchScope : IDisposable
{
    private readonly MatchRuntimeStore _store;

    internal MatchScope(MatchRuntimeStore store, MatchRuntime runtime)
    {
        _store = store;
        Runtime = runtime;
    }

    public MatchRuntime Runtime { get; }

    public void Dispose() => _store.Exit(Runtime);
}

/// <summary>
///     프로세스 수명의 매치 런타임 색인. matchingId당 <see cref="MatchRuntime"/> 하나를 만들고(초기화 훅은
///     생성 시 한 번), 잠금 진입/탈출과 터미널 정리 순서를 책임진다. 정리는 터미널 표시 뒤 깊이 0 탈출에서
///     정확히 한 번 돌고, 그 직후 색인에서 빠져 늦은 타이머·패킷이 상태를 되살릴 수 없다.
/// </summary>
internal sealed class MatchRuntimeStore
{
    private readonly ConcurrentDictionary<long, MatchRuntime> _runtimes = new();
    // 늦은 이전 매치 메시지와 ID가 겹치지 않도록 시퀀스는 매치 밖에서 공유한다.
    private readonly SwarmGrowthOfferIdSequence _growthOfferIds = new();
    private readonly SwarmCrossfireEventIdSequence _crossfireEventIds = new();
    private readonly Action<long>? _initializeMatch;
    private readonly IReadOnlyList<MatchCleanupStep> _cleanupSteps;
    private readonly Action<long>? _afterCleanup;
    private readonly ILogger _logger;
    private readonly bool _monsterSpawnEnabled;
    private readonly MatchEventArchive? _eventArchive;

    /// <param name="initializeMatch">런타임이 처음 만들어질 때 그 모니터 안에서 한 번 실행되는 훅.</param>
    /// <param name="cleanupSteps">터미널 정리 단계 — 순서대로, 각 단계 예외는 격리·로그.</param>
    /// <param name="afterCleanup">정리가 끝난 뒤 잠금 밖에서 실행할 후처리 (Redis 정리 등).</param>
    public MatchRuntimeStore(
        ILogger logger,
        Action<long>? initializeMatch = null,
        IReadOnlyList<MatchCleanupStep>? cleanupSteps = null,
        Action<long>? afterCleanup = null,
        bool monsterSpawnEnabled = true,
        MatchEventArchive? eventArchive = null)
    {
        _logger = logger;
        _monsterSpawnEnabled = monsterSpawnEnabled;
        _eventArchive = eventArchive;
        _initializeMatch = initializeMatch;
        _cleanupSteps = cleanupSteps ?? [];
        _afterCleanup = afterCleanup;
    }

    // 런타임 초기화가 끝난 뒤 알린다. 구독자는 잠금 안에서 게임 처리를 직접 실행하지 않는다.
    internal event Action<MatchRuntime>? Created;

    public int Count => _runtimes.Count;

    /// <summary>
    ///     런타임을 찾거나 만든다. 초기화 훅은 새 런타임의 모니터를 쥔 채 정확히 한 번 실행되고,
    ///     훅이 던지면 그 런타임은 색인에서 빠진다.
    /// </summary>
    public MatchRuntime GetOrCreate(long matchingId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(matchingId);
        if (_runtimes.TryGetValue(matchingId, out MatchRuntime? existing))
            return existing;

        var candidate = new MatchRuntime(this, matchingId, _logger, _growthOfferIds, _crossfireEventIds, _monsterSpawnEnabled);
        lock (candidate.Sync)
        {
            MatchRuntime runtime = _runtimes.GetOrAdd(matchingId, candidate);
            if (!ReferenceEquals(runtime, candidate))
                return runtime;

            try
            {
                _initializeMatch?.Invoke(matchingId);
                Created?.Invoke(candidate);
            }
            catch
            {
                candidate.TickLoop?.Stop();
                _runtimes.TryRemove(new KeyValuePair<long, MatchRuntime>(matchingId, candidate));
                throw;
            }

            return candidate;
        }
    }

    /// <summary>진행 중인 처리에서 필요한 기존 매치를 찾는다. 없다고 새로 만들지는 않는다.</summary>
    public MatchRuntime GetRequired(long matchingId) =>
        Get(matchingId) ?? throw new InvalidOperationException($"Match is not available: {matchingId}");

    public MatchRuntime? Get(long matchingId) =>
        matchingId > 0 && _runtimes.TryGetValue(matchingId, out MatchRuntime? runtime) ? runtime : null;

    /// <summary>색인에 남아 있는 매치 id 스냅샷 — 정리가 끝난 매치는 포함되지 않는다.</summary>
    public IReadOnlyList<long> ActiveIds() => _runtimes.Keys.OrderBy(id => id).ToList();

    /// <summary>모니터를 기다려 진입한다 (재진입 가능). 색인에 없는 매치면 false.</summary>
    public bool Enter(long matchingId, out MatchScope scope)
    {
        MatchRuntime? runtime = Get(matchingId);
        if (runtime == null)
        {
            scope = default;
            return false;
        }

        scope = Enter(runtime);
        return true;
    }

    /// <summary>이미 잡아 둔 런타임으로 진입한다 — 종료 경로가 다른 스코프 안에서 재진입할 때 쓴다.</summary>
    public MatchScope Enter(MatchRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        Monitor.Enter(runtime.Sync);
        runtime.Depth++;
        return new MatchScope(this, runtime);
    }

    /// <summary>
    ///     기다리지 않고 진입을 시도한다. false면 이 펄스는 건너뛴다 — 밀린 틱을 나중에 따라잡지 않는다.
    /// </summary>
    public bool TryEnter(long matchingId, out MatchScope scope)
    {
        scope = default;
        MatchRuntime? runtime = Get(matchingId);
        if (runtime == null)
            return false;

        bool lockTaken = false;
        Monitor.TryEnter(runtime.Sync, ref lockTaken);
        if (!lockTaken)
            return false;

        runtime.Depth++;
        scope = new MatchScope(this, runtime);
        return true;
    }

    public bool Remove(long matchingId)
    {
        var runtime = Get(matchingId);
        if (runtime == null)
            return false;
        lock (runtime.Sync)
        {
            if (!_runtimes.TryRemove(new KeyValuePair<long, MatchRuntime>(matchingId, runtime)))
                return false;
            runtime.TickLoop?.Stop();
            runtime.Sessions.Close();
            return true;
        }
    }

    internal void Exit(MatchRuntime runtime)
    {
        Action[]? afterRelease = null;
        try
        {
            runtime.Depth--;
            if (runtime.Depth > 0)
                return;

            if (runtime.IsTerminal && !runtime.CleanupDone)
            {
                // 같은 잠금을 쓰는 틱의 게임 처리는 여기까지 끝났다. 자기 루프를 await하지 않고
                // 다음 틱을 막은 뒤 정리한다. 서버 종료는 별도로 루프 Completion까지 기다린다.
                runtime.TickLoop?.Stop();
                runtime.CleanupDone = true;
                runtime.Sessions.Close();
                runtime.Doors.Clear();
                RunCleanup(runtime.MatchingId);
                try
                {
                    _eventArchive?.Archive(runtime.MatchingId, runtime.EventLog);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Match event log archive failed: MatchingId={MatchingId}", runtime.MatchingId);
                }
                runtime.Inventory.Release();
                runtime.GroundItems.Release();
                runtime.GroundItemPickupCandidates.Clear();
                runtime.SummonStones.Release();
                runtime.Encounters.Release();
                runtime.Roster.Release();
                runtime.Closures.Release();
                runtime.Bots.Release();
                runtime.Monsters.Release();
                runtime.Combat.Release();

                _runtimes.TryRemove(new KeyValuePair<long, MatchRuntime>(runtime.MatchingId, runtime));
                if (_afterCleanup != null)
                {
                    Action<long> afterCleanup = _afterCleanup;
                    long matchingId = runtime.MatchingId;
                    runtime.AfterRelease.Add(() => afterCleanup(matchingId));
                }
            }

            if (runtime.AfterRelease.Count == 0)
                return;

            afterRelease = runtime.AfterRelease.ToArray();
            runtime.AfterRelease.Clear();
        }
        finally
        {
            Monitor.Exit(runtime.Sync);
        }

        foreach (Action action in afterRelease)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Match post-release action failed: MatchingId={MatchingId}",
                    runtime.MatchingId);
            }
        }
    }

    private void RunCleanup(long matchingId)
    {
        foreach (MatchCleanupStep step in _cleanupSteps)
        {
            try
            {
                step.Cleanup(matchingId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Match component cleanup failed: MatchingId={MatchingId}, Component={Component}",
                    matchingId,
                    step.Name);
            }
        }
    }
}
