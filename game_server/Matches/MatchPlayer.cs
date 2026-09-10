using game_server.sessions;
using network.common;
using network.common.data.models;

namespace game_server.matches;

/// <summary>
///     매치 내 플레이어 한 명의 프로필·체력·이동·상호작용·탈락 상태를 관리한다.
///     MatchRuntime이 소유하며, 연결이 끊겨도 매치 정리까지 상태를 유지한다.
///     Session은 현재 연결을 가리키며, 입장 전·봇·연결 종료 후에는 null이다.
///     게임 상태 변경은 매치 잠금 안에서 수행하고, 패킷 전송은 세션과 서비스가 담당한다.
/// </summary>
public class MatchPlayer
{
    private const double SwarmSleepWarmupSeconds = 1d;
    private const double SwarmSleepCombatLockSeconds = 3d;
    private const float SwarmSleepRecoveryRatioPerSecond = 0.05f;

    private GameClientSession? _session;
    private PlayerState _state = PlayerState.IDLE;

    internal long LastMoveProcessedTimestamp;
    internal long LastMoveResponseTimestamp;

    private int _swarmSleepGrantedTicks;
    private readonly List<PeriodicBuffEntry> _periodicBuffs = [];
    private DateTime? _nextPeriodicBuffTickAtUtc;

    private readonly HashSet<int> _pending = [];
    private int? _pendingDoor;
    private int _openedDoors;
    private long _doorStartedAt;

    internal GameClientSession? Session
    {
        get => Volatile.Read(ref _session);
        set => Volatile.Write(ref _session, value);
    }

    public long PlayerId => Profile.PlayerId;
    public required PlayerInfo Profile { get; init; }
    public bool IsEliminated => Status is PlayerMatchStatus.ELIMINATED or PlayerMatchStatus.SPECTATING;
    public PlayerMatchStatus Status { get; set; } = PlayerMatchStatus.ACTIVE;
    public EliminationReason EliminationReason { get; set; } = EliminationReason.NONE;
    public DateTime? EliminatedAt { get; set; }
    public long AttackerPlayerId { get; set; }
    public AreaType EliminatedArea { get; set; } = AreaType.None;
    public int EliminationRank { get; set; }
    public int FinalOrbTier { get; set; }

    public Vector3f? LastValidatedPosition { get; internal set; }
    public Cell? LastValidatedCell { get; internal set; }
    public Vector3f LastValidatedVelocity { get; internal set; } = new();
    public float LastValidatedRotation { get; internal set; }
    public AreaType CurrentArea { get; internal set; } = AreaType.None;
    public float OrbOrbitPhaseDegrees { get; internal set; }

    public int Health { get; set; } = Config.MAX_HEALTH;

