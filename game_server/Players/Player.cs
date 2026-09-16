using game_server.sessions;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.players;

/// <summary>
///     매치 내 플레이어 한 명의 식별·외형·체력·이동·상호작용·탈락 상태를 관리한다.
///     MatchRuntime이 소유하며, 연결이 끊겨도 매치 정리까지 상태를 유지한다.
///     Session은 현재 연결을 가리키며, 입장 전·봇·연결 종료 후에는 null이다.
///     게임 상태 변경은 매치 잠금 안에서 수행하고, 패킷 전송은 세션과 서비스가 담당한다.
/// </summary>
public class Player
{
    private const double SwarmSleepCombatLockSeconds = 3d;

    private GameClientSession? _session;
    internal GameClientSession? Session
    {
        get => Volatile.Read(ref _session);
        set => Volatile.Write(ref _session, value);
    }

    public long PlayerId => GameInfo.ObjectInfo.ObjectId;

    public Player(PlayerInfo profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        GameInfo.ObjectInfo.ObjectId = profile.PlayerId;
        GameInfo.ObjectInfo.MapId = Config.SWARM_MATCH_MAP;
        GameInfo.Name = profile.Name;
        GameInfo.WearItemIdList = new List<int>(profile.WearItemIdList);
    }

    public GamePlayerInfo GameInfo { get; } = new();
    public bool IsSpawned { get; private set; }
    internal long LastMoveProcessedTimestamp { get; set; }

