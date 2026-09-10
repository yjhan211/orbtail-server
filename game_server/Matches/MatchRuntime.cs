using game_server.bots;
using game_server.combat;
using game_server.items;
using game_server.monsters;
using game_server.field;
using game_server.logging;
using game_server.orbs;
using game_server.sessions;
using Microsoft.Extensions.Logging;
using network.common.data.models;
using network.common;

namespace game_server.matches;

/// <summary>
///     게임 한 판의 참가자 프로필·탈락 기록, 참가 세션, 봇, 전투, 아이템, 문 등 상태와 처리 객체를 소유한다.
///     같은 매치의 패킷 처리와 틱은 매치 잠금 안에서 실행하고, 서로 다른 매치는 독립적으로 처리한다.
///
///     Enter()로 잠금에 진입하며, 입장 초기화처럼 await가 필요한 작업은 EntryInitializationLock을 사용한다.
///     종료 표시 후 가장 바깥쪽 잠금 범위를 벗어나면 자원을 정리하고 Store에서 자신을 제거한다.
/// </summary>
internal sealed class MatchRuntime
{
    public bool IsEnded => Volatile.Read(ref _ended) != 0;
    public object MatchLock { get; } = new();
    public SemaphoreSlim EntryInitializationLock { get; } = new(1, 1);

    // 한 번 확정하는 시작 구성
    public bool IsSetupComplete => Volatile.Read(ref _isSetupComplete);
    public MatchMode Mode { get; private set; }
    public IReadOnlyDictionary<long, Cell> SpawnCells { get; private set; } = new Dictionary<long, Cell>();

    // 사람·봇 참가자는 연결이 끊겨도 매치 정리까지 보관한다. MatchLock으로 보호한다.
    private readonly Dictionary<long, MatchPlayer> _participants = new();
    private int _aliveCount;

    private readonly MatchRuntimeStore _runtimeStore;
    private readonly MatchSessionCleanupService _matchSessionCleanup;
    private readonly ILogger<MatchRuntime> _logger;

    private readonly HashSet<long> _readyPlayerIds = [];
    private DateTime? _entryDeadlineUtc;
    private DateTime? _startsAtUtc;

    private bool _isSetupComplete;
    private int _ended;
    private int _lockDepth;
    private bool _cleanupStarted;
    private MatchTickLoop? _tickLoop;

    internal readonly List<Action> AfterRelease = new();

    internal MatchRuntime(MatchRuntimeStore runtimeStore, long matchingId, ILogger<MatchRuntime> logger, MatchSessionCleanupService matchSessionCleanup,
        GameEventLogManager eventLogs, ILogger<MatchCombatDamageService> damageLogger)
    {
        _runtimeStore = runtimeStore;
        _logger = logger;
        _matchSessionCleanup = matchSessionCleanup;
        MatchingId = matchingId;
        SunOrbAttacks = new SunOrbAttackState(matchingId);
        Bots = new BotPlayerManager(matchingId, logger, Doors, SunOrbAttacks, eventLogs);
        AutoAttack = new AutoAttackController(matchingId);
        Inventory = new InGameInventoryManager(matchingId, logger);
        GroundItems = new GroundItemManager(matchingId);
        SummonStones = new SummonStoneManager(matchingId);
        Closures = new AreaClosureManager(matchingId, logger);
        Monsters = new SwarmMonsterDirector(matchingId, Closures, Inventory);
        CombatDamage = new MatchCombatDamageService(this, eventLogs, damageLogger);
    }

    // 매치 식별과 수명·잠금
    public long MatchingId { get; }
    public BotPlayerManager Bots { get; }
    public BotTacticalState BotTactics { get; } = new();
    internal SwarmBotTickMetrics BotTickMetrics { get; } = new();
    public SwarmMonsterDirector Monsters { get; }

    // 전투와 오브
    public AutoAttackController AutoAttack { get; }
    public MatchCombatDamageService CombatDamage { get; }
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
    public OrbVisualStateCache OrbVisualCache { get; } = new();
    public OrbRecoveryState OrbRecovery { get; } = new();
    public EventLogState EventLog { get; } = new();

    // 틱 실행과 일정
    internal MatchTickLoop? TickLoop
    {
        get => Volatile.Read(ref _tickLoop);
        set => Interlocked.Exchange(ref _tickLoop, value);
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
        foreach (var player in playerRoster)
        {
            var participant = Bots.GetBot(MatchingId, player.PlayerId)?.Player ?? new MatchPlayer { Profile = player };
            RegisterParticipant(participant);
            OrbUpgradeService.GrantStartingResources(this, player.PlayerId);
        }

        if (playerRoster.Count > 0 && playerRoster.All(player => player.PlayerId < 0))
        {
            _startsAtUtc = DateTime.UtcNow;
        }
        Volatile.Write(ref _isSetupComplete, true);
    }

    // 참가자 등록·조회·탈락 처리는 모두 MatchLock으로 보호한다.
    public void RegisterParticipant(MatchPlayer participant)
    {
        lock (MatchLock)
        {
            if (_cleanupStarted)
            {
                throw new InvalidOperationException($"Match is not available: {MatchingId}");
            }

            if (!_participants.TryAdd(participant.PlayerId, participant))
            {
                _logger.LogDebug("Match participant already registered: MatchingId={MatchingId}, PlayerId={PlayerId}", MatchingId, participant.PlayerId);
                return;
            }

            _aliveCount = _participants.Count;
            _logger.LogInformation("Match participant registered: MatchingId={MatchingId}, PlayerId={PlayerId}, Count={Count}", MatchingId, participant.PlayerId, _aliveCount);
        }
    }

