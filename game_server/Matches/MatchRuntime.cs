using game_server.matches.monsters;
using game_server.players;
using game_server.players.bots;
using game_server.sessions;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;

namespace game_server.matches;

/// <summary>
///     게임 한 판의 참가자 프로필·탈락 기록, 참가 세션, 봇, 전투, 아이템, 문 등 상태와 처리 객체를 소유한다.
///     같은 매치의 패킷 처리와 틱은 매치 잠금 안에서 실행하고, 서로 다른 매치는 독립적으로 처리한다.
///
///     Enter()로 잠금에 진입한다. 비동기 매치 초기화는 Store가 등록 전에 완료한다.
///     종료 표시 후 가장 바깥쪽 잠금 범위를 벗어나면 자원을 정리하고 Store에서 자신을 제거한다.
/// </summary>
internal sealed class MatchRuntime
{
    // 사망·회수 상태는 기존 스냅샷으로 즉시 알리고, 실행 목록에는 남기지 않는다.
    internal void RemoveMonster(Monster monster)
    {
        if (!Monitor.IsEntered(MatchLock))
        {
            throw new InvalidOperationException("Monster removal requires the match lock.");
        }
        if (!Monsters.Entities.TryGetValue(monster.MonsterId, out var current) ||
            !ReferenceEquals(current, monster))
        {
            return;
        }

        monster.Alive = false;
        Monsters.Entities.Remove(monster.MonsterId);
        var states = new Dictionary<AreaType, List<MonsterInfo>>
        {
            [monster.Area] = [monster.ToMonsterInfo()]
        };
        foreach (var session in GetSessions())
        {
            session.SendMonsterSnapshot(states, fullSnapshot: false);
        }
    }
    internal Dictionary<(ObjectType Type, long Id), MatchObjectSnapshot> SynchronizedObjects { get; } = new();
    internal Dictionary<long, PlayerState> SynchronizedPlayerStates { get; } = new();
    internal Queue<(GameClientSession Session, G_TO_C_COMBAT_HIT Hit)> PendingCombatHits { get; } = new();

    public bool IsEnded => Volatile.Read(ref _ended) != 0;
    public object MatchLock { get; } = new();

    public bool IsSetupComplete => Volatile.Read(ref _isSetupComplete);
    public MatchMode Mode { get; private set; }
    public IReadOnlyDictionary<long, Cell> SpawnCells { get; private set; } = new Dictionary<long, Cell>();

    private readonly Dictionary<long, Player> _players = new();
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

    internal MatchRuntime(MatchRuntimeStore runtimeStore, long matchingId, ILogger<MatchRuntime> logger, MatchSessionCleanupService matchSessionCleanup)
    {
        _runtimeStore = runtimeStore;
        _logger = logger;
        _matchSessionCleanup = matchSessionCleanup;
        MatchingId = matchingId;
        Bots = new MatchBots(logger);
        GroundItems = new MatchGroundItemState();
        Closures = new MatchAreaClosureState();
        Monsters = new MatchMonsters();
    }

    // 매치 식별과 수명·잠금
    public long MatchingId { get; }
    public MatchBots Bots { get; }
    public MatchMonsters Monsters { get; }

    // 전투와 오브
    public MatchCombatDamageState CombatDamage { get; } = new();
    public Dictionary<(long CutterId, long VictimId), CutRetaliationWindow> CutRetaliationWindows { get; } = new();
    public List<SwarmCrossfireShape> SunCrossfireShapes { get; } = new();
    public List<PendingWaveAttack> PendingWaveAttacks { get; } = new();

    // 아이템과 재화
    public MatchGroundItemState GroundItems { get; }