    public Vector3f? Position
    {
        get => IsSpawned ? GameInfo.ObjectInfo.Position : null;
        internal set
        {
            if (value == null)
            {
                IsSpawned = false;
                return;
            }
            GameInfo.ObjectInfo.Position = value;
            GameInfo.ObjectInfo.Cell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, value);
            IsSpawned = true;
        }
    }

    public Cell? Cell
    {
        get => IsSpawned ? GameInfo.ObjectInfo.Cell : null;
        internal set
        {
            if (value == null)
            {
                IsSpawned = false;
                return;
            }
            GameInfo.ObjectInfo.Cell = value;
            GameInfo.ObjectInfo.Position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, value);
            IsSpawned = true;
        }
    }

    public Vector3f Velocity
    {
        get => GameInfo.ObjectInfo.Velocity;
        internal set => GameInfo.ObjectInfo.Velocity = value;
    }

    public float Rotation
    {
        get => GameInfo.ObjectInfo.Rotation;
        internal set => GameInfo.ObjectInfo.Rotation = value;
    }

    public int Health { get => GameInfo.Health; set => GameInfo.Health = value; }

    internal Dictionary<long, ReachableItem> ReachableItems { get; } = new();

    public PlayerState State
    {
        get
        {
            if (StatusEffects.HasSleep()) return PlayerState.SLEEP;
            if (GameInfo.State == PlayerState.SLEEP) return PlayerState.IDLE;
            return GameInfo.State;
        }
        set
        {
            GameInfo.State = value;
            if (value == PlayerState.SLEEP)
                StatusEffects.StartSleep();
            else
                StatusEffects.StopSleep();
        }
    }

    public bool IsSleeping => StatusEffects.HasSleep();
    public DateTime LastCombatAtUtc { get; set; } = DateTime.MinValue;
    internal PlayerStatusEffects StatusEffects { get; } = new();

    private int? _pendingDoor;
    private long _doorStartedAt;
    internal int? PendingDoorInteractionId => _pendingDoor;

    private float? _orbOrbitPhaseDegrees;
    private Vector3f? _orbOrbitLastPosition;
    private readonly Dictionary<int, int> _orbUpgradeCounts = new();
    public float OrbOrbitPhaseDegrees => _orbOrbitPhaseDegrees ?? SwarmOrbOrbit.InitialPhaseDegrees(PlayerId);

    public SummonStoneStateInfo SummonStones { get; internal set; } = SummonStoneStateInfo.Empty;
    public PlayerOrbCollection Orbs { get; } = new();
    internal PlayerAutoAttackState AutoAttack { get; } = new();
    internal Dictionary<(long ItemUid, int StackIndex), DateTime> OrbRecoveryReadyAtUtc { get; } = new();
    internal List<Vector3f> OrbTrail { get; } = new();
    internal Vector3f? TrailLastTickPosition { get; set; }
    internal Dictionary<long, DateTime> OrbCutLatches { get; } = new();

    private readonly Dictionary<long, DateTime> _windOrbNextAttackAtUtc = new();
    private readonly Dictionary<long, DateTime> _windOrbEngagedAtUtc = new();
    private readonly Dictionary<long, DateTime> _waveOrbNextAttackAtUtc = new();
    internal float PvpDamageCarry { get; set; }

    public bool IsEliminated => Status is PlayerMatchStatus.ELIMINATED or PlayerMatchStatus.SPECTATING;
    public PlayerMatchStatus Status { get => GameInfo.Status; set => GameInfo.Status = value; }
    public EliminationReason EliminationReason { get; set; } = EliminationReason.NONE;
    public DateTime? EliminatedAt { get; set; }
    public long AttackerPlayerId { get; set; }
    public AreaType EliminatedArea { get; set; } = AreaType.None;
    public int EliminationRank { get; set; }
    public int FinalOrbTier { get; set; }
    public int PvpDamageDealt { get; set; }
    public int MonsterKillCount { get; set; }
    public int MonsterDamageDealt { get; set; }
    public int RecoveryTotal { get; set; }

    internal bool DetachSession(GameClientSession session) => ReferenceEquals(Interlocked.CompareExchange(ref _session, null, session), session);

    public void ResetOrbOrbit(Vector3f? position = null)
    {
        _orbOrbitPhaseDegrees = SwarmOrbOrbit.InitialPhaseDegrees(PlayerId);
        _orbOrbitLastPosition = position == null ? null : new Vector3f(position.X, position.Y, position.Z);
    }

    public void AdvanceOrbOrbit(Vector3f position)
    {
        if (_orbOrbitLastPosition != null)
        {
            float dx = position.X - _orbOrbitLastPosition.X;
            float dy = position.Y - _orbOrbitLastPosition.Y;
            _orbOrbitPhaseDegrees = SwarmOrbOrbit.AdvancePhase(
                OrbOrbitPhaseDegrees, MathF.Sqrt(dx * dx + dy * dy));
        }
        _orbOrbitLastPosition = new Vector3f(position.X, position.Y, position.Z);
    }

    public GamePlayerInfo CreatePlayerObjectInfo() => new()
    {
        ObjectInfo = CreateGameObjectInfo(),
        Name = GameInfo.Name,
        WearItemIdList = new List<int>(GameInfo.WearItemIdList),
        State = State,
        Health = Health,
        Status = Status
    };

    public GameObjectInfo CreateGameObjectInfo()
    {
        if (!IsSpawned)
        {
            throw new InvalidOperationException("Cannot publish a player before its spawn is initialized.");
        }
        var source = GameInfo.ObjectInfo;
        var snapshot = source.Clone();
        snapshot.ObjectId = PlayerId;
        return snapshot;
    }

    public void InitializeSpawn(Cell spawnCell)
    {
        Cell = Cell.Clone(spawnCell);
        Position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, spawnCell);
        Velocity = new Vector3f();
        Rotation = 0f;
        ResetOrbOrbit(Position);
    }

    internal void ApplyValidatedMovement(Cell? cell, Vector3f position, Vector3f velocity, float rotation)
    {
        Cell = cell;
        Position = position;
        Velocity = velocity;
        Rotation = rotation;
    }

    public int GetOrbUpgradeCount(int orbGroupId) =>
        _orbUpgradeCounts.GetValueOrDefault(orbGroupId);

    public int IncrementOrbUpgradeCount(int orbGroupId)
    {
        int count = GetOrbUpgradeCount(orbGroupId) + 1;
        _orbUpgradeCounts[orbGroupId] = count;
        return count;
    }

    public HealthChange ApplyDamage(int damage)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(damage);
        return ChangeHealth(-damage, Config.MAX_HEALTH);
    }

    public HealthChange Recover(int amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        return ChangeHealth(amount, Config.MAX_HEALTH);
    }

    public HealthChange ChangeHealth(int healthDelta, int maxHealth)
    {
        int before = Health;
        Health = (int)Math.Clamp((long)Health + healthDelta, 0L, maxHealth);
        return new HealthChange(before, Health, healthDelta);
    }

    public bool TryStartSleep(DateTime nowUtc)
    {
        if (IsSleeping || !CanSleep(nowUtc)) return false;
        State = PlayerState.SLEEP;
        return true;
    }

    public void MarkSwarmCombat(DateTime nowUtc) => LastCombatAtUtc = nowUtc;


    public bool TryStopSleep()
    {
        if (!IsSleeping) return false;
        State = PlayerState.IDLE;
        return true;
    }

    public bool CanSleep(DateTime nowUtc) =>
        (nowUtc - LastCombatAtUtc).TotalSeconds >= SwarmSleepCombatLockSeconds && !StatusEffects.IsActive(PlayerStatusEffectKind.HealingBlocked, nowUtc);

    public bool TryFinishInteraction(int interactId)
    {
        if (_pendingDoor != interactId) return false;
        _pendingDoor = null;
        return true;
    }
    public int[] GetPendingInteractionIds() => _pendingDoor.HasValue ? [_pendingDoor.Value] : [];
    public void ClearPendingInteractions()
    {
        _pendingDoor = null;
    }

    public void BeginDoor(int interactId, long startedAt)
    {
        _pendingDoor = interactId;
        _doorStartedAt = startedAt;
    }

    public bool TryFinishDoor(int interactId, long now, TimeSpan duration, out ErrorCode error)
    {
        error = ErrorCode.INVALID_GAME_STATE;
        if (_pendingDoor != interactId)
            return false;
        if (now - _doorStartedAt < duration.TotalMilliseconds)
        {
            error = ErrorCode.DOOR_OPEN_TOO_EARLY;
            return false;
        }

        _pendingDoor = null;
        error = ErrorCode.SUCCESS;
        return true;
    }

    public int? InterruptDoor()
    {
        if (_pendingDoor is not { } id)
        {
            return null;
        }
        _pendingDoor = null;
        return id;
    }

    internal bool TryBeginWindOrbTick(long itemUid, DateTime nowUtc, double intervalSeconds)
    {
        if (_windOrbNextAttackAtUtc.TryGetValue(itemUid, out var nextTickAtUtc) && nowUtc < nextTickAtUtc)
            return false;

        _windOrbNextAttackAtUtc[itemUid] = nowUtc.AddSeconds(intervalSeconds);
        return true;
    }

    internal void ResetWindOrbEngagement(long itemUid) => _windOrbEngagedAtUtc.Remove(itemUid);

    internal bool HasCompletedWindOrbSpinup(long itemUid, DateTime nowUtc, double durationSeconds)
    {
        if (!_windOrbEngagedAtUtc.TryGetValue(itemUid, out DateTime engagedAtUtc))
        {
            engagedAtUtc = nowUtc;
            _windOrbEngagedAtUtc[itemUid] = engagedAtUtc;
        }

        return (nowUtc - engagedAtUtc).TotalSeconds >= durationSeconds;
    }

    internal bool IsWaveOrbDue(long itemUid, DateTime nowUtc, double intervalSeconds, double firstPhase)
    {
        if (!_waveOrbNextAttackAtUtc.TryGetValue(itemUid, out DateTime nextAttackAtUtc))
        {
            _waveOrbNextAttackAtUtc[itemUid] = nowUtc.AddSeconds(intervalSeconds * firstPhase);
            return false;
        }

        return nowUtc >= nextAttackAtUtc;
    }

    internal void ScheduleNextWaveOrbAttack(long itemUid, DateTime nowUtc, double intervalSeconds) =>
        _waveOrbNextAttackAtUtc[itemUid] = nowUtc.AddSeconds(intervalSeconds);

    internal DateTime? WaveOrbNextAttackAt(long itemUid) =>
        _waveOrbNextAttackAtUtc.TryGetValue(itemUid, out DateTime nextAttackAtUtc) ? nextAttackAtUtc : null;

    internal void ForgetOrbTimers(long itemUid)
    {
        _windOrbNextAttackAtUtc.Remove(itemUid);
        _windOrbEngagedAtUtc.Remove(itemUid);
        _waveOrbNextAttackAtUtc.Remove(itemUid);
    }

    internal void ClearOrbTimers()
    {
        _windOrbNextAttackAtUtc.Clear();
        _windOrbEngagedAtUtc.Clear();
        _waveOrbNextAttackAtUtc.Clear();
    }

    internal sealed record ReachableItem(long GroundItemUid, AreaType Area, Vector3f Position);

    public readonly record struct HealthChange(int Before, int After, int RequestedDelta)
    {
        public bool Changed => Before != After;
        public int ActualDelta => After - Before;
        public int Recovered => Math.Max(0, ActualDelta);
        public bool IsDepleted => After == 0;
    }
}