    public MatchPlayer? GetParticipant(long playerId)
    {
        lock (MatchLock)
        {
            if (_cleanupStarted || !_participants.TryGetValue(playerId, out var entry))
            {
                return null;
            }

            return entry;
        }
    }

    public List<PlayerInfo> GetPlayerProfiles()
    {
        lock (MatchLock)
        {
            return _participants.Values.Select(participant => participant.Profile).ToList();
        }
    }

    public bool TryEliminatePlayer(long playerId, EliminationReason reason,
        long attackerPlayerId = 0, AreaType eliminatedArea = AreaType.None, int forcedRank = 0, int finalOrbTier = 0)
    {
        lock (MatchLock)
        {
            if (_cleanupStarted)
            {
                return false;
            }
            if (!_participants.TryGetValue(playerId, out var participant)) return false;
            if (participant.Status == PlayerMatchStatus.ELIMINATED) return false;

            participant.Status = PlayerMatchStatus.ELIMINATED;
            participant.EliminationReason = reason;
            participant.EliminatedAt = DateTime.UtcNow;
            participant.AttackerPlayerId = attackerPlayerId;
            participant.EliminatedArea = eliminatedArea;
            participant.FinalOrbTier = finalOrbTier;
            participant.EliminationRank = forcedRank > 0 ? forcedRank : _aliveCount;
            _aliveCount--;

            _logger.LogInformation("플레이어 탈락: MatchingId={MatchingId}, PlayerId={PlayerId}, 사유={Reason}, 생존={Alive}", MatchingId, playerId, reason, _aliveCount);
            return true;
        }
    }

    /// <summary>사람·봇 참가자 중 현재 연결이 있는 세션만 복사해 반환한다.</summary>
    public List<GameClientSession> GetSessions()
    {
        lock (MatchLock)
        {
            var sessions = new List<GameClientSession>();
            foreach (var player in _participants.Values)
            {
                var session = player.Session;
                if (session != null)
                    sessions.Add(session);
            }
            return sessions;
        }
    }

    public (bool isGameOver, long? winnerId) CheckGameOver()
    {
        lock (MatchLock)
        {
            if (_cleanupStarted)
                return (false, null);

            if (_participants.Count == 0)
                return (false, null);

            var activePlayers = _participants.Values
                .Where(l => l.Status != PlayerMatchStatus.ELIMINATED && l.Status != PlayerMatchStatus.SPECTATING)
                .ToList();

            if (activePlayers.Count <= 1)
            {
                long? winnerId = activePlayers.FirstOrDefault()?.PlayerId;
                return (true, winnerId);
            }

            return (false, null);
        }
    }

    public List<(long playerId,
        EliminationReason reason, PlayerMatchStatus finalStatus, DateTime? eliminatedAt,
        long attackerPlayerId, AreaType eliminatedArea, int eliminationRank, int finalOrbTier)> BuildGameResult()
    {
        lock (MatchLock)
        {
            if (_cleanupStarted)
                return new();

            var result = new List<(long, EliminationReason, PlayerMatchStatus, DateTime?, long,
                AreaType, int, int)>();

            foreach (var participant in _participants.Values)
            {
                result.Add((participant.PlayerId,
                    participant.EliminationReason, participant.Status, participant.EliminatedAt, participant.AttackerPlayerId,
                    participant.EliminatedArea,
                    participant.EliminationRank, participant.FinalOrbTier));
            }

            return result;
        }
    }


    public DateTime? EntryDeadlineUtc { get { lock (MatchLock) return _entryDeadlineUtc; } }
    public DateTime? StartsAtUtc { get { lock (MatchLock) return _startsAtUtc; } }

    public void BeginEntry(long playerId)
    {
        lock (MatchLock)
        {
            if (IsEnded || !IsSetupComplete || playerId <= 0 || GetParticipant(playerId) == null)
            {
                return;
            }
            _entryDeadlineUtc ??= DateTime.UtcNow + MatchingRedisKeys.EntryTimeout;
        }
    }

    public void MarkPlayerReady(long playerId)
    {
        lock (MatchLock)
        {
            if (IsEnded || !_entryDeadlineUtc.HasValue || playerId <= 0 || GetParticipant(playerId) == null)
            {
                return;
            }
            _readyPlayerIds.Add(playerId);
            if (GetPlayerProfiles().Any(player => player.PlayerId > 0 && !_readyPlayerIds.Contains(player.PlayerId)))
            {
                return;
            }
            _startsAtUtc ??= DateTime.UtcNow.AddSeconds(5);
        }
    }

    public bool IsGameplayActive(DateTime? utcNow = null)
    {
        lock (MatchLock)
            return !IsEnded && _startsAtUtc.HasValue && (utcNow ?? DateTime.UtcNow) >= _startsAtUtc.Value;
    }

    public bool IsEntryTimedOut(DateTime utcNow)
    {
        lock (MatchLock)
            return !IsEnded && !_startsAtUtc.HasValue && _entryDeadlineUtc.HasValue && utcNow >= _entryDeadlineUtc.Value;
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
                foreach (var player in _participants.Values)
                    player.Session = null;
                Doors.Clear();
                EventLog.Release();
                Inventory.Release();
                GroundItems.Release();
                GroundItemPickupCandidates.Clear();
                SummonStones.Release();
                Encounters.Release();
                _participants.Clear();
                _aliveCount = 0;
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
            _matchSessionCleanup.StartMatchDataCleanup(MatchingId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Match Redis cleanup could not be started: MatchingId={MatchingId}", MatchingId);
        }
    }
}