    public PlayerState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            ResetSleep();
        }
    }

    public bool IsSleeping => State == PlayerState.SLEEP;
    public DateTime SleepStartedAtUtc { get; set; } = DateTime.MinValue;
    public DateTime LastCombatAtUtc { get; set; } = DateTime.MinValue;
    public DateTime HealLockUntilUtc { get; set; } = DateTime.MinValue;


    // 다른 매치 잠금을 잡지 않고 이전 연결만 해제한다. 새 연결은 지우지 않는다.
    internal bool DetachSession(GameClientSession session) =>
        ReferenceEquals(Interlocked.CompareExchange(ref _session, null, session), session);

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

    /// <summary>체력을 변경하고 확정 결과를 반환한다. 패킷·로그·탈락 처리는 하지 않는다.</summary>
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

    /// <summary>교전 시각을 기록한다. 이후 3초 동안 수면 진입을 막지만 현재 수면을 깨우지는 않는다.</summary>
    public void MarkSwarmCombat(DateTime nowUtc) => LastCombatAtUtc = nowUtc;

    /// <summary>지정한 시각까지 수면 진입과 회복을 차단한다.</summary>
    public void BlockHealingUntil(DateTime untilUtc) => HealLockUntilUtc = untilUtc;

    /// <summary>수면 중이면 IDLE로 전환한다. 버프는 유지하며 전송은 호출자가 담당한다.</summary>
    public bool TryStopSleep()
    {
        if (!IsSleeping) return false;
        State = PlayerState.IDLE;
        return true;
    }

    public bool CanSleep(DateTime nowUtc) =>
        (nowUtc - LastCombatAtUtc).TotalSeconds >= SwarmSleepCombatLockSeconds && nowUtc >= HealLockUntilUtc;

    private void ResetSleep()
    {
        SleepStartedAtUtc = DateTime.MinValue;
        _swarmSleepGrantedTicks = 0;
    }

    public int GetSleepRecovery(DateTime nowUtc, bool eliminated, int maxHealth)
    {
        if (eliminated || !IsSleeping) { ResetSleep(); return 0; }
        if (SleepStartedAtUtc == DateTime.MinValue)
        {
            SleepStartedAtUtc = nowUtc;
            _swarmSleepGrantedTicks = 0;
            return 0;
        }
        double elapsed = (nowUtc - SleepStartedAtUtc).TotalSeconds;
        if (elapsed < SwarmSleepWarmupSeconds) return 0;
        int due = (int)Math.Floor(elapsed - SwarmSleepWarmupSeconds) + 1;
        if (nowUtc < HealLockUntilUtc) { _swarmSleepGrantedTicks = due; return 0; }
        int pending = due - _swarmSleepGrantedTicks;
        if (pending <= 0) return 0;
        _swarmSleepGrantedTicks = due;
        if (Health >= maxHealth) return 0;
        int perTick = Math.Max(1, (int)MathF.Round(maxHealth * SwarmSleepRecoveryRatioPerSecond));
        return Math.Min(perTick * pending, maxHealth - Health);
    }

    public void AddPeriodicBuff(BuffSubType type, int value, int interval, int duration = 0, DateTime? nowUtc = null)
    {
        if (_periodicBuffs.Count == 0)
            _nextPeriodicBuffTickAtUtc = (nowUtc ?? DateTime.UtcNow).AddSeconds(1);
        _periodicBuffs.RemoveAll(buff => buff.Type == type);
        _periodicBuffs.Add(new PeriodicBuffEntry(type, value, interval, duration));
    }

    public void ClearPeriodicBuffs()
    {
        _periodicBuffs.Clear();
        _nextPeriodicBuffTickAtUtc = null;
    }

    /// <summary>등록 후 첫 1초부터 기존 매치 틱에서 초 단위 버프 처리를 실행한다.</summary>
    public void UpdatePeriodicBuffs(DateTime nowUtc, int maxHealth, Action<int> apply)
    {
        while (_periodicBuffs.Count != 0 && _nextPeriodicBuffTickAtUtc is { } next && nowUtc >= next)
        {
            _nextPeriodicBuffTickAtUtc = next.AddSeconds(1);
            TickPeriodicBuffs(maxHealth, apply);
        }
        if (_periodicBuffs.Count == 0)
            _nextPeriodicBuffTickAtUtc = null;
    }

    public void TickPeriodicBuffs(int maxHealth, Action<int> apply)
    {
        foreach (var buff in _periodicBuffs.ToArray())
        {
            if (!_periodicBuffs.Contains(buff)) continue;
            buff.Elapsed++;
            if (buff.Duration > 0 && buff.Remaining > 0) buff.Remaining--;
            if (buff.Elapsed >= buff.Interval)
            {
                buff.Elapsed = 0;
                bool canApply = buff.Type switch
                {
                    BuffSubType.HEALTH_ADD => Health < maxHealth,
                    BuffSubType.HEALTH_DOWN => Health > 0,
                    _ => false
                };
                if (canApply)
                {
                    apply(buff.Type == BuffSubType.HEALTH_ADD ? buff.Value : -buff.Value);
                }
                else if (buff.Duration <= 0 && buff.Type is BuffSubType.HEALTH_ADD or BuffSubType.HEALTH_DOWN)
                    _periodicBuffs.Remove(buff);
            }
            if (buff.Duration > 0 && buff.Remaining <= 0) _periodicBuffs.Remove(buff);
        }
    }

    public void BeginInteraction(int interactId) => _pending.Add(interactId);
    public bool TryFinishInteraction(int interactId) => _pending.Remove(interactId);
    public int[] GetPendingInteractionIds() => _pending.ToArray();
    public void ClearPendingInteractions()
    {
        _pending.Clear();
        _pendingDoor = null;
    }

    public void BeginDoor(int interactId, long startedAt)
    {
        if (_pendingDoor is { } previous)
            _pending.Remove(previous);
        BeginInteraction(interactId);
        _pendingDoor = interactId;
        _doorStartedAt = startedAt;
    }

    public bool TryFinishDoor(int interactId, long now, TimeSpan duration, out ErrorCode error)
    {
        error = ErrorCode.INVALID_GAME_STATE;
        if (_pendingDoor != interactId || !_pending.Contains(interactId))
            return false;
        if (now - _doorStartedAt < duration.TotalMilliseconds)
        {
            error = ErrorCode.DOOR_OPEN_TOO_EARLY;
            return false;
        }

        _pendingDoor = null;
        _pending.Remove(interactId);
        error = ErrorCode.SUCCESS;
        return true;
    }

    public void CompleteDoor()
    {
        _pendingDoor = null;
        _openedDoors++;
    }

    public int? InterruptDoor()
    {
        // 첫 문은 시작 구역 탈출을 보장하기 위해 피격으로 중단하지 않는다.
        if (_pendingDoor is not { } id || _openedDoors == 0) return null;
        _pendingDoor = null;
        _pending.Remove(id);
        return id;
    }

    public readonly record struct HealthChange(int Before, int After, int RequestedDelta)
    {
        public bool Changed => Before != After;
        public int ActualDelta => After - Before;
        public int Recovered => Math.Max(0, ActualDelta);
        public bool IsDepleted => After == 0;
    }

    private sealed class PeriodicBuffEntry(BuffSubType type, int value, int interval, int duration)
    {
        public readonly BuffSubType Type = type;
        public readonly int Value = value;
        public readonly int Interval = interval;
        public readonly int Duration = duration;
        public int Remaining = duration;
        public int Elapsed;
    }
}
