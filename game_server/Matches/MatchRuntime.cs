using game_server.services;
using game_server.sessions;
using Microsoft.Extensions.Logging;
using network.common.data;

namespace game_server.matches;

/// <summary>
///     게임 한 판의 참가 세션, 봇, 전투, 아이템, 문 등 상태와 처리 객체를 소유한다.
///     같은 매치의 패킷 처리와 틱은 매치 잠금 안에서 실행하고, 서로 다른 매치는 독립적으로 처리한다.
///
///     Enter()로 잠금에 진입하며, 입장 초기화처럼 await가 필요한 작업은 EntryInitializationLock을 사용한다.
///     종료 표시 후 가장 바깥쪽 잠금 범위를 벗어나면 자원을 정리하고 Store에서 자신을 제거한다.
/// </summary>
internal sealed class MatchRuntime
{
    public const int EnvironmentalTickIntervalSeconds = 5;

    private readonly MatchRuntimeStore _runtimeStore;
    private readonly ILogger _logger;
    private readonly MatchingLifecycleService _matchingLifecycle;
    private readonly MatchEventArchive? _eventArchive;
    private int _terminal;
    private MatchTickLoop? _tickLoop;
    private MatchComposition? _composition;

    private int _lockDepth;
    private bool _cleanupDone;
    internal readonly List<Action> AfterRelease = new();