    // 맵과 진행 상태
    public MatchDoorState Doors { get; } = new();
    public MatchAreaClosureState Closures { get; }

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
            var participant = Bots.GetBot(player.PlayerId)?.Player ?? new Player(player);
            RegisterPlayer(participant);
            PlayerOrbGrowthService.GrantStartingSummonStones(this, participant);
        }

        var initializedAtUtc = DateTime.UtcNow;
        Monsters.Initialize(initializedAtUtc);
        if (playerRoster.Count > 0 && playerRoster.All(player => player.PlayerId < 0))
        {
            _startsAtUtc = initializedAtUtc;
        }
        Volatile.Write(ref _isSetupComplete, true);
    }

    public void RegisterPlayer(Player participant)
    {
        using (Enter())
        {
            if (_cleanupStarted)
            {
                throw new InvalidOperationException($"Match is not available: {MatchingId}");
            }

            if (!_players.TryAdd(participant.PlayerId, participant))
            {
                _logger.LogDebug("Match participant already registered: MatchingId={MatchingId}, PlayerId={PlayerId}", MatchingId, participant.PlayerId);
                return;
            }

            SynchronizedPlayerStates[participant.PlayerId] = participant.State;
            _aliveCount = _players.Count;
            _logger.LogInformation("Match participant registered: MatchingId={MatchingId}, PlayerId={PlayerId}, Count={Count}", MatchingId, participant.PlayerId, _aliveCount);
        }
    }

    public PlayerOrbState GetOrbs(long playerId) => GetPlayer(playerId)?.Orbs ?? new PlayerOrbState();

    public Player? GetPlayer(long playerId)
    {
        using (Enter())
        {
            if (_cleanupStarted || !_players.TryGetValue(playerId, out var entry))
            {
                return null;
            }

            return entry;
        }
    }

    public List<Player> GetAlivePlayers()
    {
        using (Enter())
        {
            return _players.Values.Where(player => !player.IsEliminated).ToList();
        }
    }

    public List<Player> GetPlayers()
    {
        using (Enter())
        {
            return _players.Values.ToList();
        }
    }

    public List<PlayerInfo> GetPlayerProfiles()
    {
        using (Enter())
        {
            // 로스터 전달용 복사본만 만든다. 계정 프로필은 매치에서 보관하지 않는다.
            var profiles = new List<PlayerInfo>(_players.Count);
            foreach (var participant in _players.Values)
            {
                profiles.Add(new PlayerInfo
                {
                    PlayerId = participant.PlayerId,
                    Name = participant.GameInfo.Name,
                    WearItemIdList = new List<int>(participant.GameInfo.WearItemIdList)
                });
            }
            return profiles;
        }
    }

    public bool TryEliminatePlayer(long playerId, EliminationReason reason,
        long attackerPlayerId = 0, AreaType eliminatedArea = AreaType.None, int forcedRank = 0)
    {
        using (Enter())
        {
            if (_cleanupStarted)
            {
                return false;
            }
            if (!_players.TryGetValue(playerId, out var participant)) return false;
            if (participant.Status == PlayerMatchStatus.ELIMINATED) return false;

            participant.Status = PlayerMatchStatus.ELIMINATED;
            participant.EliminationReason = reason;
            participant.EliminatedAt = DateTime.UtcNow;
            participant.AttackerPlayerId = attackerPlayerId;
            participant.EliminatedArea = eliminatedArea;
            participant.EliminationRank = forcedRank > 0 ? forcedRank : _aliveCount;
            _aliveCount--;

            _logger.LogInformation("플레이어 탈락: MatchingId={MatchingId}, PlayerId={PlayerId}, 사유={Reason}, 생존={Alive}", MatchingId, playerId, reason, _aliveCount);
            return true;
        }
    }

    /// <summary>사람·봇 참가자 중 현재 연결이 있는 세션만 복사해 반환한다.</summary>
    public List<GameClientSession> GetSessions()
    {
        using (Enter())
        {
            var sessions = new List<GameClientSession>();
            foreach (var player in _players.Values)
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
        using (Enter())
        {
            if (_cleanupStarted)
                return (false, null);

            if (_players.Count == 0)
                return (false, null);

            var activePlayers = _players.Values
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
        long attackerPlayerId, AreaType eliminatedArea, int eliminationRank)> BuildGameResult()
    {
        using (Enter())
        {
            if (_cleanupStarted)
                return new();

            var result = new List<(long, EliminationReason, PlayerMatchStatus, DateTime?, long,
                AreaType, int)>();

            foreach (var participant in _players.Values)
            {
                result.Add((participant.PlayerId,
                    participant.EliminationReason, participant.Status, participant.EliminatedAt, participant.AttackerPlayerId,
                    participant.EliminatedArea,
                    participant.EliminationRank));
            }

            return result;
        }
    }

    public DateTime? EntryDeadlineUtc { get { using (Enter()) return _entryDeadlineUtc; } }
    public DateTime? StartsAtUtc { get { using (Enter()) return _startsAtUtc; } }
    /// <summary>개전 게이트가 없는 봇 전용 매치의 시작 앵커. 전투 서비스가 스웜 첫 틱에 찍는다.</summary>
    public DateTime? FallbackStartedAtUtc { get; set; }

    // 전투·구역 서비스가 매치 잠금 안에서 쓰는 진행 표시
    public bool TimeoutResultProcessed { get; set; }
    /// <summary>마지막으로 방송한 오브 순위표. 같으면 다시 보내지 않는다.</summary>
    public string? OrbRankingsSignature { get; set; }
    public bool InitialFieldStateSent { get; set; }

    public void BeginEntry(long playerId)
    {
        using (Enter())
        {
            if (IsEnded || !IsSetupComplete || playerId <= 0 || GetPlayer(playerId) == null)
            {
                return;
            }
            _entryDeadlineUtc ??= DateTime.UtcNow + MatchingRedisKeys.EntryTimeout;
        }
    }

    public void MarkPlayerReady(long playerId)
    {
        using (Enter())
        {
            if (IsEnded || !_entryDeadlineUtc.HasValue || playerId <= 0 || GetPlayer(playerId) == null)
            {
                return;
            }
            _readyPlayerIds.Add(playerId);
            if (_players.Values.Any(player => player.PlayerId > 0 && !_readyPlayerIds.Contains(player.PlayerId)))
            {
                return;
            }
            _startsAtUtc ??= DateTime.UtcNow.AddSeconds(5);
        }
    }

    public bool IsGameplayActive(DateTime? utcNow = null)
    {
        using (Enter())
            return !IsEnded && _startsAtUtc.HasValue && (utcNow ?? DateTime.UtcNow) >= _startsAtUtc.Value;
    }

    public bool IsEntryTimedOut(DateTime utcNow)
    {
        using (Enter())
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
                while (PendingCombatHits.Count > 0)
                {
                    try
                    {
                        MatchSynchronizationService.SendPendingCombatHits(this);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Terminal combat hit publication failed: MatchingId={MatchingId}", MatchingId);
                    }
                }
                TickLoop?.Stop();
                foreach (var player in _players.Values)
                {
                    player.Session = null;
                    player.ReachableItems.Clear();
                }
                Doors.Clear();
                GroundItems.Release();
                _players.Clear();
                _aliveCount = 0;
                Closures.Release();
                Bots.Release();
                Monsters.Release();
                SynchronizedObjects.Clear();
                SynchronizedPlayerStates.Clear();
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
