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
    // 식별·공개 정보와 현재 연결
    public GamePlayerInfo GameInfo { get; } = new();
    public long PlayerId => GameInfo.ObjectInfo.ObjectId;
    private GameClientSession? _session;
    internal GameClientSession? Session
    {
        get => Volatile.Read(ref _session);
        set => Volatile.Write(ref _session, value);
    }

    // 스폰·이동 상태
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

    // 체력·행동·상태 효과
    public int Health { get => GameInfo.Health; set => GameInfo.Health = value; }

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
    internal PlayerStatusEffects StatusEffects { get; } = new();

    // 기능별 소유 상태
    public PlayerOrbState Orbs { get; }
    internal PlayerInteractState Interactions { get; } = new();
    internal Dictionary<long, ReachableItem> ReachableItems { get; } = new();

    // 절단 판정·전투 누적 상태
    internal Vector3f? TrailLastTickPosition { get; set; }
    public int PvpDamageDealt { get; set; }

    // 매치 참가·탈락 상태
    public PlayerMatchStatus Status { get => GameInfo.Status; set => GameInfo.Status = value; }
    public bool IsEliminated => Status is PlayerMatchStatus.ELIMINATED or PlayerMatchStatus.SPECTATING;
    public EliminationReason EliminationReason { get; set; } = EliminationReason.NONE;
    public DateTime? EliminatedAt { get; set; }
    public long AttackerPlayerId { get; set; }
    public AreaType EliminatedArea { get; set; } = AreaType.None;
    public int EliminationRank { get; set; }

    public Player(PlayerInfo profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        GameInfo.ObjectInfo.ObjectId = profile.PlayerId;
        Orbs = new PlayerOrbState();
        GameInfo.ObjectInfo.MapId = Config.SWARM_MATCH_MAP;
        GameInfo.Name = profile.Name;
        GameInfo.WearItemIdList = new List<int>(profile.WearItemIdList);
    }

    internal bool DetachSession(GameClientSession session) => ReferenceEquals(Interlocked.CompareExchange(ref _session, null, session), session);

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
    }

    internal void ApplyValidatedMovement(Vector3f position, Vector3f velocity, float rotation)
    {
        Position = position;
        Velocity = velocity;
        Rotation = rotation;
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

    public bool TryStartSleep()
    {
        if (IsSleeping) return false;
        State = PlayerState.SLEEP;
        return true;
    }

    public bool TryStopSleep()
    {
        if (!IsSleeping) return false;
        State = PlayerState.IDLE;
        return true;
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
