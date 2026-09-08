using game_server.matches.states;
using game_server.services;
using game_server.sessions;
using Microsoft.Extensions.Logging;
using network.common.data.models;

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
    private readonly MatchRuntimeStore _runtimeStore;
    private readonly MatchingLifecycleService _matchingLifecycle;
    private readonly ILogger<MatchRuntime> _logger;

    private bool _isSetupComplete;
    private int _ended;
    private int _lockDepth;
    private bool _cleanupStarted;
    private MatchTickLoop? _tickLoop;

    internal readonly List<Action> AfterRelease = new();

    internal MatchRuntime(MatchRuntimeStore runtimeStore, long matchingId, ILogger<MatchRuntime> logger, MatchingLifecycleService matchingLifecycle)
    {
        _runtimeStore = runtimeStore;
        _logger = logger;
        _matchingLifecycle = matchingLifecycle;
        MatchingId = matchingId;
        TickSchedule = new MatchTickSchedule(MatchLock);
        SunOrbAttacks = new SunOrbAttackState(matchingId);
        Bots = new BotPlayerManager(matchingId, logger, Doors, SunOrbAttacks);
        AutoAttack = new AutoAttackController(matchingId);
        Inventory = new InGameInventoryManager(matchingId, logger);
        GroundItems = new GroundItemManager(matchingId);
        Roster = new RosterManager(matchingId, logger);
        SummonStones = new SummonStoneManager(matchingId);
        Closures = new AreaClosureManager(matchingId, logger);
        Monsters = new SwarmMonsterDirector(matchingId, Closures, Inventory);
    }

    // 매치 식별과 수명·잠금
    public long MatchingId { get; }
    public bool IsEnded => Volatile.Read(ref _ended) != 0;
    public object MatchLock { get; } = new();
    public SemaphoreSlim EntryInitializationLock { get; } = new(1, 1);

    // 한 번 확정하는 시작 구성
    public bool IsSetupComplete => Volatile.Read(ref _isSetupComplete);
    public MatchMode Mode { get; private set; }
    public IReadOnlyDictionary<long, Cell> SpawnCells { get; private set; } = new Dictionary<long, Cell>();
    public IReadOnlyList<PlayerInfo> PlayerRoster { get; private set; } = [];

    // 참가자
    public MatchSessionCollection Sessions { get; } = new();
    public RosterManager Roster { get; }
    public BotPlayerManager Bots { get; }
    public BotTacticalState BotTactics { get; } = new();
    internal SwarmBotTickMetrics BotTickMetrics { get; } = new();
    public SwarmMonsterDirector Monsters { get; }

    // 전투와 오브
    public AutoAttackController AutoAttack { get; }
    public TrailCombatState TrailCombat { get; } = new();
    public SunOrbAttackState SunOrbAttacks { get; }
    public WindOrbAttackState WindOrbAttacks { get; } = new();
    public OrbUpgradeState OrbUpgrades { get; } = new();

    // 아이템과 재화
    public InGameInventoryManager Inventory { get; }
    public GroundItemManager GroundItems { get; }
    internal Dictionary<GameClientSession, GroundItemPickupCandidates> GroundItemPickupCandidates { get; } = new();
    public SummonStoneManager SummonStones { get; }

    // 맵과 진행 상태
    public DoorState Doors { get; } = new();
    public AreaClosureManager Closures { get; }
    public EncounterRevealManager Encounters { get; } = new();
    public MatchProgressState Progress { get; } = new();
    public PresentationState Presentation { get; } = new();
    public EventLogState EventLog { get; } = new();

    // 틱 실행과 일정
    public MatchTickSchedule TickSchedule { get; }
    internal MatchTickLoop? TickLoop
    {
        get => Volatile.Read(ref _tickLoop);
        set => Volatile.Write(ref _tickLoop, value);
    }

    public void InitializeMatch(MatchMode mode, IReadOnlyDictionary<long, Cell> spawnCells, IReadOnlyList<PlayerInfo> playerRoster)
    {
        ArgumentNullException.ThrowIfNull(spawnCells);
        ArgumentNullException.ThrowIfNull(playerRoster);
        if (!Monitor.IsEntered(MatchLock))
        {
            throw new InvalidOperationException("Setting match setup requires the match lock.");
        }

        if (IsEnded)
        {
            throw new InvalidOperationException("Cannot set up an ended match.");
        }

        if (IsSetupComplete)
        {
            throw new InvalidOperationException("Match setup is already registered.");
        }

        Mode = mode;
        SpawnCells = spawnCells;
        PlayerRoster = playerRoster;
        Volatile.Write(ref _isSetupComplete, true);
    }

    public MatchLockScope Enter()
    {
        Monitor.Enter(MatchLock);
        _lockDepth++;
        return new MatchLockScope(this);
    }

    public bool TryEnter(out MatchLockScope scope)
    {
        scope = default;
        bool lockTaken = false;
        Monitor.TryEnter(MatchLock, ref lockTaken);
        if (!lockTaken)
        {
            return false;
        }
        _lockDepth++;
        scope = new MatchLockScope(this);
        return true;
    }

    public bool TryMarkEnded()
    {
        if (!Monitor.IsEntered(MatchLock))
        {
            throw new InvalidOperationException("TryMarkEnded requires the match monitor to be held.");
        }

        return Interlocked.CompareExchange(ref _ended, 1, 0) == 0;
    }

    internal void Exit()
    {
        Action[] afterRelease;
        bool startRedisCleanup = false;
        try
        {
            _lockDepth--;
            if (_lockDepth > 0)
            {
                return;
            }

            if (IsEnded && !_cleanupStarted)
            {
                _cleanupStarted = true;
                TickLoop?.Stop();
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
                EventLog.Release();
                Inventory.Release();
                GroundItems.Release();
                GroundItemPickupCandidates.Clear();
                SummonStones.Release();
                Encounters.Release();
                Roster.Release();
                Closures.Release();
                Bots.Release();
                Monsters.Release();
                AutoAttack.Release();
                _runtimeStore.RemoveCompleted(this);
                startRedisCleanup = true;
            }

            if (AfterRelease.Count == 0 && !startRedisCleanup)
            {
                return;
            }

            afterRelease = AfterRelease.ToArray();
            AfterRelease.Clear();
        }
        finally
        {
            Monitor.Exit(MatchLock);
        }

        foreach (var action in afterRelease)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Match post-release action failed: MatchingId={MatchingId}", MatchingId);
            }
        }

        if (!startRedisCleanup)
        {
            return;
        }

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