    internal MatchRuntime(MatchRuntimeStore runtimeStore, long matchingId, ILogger logger,
        MatchingLifecycleService matchingLifecycle,
        SwarmGrowthOfferIdSequence? growthOfferIds = null,
        SwarmCrossfireEventIdSequence? crossfireEventIds = null,
        bool monsterSpawnEnabled = true,
        MatchEventArchive? eventArchive = null)
    {
        _runtimeStore = runtimeStore;
        _logger = logger;
        _matchingLifecycle = matchingLifecycle ?? throw new ArgumentNullException(nameof(matchingLifecycle));
        _eventArchive = eventArchive;
        MatchingId = matchingId;
        Bots = new BotPlayerManager(matchingId, logger);
        Combat = new ProximityAutoCombatResolver(matchingId);
        BotMovement = new SwarmBotMovementCoordinator(this);
        Swarm = new SwarmMatchRuntime(matchingId, growthOfferIds ?? new SwarmGrowthOfferIdSequence(), crossfireEventIds ?? new SwarmCrossfireEventIdSequence());
        Bots.SetDoorOpenResolver((_, doorId) => Doors.IsDoorOpen(doorId));
        Bots.SetSwarmDodgeResolver((id, botId, position, area, now) =>
            SwarmBotDodgePolicy.ResolveSwarmBotDodgeDirection(Swarm.Crossfire.DodgeSnapshot, id, botId, position, area, now));
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
    public MatchSessionCollection Sessions { get; } = new();
    public MatchDoorState Doors { get; } = new();
    /// <summary>이 매치의 오브 전투·성장·봇 전술 상태. 매치와 함께 생성되고 제거된다.</summary>
    public SwarmMatchRuntime Swarm { get; }
    public BotPlayerManager Bots { get; }
    public ProximityAutoCombatResolver Combat { get; }
    public SwarmBotMovementCoordinator BotMovement { get; }
    public SwarmMonsterDirector Monsters { get; }
    public MatchEventLogState EventLog { get; } = new();
    public InGameInventoryManager Inventory { get; }
    public GroundItemManager GroundItems { get; }
    internal Dictionary<GameClientSession, GroundItemPickupCandidates> GroundItemPickupCandidates { get; } = new();
    public SummonStoneManager SummonStones { get; }
    public EncounterRevealManager Encounters { get; } = new();
    public MatchRosterManager Roster { get; }
    public MatchPresentationState Presentation { get; } = new();
    public AreaClosureManager Closures { get; }
    public object Sync { get; } = new();
    public bool IsTerminal => Volatile.Read(ref _terminal) != 0;
    public SemaphoreSlim EntryInitializationLock { get; } = new(1, 1);

    /// <summary>
    ///     매치 구성 — 사람 ID·모드와 GameServer가 만든 봇 ID·스폰·최종 명단. 매치 초기화 잠금 안에서 한 번 세우고
    ///     이후 사람 세션은 읽기만 한다. 개발 모드도 프로세스 전역이 아니라 이 매치 구성에 고정된다.
    /// </summary>
    public MatchComposition? Composition
    {
        get => Volatile.Read(ref _composition);
        set => Volatile.Write(ref _composition, value);
    }

    internal MatchTickLoop? TickLoop
    {
        get => Volatile.Read(ref _tickLoop);
        set => Volatile.Write(ref _tickLoop, value);
    }

    public DateTime? NextAreaClosureTickAtUtc { get; private set; }
    public DateTime? NextEnvironmentalTickAtUtc { get; private set; }

    /// <summary>이 매치의 잠금을 잡는다. 스코프가 끝나면 잠금을 해제하고 필요한 종료 정리를 수행한다.</summary>
    public MatchScope Enter()
    {
        Monitor.Enter(Sync);
        _lockDepth++;
        return new MatchScope(this);
    }

    /// <summary>기다리지 않고 매치 잠금을 잡는다. 다른 스레드가 사용 중이면 false를 반환한다.</summary>
    public bool TryEnter(out MatchScope scope)
    {
        scope = default;
        bool lockTaken = false;
        Monitor.TryEnter(Sync, ref lockTaken);
        if (!lockTaken)
            return false;

        _lockDepth++;
        scope = new MatchScope(this);
        return true;
    }

    public bool TryBeginAreaClosureTick(DateTime utcNow, DateTime? gameplayStartedAtUtc)
    {
        if (!Monitor.IsEntered(Sync))
        {
            throw new InvalidOperationException("Area closure tick requires the match monitor to be held.");
        }

        if (IsTerminal || gameplayStartedAtUtc is not { } startedAt || utcNow < startedAt.AddSeconds(1))
        {
            return false;
        }

        if (NextAreaClosureTickAtUtc is { } next && utcNow < next)
        {
            return false;
        }
        NextAreaClosureTickAtUtc = utcNow.AddSeconds(1);
        return true;
    }

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

    /// <summary>터미널 전이 — Sync를 쥔 호출자만 부를 수 있고 첫 호출자만 true를 받는다.</summary>
    public bool TryMarkTerminal()
    {
        if (!Monitor.IsEntered(Sync))
            throw new InvalidOperationException("TryMarkTerminal requires the match monitor to be held.");

        return Interlocked.CompareExchange(ref _terminal, 1, 0) == 0;
    }

    internal void Exit()
    {
        Action[] afterRelease = [];
        bool startRedisCleanup = false;
        try
        {
            _lockDepth--;
            if (_lockDepth > 0)
                return;

            if (IsTerminal && !_cleanupDone)
            {
                // 같은 잠금을 쓰는 틱의 게임 처리는 여기까지 끝났다. 자기 루프를 await하지 않고
                // 다음 틱을 막은 뒤 정리한다. 서버 종료는 별도로 루프 Completion까지 기다린다.
                TickLoop?.Stop();
                _cleanupDone = true;
                Sessions.Close();
                Doors.Clear();
                try
                {
                    MatchStartGate.RemoveMatching(MatchingId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Match start state cleanup failed: MatchingId={MatchingId}", MatchingId);
                }
                try
                {
                    _eventArchive?.Archive(MatchingId, EventLog);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Match event log archive failed: MatchingId={MatchingId}", MatchingId);
                }
                Inventory.Release();
                GroundItems.Release();
                GroundItemPickupCandidates.Clear();
                SummonStones.Release();
                Encounters.Release();
                Roster.Release();
                Closures.Release();
                Bots.Release();
                Monsters.Release();
                Combat.Release();

                _runtimeStore.RemoveCompleted(this);
                startRedisCleanup = true;
            }

            if (AfterRelease.Count == 0 && !startRedisCleanup)
                return;

            afterRelease = AfterRelease.ToArray();
            AfterRelease.Clear();
        }
        finally
        {
            Monitor.Exit(Sync);
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
                    MatchingId);
            }
        }
        if (startRedisCleanup)
        {
            try
            {
                _matchingLifecycle.StartRedisCleanup(MatchingId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Match Redis cleanup could not be started: MatchingId={MatchingId}", MatchingId);
            }
        }
    }
}
