using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
///     매치 하나의 직렬화 경계. 같은 matchingId의 권위 상태 변경은 전부 <see cref="Sync"/> 모니터 안에서
///     돌고(타이머 틱·세션 핸들러·종료), 서로 다른 매치는 병렬이다. 수명은 <see cref="MatchRuntimeStore"/>가
///     소유한다 — 생성은 접속/봇 전용 인스턴스, 제거는 터미널 정리가 끝난 최외곽 스코프 탈출 시점.
///     <see cref="IsTerminal"/>은 Sync 안에서만 바뀌고 잠금 밖 읽기는 늦은 패킷을 거르는 게이트로만 쓴다.
/// </summary>
internal sealed class MatchRuntime
{
    public const int EnvironmentalTickIntervalSeconds = 5;
    private int _terminal;
    private MatchTickLoop? _tickLoop;
    internal MatchTickLoop? TickLoop
    {
        get => Volatile.Read(ref _tickLoop);
        set => Volatile.Write(ref _tickLoop, value);
    }

    public DateTime? NextAreaClosureTickAtUtc { get; private set; }

    /// <summary>매치 잠금 안에서 구역 폐쇄를 1초마다 실행한다. 놓친 구간을 몰아서 처리하지 않는다.</summary>
    public bool TryBeginAreaClosureTick(DateTime utcNow, DateTime? gameplayStartedAtUtc)
    {
        if (!Monitor.IsEntered(Sync))
            throw new InvalidOperationException("Area closure tick requires the match monitor to be held.");
        if (IsTerminal || gameplayStartedAtUtc is not { } startedAt || utcNow < startedAt.AddSeconds(1))
            return false;
        if (NextAreaClosureTickAtUtc is { } next && utcNow < next)
            return false;
        NextAreaClosureTickAtUtc = utcNow.AddSeconds(1);
        return true;
    }

    internal MatchRuntime(long matchingId, ILogger logger,
        SwarmGrowthOfferIdSequence? growthOfferIds = null,
        SwarmCrossfireEventIdSequence? crossfireEventIds = null,
        bool monsterSpawnEnabled = true)
    {
        MatchingId = matchingId;
        Bots = new BotPlayerManager(matchingId, logger);
        Combat = new ProximityAutoCombatResolver(matchingId);
        BotMovement = new SwarmBotMovementCoordinator(this);
        Swarm = new SwarmMatchRuntime(matchingId,
            growthOfferIds ?? new SwarmGrowthOfferIdSequence(),
            crossfireEventIds ?? new SwarmCrossfireEventIdSequence());
        Bots.SetDoorOpenResolver((_, doorId) => Doors.IsDoorOpen(doorId));
        Bots.SetSwarmDodgeResolver((id, botId, position, area, now) =>
            SwarmBotDodgePolicy.ResolveSwarmBotDodgeDirection(
                Swarm.Crossfire.DodgeSnapshot, id, botId, position, area, now));
        Inventory = new InGameInventoryManager(matchingId, message => logger.LogInformation("{Message}", message));
        GroundItems = new GroundItemManager(matchingId);
        Roster = new MatchRosterManager(matchingId, logger);
        SummonStones = new SummonStoneManager(matchingId);
        Closures = new AreaClosureManager(matchingId, logger);
        Monsters = new SwarmMonsterDirector(matchingId, monsterSpawnEnabled: monsterSpawnEnabled)
        {
            IsAreaClosedResolver = (_, area) => Closures.IsAreaClosed(area),
            IsGameplayActiveResolver = _ => MatchStartGate.IsGameplayActive(MatchingId),
            IsPlayerOrblessResolver = (_, playerId) => !HasAnySquadOrb(playerId)
        };
    }

    public long MatchingId { get; }
    public MatchDoorState Doors { get; } = new();
    /// <summary>이 매치의 오브 전투·성장·봇 전술 상태. 매치와 함께 생성되고 제거된다.</summary>
    public SwarmMatchRuntime Swarm { get; }
    public BotPlayerManager Bots { get; }
    public ProximityAutoCombatResolver Combat { get; }
    public RngCollectCooldownStore CollectCooldowns { get; } = new();
    public SwarmBotMovementCoordinator BotMovement { get; }
    public SwarmMonsterDirector Monsters { get; }
    public MatchEventLogState EventLog { get; } = new();
    // 데이터와 처리 객체를 함께 소유한다. 호출자는 이 매치를 고른 뒤 playerId만 넘긴다.
    public InGameInventoryManager Inventory { get; }
    public GroundItemManager GroundItems { get; }
    public SummonStoneManager SummonStones { get; }
    public EncounterRevealManager Encounters { get; } = new();
    public MatchRosterManager Roster { get; }
    public MatchPresentationState Presentation { get; } = new();
    public AreaClosureManager Closures { get; }
    public object Sync { get; } = new();
    public bool IsTerminal => Volatile.Read(ref _terminal) != 0;

