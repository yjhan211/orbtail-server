using game_server.services;
using game_server.sessions;
using Microsoft.Extensions.Logging;
using network.common.data;
using network.common.data.models;

namespace game_server.matches;

/// <summary>
///     매치 하나의 직렬화 경계. 같은 matchingId의 권위 상태 변경은 전부 <see cref="Sync"/> 모니터 안에서
///     돌고(타이머 틱·세션 핸들러·종료), 서로 다른 매치는 병렬이다. 수명은 <see cref="MatchRuntimeStore"/>가
///     소유한다 — 생성은 접속/봇 전용 인스턴스, 제거는 터미널 정리가 끝난 최외곽 스코프 탈출 시점.
///     <see cref="IsTerminal"/>은 Sync 안에서만 바뀌고 잠금 밖 읽기는 늦은 패킷을 거르는 게이트로만 쓴다.
/// </summary>
internal sealed class MatchRuntime
{
    private readonly MatchRuntimeStore _owner;

    /// <summary>이 매치의 잠금에 진입한다. 마지막 스코프가 끝날 때 Store가 종료 정리를 수행한다.</summary>
    public MatchScope Enter() => _owner.Enter(this);

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

    internal MatchRuntime(MatchRuntimeStore owner, long matchingId, ILogger logger,
        SwarmGrowthOfferIdSequence? growthOfferIds = null,
        SwarmCrossfireEventIdSequence? crossfireEventIds = null,
        bool monsterSpawnEnabled = true)
    {
        _owner = owner;
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
    public MatchSessionCollection Sessions { get; } = new();
    public MatchDoorState Doors { get; } = new();
    /// <summary>이 매치의 오브 전투·성장·봇 전술 상태. 매치와 함께 생성되고 제거된다.</summary>
    public SwarmMatchRuntime Swarm { get; }
    public BotPlayerManager Bots { get; }
    public ProximityAutoCombatResolver Combat { get; }

    public SwarmBotMovementCoordinator BotMovement { get; }
    public SwarmMonsterDirector Monsters { get; }
    public MatchEventLogState EventLog { get; } = new();
    // 데이터와 처리 객체를 함께 소유한다. 호출자는 이 매치를 고른 뒤 playerId만 넘긴다.
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

    // await를 포함하는 입장 초기화만 직렬화한다. Sync 안에서는 이 잠금을 기다리지 않는다.
    // 종료 중 기다리는 작업이 있을 수 있으므로 Dispose하지 않고 런타임과 함께 회수된다.
    public SemaphoreSlim EntryInitializationLock { get; } = new(1, 1);
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