    /// <summary>이 매치의 인벤토리에 수량이 남은 공격·회복 오브가 있는지 확인한다.</summary>
    public bool HasAnySquadOrb(long playerId)
    {
        return Inventory.GetPlayerInventory(playerId).GetAllItems().Any(item =>
            item.Count > 0 &&
            (OrbData.TryGetColorAndTier(item.ItemId, out _, out int tier)
                ? tier > 0
                : OrbData.TryGetRecoveryTier(item.ItemId, out int recoveryTier) && recoveryTier > 0));
    }

    /// <summary>이 매치의 보유 오브 수량과 티어 합계를 계산한다. 호출자는 매치 잠금을 보유한다.</summary>
    public (int OrbCount, int TierSum) GetOrbScore(long playerId)
    {
        int orbCount = 0;
        int tierSum = 0;
        foreach (var item in Inventory.GetPlayerInventory(playerId).GetAllItems())
        {
            int tier = OrbData.TryGetColorAndTier(item.ItemId, out _, out int attackTier)
                ? attackTier
                : OrbData.TryGetRecoveryTier(item.ItemId, out int recoveryTier) ? recoveryTier : 0;
            if (item.Count <= 0 || tier <= 0)
                continue;
            orbCount += item.Count;
            tierSum += tier * item.Count;
        }
        return (orbCount, tierSum);
    }
    public DateTime? NextEnvironmentalTickAtUtc { get; private set; }

    /// <summary>
    ///     매치 시작 기준 5초마다 환경 정산을 한 번 허용한다. 반드시 매치 잠금 안에서 호출한다.
    ///     지연된 구간은 몰아서 정산하지 않고 다음 5초 경계로 건너뛴다.
    ///     실행 전에 시각을 넘겨 예외가 나더라도 매 50ms마다 같은 정산을 반복하지 않는다.
    /// </summary>
    public bool TryBeginEnvironmentalTick(DateTime utcNow, DateTime? gameplayStartedAtUtc)
    {
        if (!Monitor.IsEntered(Sync))
            throw new InvalidOperationException("Environmental tick requires the match monitor to be held.");
        if (IsTerminal || gameplayStartedAtUtc is not { } startedAt || utcNow < startedAt)
            return false;

        NextEnvironmentalTickAtUtc ??= startedAt.AddSeconds(EnvironmentalTickIntervalSeconds);
        if (utcNow < NextEnvironmentalTickAtUtc.Value)
            return false;

        long intervalTicks = TimeSpan.TicksPerSecond * EnvironmentalTickIntervalSeconds;
        long nextInterval = (utcNow.Ticks - startedAt.Ticks) / intervalTicks + 1;
        NextEnvironmentalTickAtUtc = startedAt.AddTicks(nextInterval * intervalTicks);
        return true;
    }

    private MatchComposition? _composition;

    /// <summary>
    ///     매치 구성 — 사람 ID·모드와 GameServer가 만든 봇 ID·스폰·최종 명단. 매치 초기화 잠금 안에서 한 번 세우고
    ///     이후 사람 세션은 읽기만 한다. 개발 모드도 프로세스 전역이 아니라 이 매치 구성에 고정된다.
    /// </summary>
    public MatchComposition? Composition
    {
        get => Volatile.Read(ref _composition);
        set => Volatile.Write(ref _composition, value);
    }

    /// <summary>재진입 깊이 — Sync 안에서만 읽고 쓴다. 0으로 돌아오는 순간이 정리·후처리 시점이다.</summary>
    internal int Depth;
    internal bool CleanupDone;

    /// <summary>
    ///     최외곽 스코프가 잠금을 놓은 뒤 한 번만 실행할 후처리 (요약 파일 쓰기·NATS 발행·Redis 정리).
    ///     Sync 안에서만 추가한다.
    /// </summary>
    internal readonly List<Action> AfterRelease = new();

    /// <summary>터미널 전이 — Sync를 쥔 호출자만 부를 수 있고 첫 호출자만 true를 받는다.</summary>
    public bool TryMarkTerminal()
    {
        if (!Monitor.IsEntered(Sync))
            throw new InvalidOperationException("TryMarkTerminal requires the match monitor to be held.");

        return Interlocked.CompareExchange(ref _terminal, 1, 0) == 0;
    }
}

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

        var candidate = new MatchRuntime(matchingId, _logger, _growthOfferIds, _crossfireEventIds, _monsterSpawnEnabled);
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
                runtime.SummonStones.Release();
                runtime.Encounters.Release();
                runtime.Roster.Release();
                runtime.Closures.Release();
                runtime.Bots.Release();
                runtime.Monsters.Release();
                runtime.Combat.Release();
                runtime.CollectCooldowns.Release();
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
