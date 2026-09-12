using System.Diagnostics;
using game_server.sessions;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.players;

/// <summary>
///     매치 내 플레이어 한 명의 프로필·체력·이동·상호작용·탈락 상태를 관리한다.
///     MatchRuntime이 소유하며, 연결이 끊겨도 매치 정리까지 상태를 유지한다.
///     Session은 현재 연결을 가리키며, 입장 전·봇·연결 종료 후에는 null이다.
///     게임 상태 변경은 매치 잠금 안에서 수행하고, 패킷 전송은 세션과 서비스가 담당한다.
/// </summary>
public class Player
{
    private const double SwarmSleepWarmupSeconds = 1d;
    private const double SwarmSleepCombatLockSeconds = 3d;
    private const float SwarmSleepRecoveryRatioPerSecond = 0.05f;

    private GameClientSession? _session;
    private PlayerState _state = PlayerState.IDLE;
    private float? _orbOrbitPhaseDegrees;
    private Vector3f? _orbOrbitLastPosition;

    private readonly Dictionary<int, int> _orbUpgradeCounts = new();

    private long _lastMoveProcessedTimestamp;
    private long _lastMoveResponseTimestamp;

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

    // 서버가 확정한 공통 위치. 사람은 입장 초기화 전까지 위치와 셀이 없을 수 있다.
    public Vector3f? Position { get; internal set; }
    public Cell? Cell { get; internal set; }
    public Vector3f Velocity { get; internal set; } = new();
    public float Rotation { get; internal set; }
    public AreaType CurrentArea { get; internal set; } = AreaType.None;
    /// <summary>승인된 이동 구간에서 획득 반경에 닿은 바닥 아이템(uid 키). 다음 자동 줍기 틱이 집는다.</summary>
    internal Dictionary<long, ReachableItem> ReachableItems { get; } = new();
    public float OrbOrbitPhaseDegrees => _orbOrbitPhaseDegrees ?? SwarmOrbOrbit.InitialPhaseDegrees(PlayerId);

    public int Health { get; set; } = Config.MAX_HEALTH;
    /// <summary>보유 오브 컬렉션. 각 오브의 UID와 꼬리 순서를 유지한다.</summary>
    public PlayerOrbCollection Orbs { get; } = new();
    /// <summary>오브별 자동공격 표적·조준·발사 주기. MatchAutoAttackService가 매치 잠금 안에서 갱신한다.</summary>
    internal PlayerAutoAttackState AutoAttack { get; } = new();
    /// <summary>회복 오브(UID·스택)별 다음 회복 시각. PlayerOrbService가 매치 잠금 안에서 갱신한다.</summary>
    internal Dictionary<(long ItemUid, int StackIndex), DateTime> OrbRecoveryReadyAtUtc { get; } = new();
    /// <summary>내 이동이 남긴 꼬리 경로 점. 오브 열 좌표는 이 경로 위의 거리로 정한다.</summary>
    internal List<Vector3f> OrbTrail { get; } = new();
    /// <summary>절단 판정용 직전 틱 위치. 첫 틱은 기록만 한다.</summary>
    internal Vector3f? TrailLastTickPosition { get; set; }
    /// <summary>내가 상대 오브(UID)를 마지막으로 밟은 시각 — 같은 오브 재판정 억제와 이탈 재무장의 기준.</summary>
    internal Dictionary<long, DateTime> OrbCutLatches { get; } = new();
    /// <summary>태양 교차사격 화상. 맞을 때마다 지속·다음 틱이 새로 잡히고 MatchOrbAttackService가 틱을 정산한다.</summary>
    internal SunBurnState? SunBurn { get; set; }
    /// <summary>바람 오브 UID별 다음 칼날 시각과 표적이 반경에 든 시각. 매치 잠금 안에서 접근한다.</summary>
    private readonly Dictionary<long, DateTime> _windOrbNextAttackAtUtc = new();
    private readonly Dictionary<long, DateTime> _windOrbEngagedAtUtc = new();
    /// <summary>파도 오브 UID별 다음 발동 시각. 매치 잠금 안에서 접근한다.</summary>
    private readonly Dictionary<long, DateTime> _waveOrbNextAttackAtUtc = new();
    private DateTime? _windShockImmuneUntilUtc;
    private DateTime? _woundUntilUtc;
    /// <summary>소환석 잔액과 성공한 소환 횟수. 비용·후보·지급 규칙은 PlayerOrbGrowthService에 있다.</summary>
    public SummonStoneState SummonStones { get; internal set; }

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
    /// <summary>파도 오브 감속이 끝나는 시각.</summary>
    public DateTime WaveSlowUntilUtc { get; set; }
    /// <summary>잔상 접촉 피해 면역이 끝나는 시각. 한 번 맞으면 잠깐 연속 피격을 막는다.</summary>
    public DateTime MonsterContactImmuneUntilUtc { get; set; }
    /// <summary>PvP 피해의 소수점 잔여. 정수 체력 피해로 넘어갈 때까지 누적한다.</summary>
    internal float PvpDamageCarry { get; set; }


    // 다른 매치 잠금을 잡지 않고 이전 연결만 해제한다. 새 연결은 지우지 않는다.
    internal bool DetachSession(GameClientSession session) =>
        ReferenceEquals(Interlocked.CompareExchange(ref _session, null, session), session);

    /// <summary>위상을 초기화한다. 기준 위치가 없으면 첫 이동은 기준만 기록한다.</summary>
    public void ResetOrbOrbit(Vector3f? position = null)
    {
        _orbOrbitPhaseDegrees = SwarmOrbOrbit.InitialPhaseDegrees(PlayerId);
        _orbOrbitLastPosition = position == null ? null : new Vector3f(position.X, position.Y, position.Z);
    }

    /// <summary>직전 이동부터의 거리로 오브 위상을 갱신한다. 텔레포트급 이동은 회전에 반영하지 않는다.</summary>
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
    /// <summary>이전 이동 요청과의 처리 간격을 초 단위로 계산하고, 마지막 처리 시각을 갱신한다.</summary>
    public float CalculateMoveDeltaTime(long timestamp)
    {
        if (_lastMoveProcessedTimestamp == 0)
        {
            _lastMoveProcessedTimestamp = timestamp;
            return PlayerMovementService.InitialReceiptDeltaSeconds;
        }

        double elapsedSeconds = (timestamp - _lastMoveProcessedTimestamp) / (double)Stopwatch.Frequency;
        _lastMoveProcessedTimestamp = timestamp;
        return PlayerMovementService.ClampMoveDeltaTime(elapsedSeconds);
    }

    /// <summary>첫 이동 응답이거나, 마지막 응답 이후 전송 간격이 지났는지 확인한다.</summary>
    public bool ShouldSendMoveResponse(long timestamp)
    {
        if (_lastMoveResponseTimestamp == 0)
            return true;

        double elapsedSeconds = (timestamp - _lastMoveResponseTimestamp) / (double)Stopwatch.Frequency;
        return elapsedSeconds >= PlayerMovementService.MovementAcknowledgementIntervalSeconds;
    }

    /// <summary>이동 응답을 전송한 시각을 기록한다. 즉시 보정 응답도 같은 간격에 반영한다.</summary>
    public void RecordMoveResponse(long timestamp) => _lastMoveResponseTimestamp = timestamp;

    /// <summary>입장 시 서버가 지정한 스폰으로 이동 상태를 초기화한다.</summary>
    public void InitializeSpawn(Cell spawnCell)
    {
        Cell = Cell.Clone(spawnCell);
        Position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, spawnCell);
        Velocity = new Vector3f();
        Rotation = 0f;
        CurrentArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, spawnCell);
        ResetOrbOrbit(Position);
    }

    /// <summary>검증된 이동 값을 함께 반영한다. 호출자는 매치 잠금을 잡아야 한다.</summary>
    internal void ApplyValidatedMovement(PlayerMovementService.ValidatedMovement movement, float rotation)
    {
        Cell = movement.ValidCell;
        Position = movement.Position;
        Velocity = movement.Velocity;
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

    /// <summary>
    ///     등록 후 첫 1초부터 매치 틱마다 초 단위로 진행하고, 실행 시각이 된 체력 변화량(양수 회복·음수 피해)을
    ///     순서대로 돌려준다. 적용은 호출자가 한다.
    /// </summary>
    public List<int> TakeDuePeriodicBuffDeltas(DateTime nowUtc, int maxHealth)
    {
        var deltas = new List<int>();
        while (_periodicBuffs.Count != 0 && _nextPeriodicBuffTickAtUtc is { } next && nowUtc >= next)
        {
            _nextPeriodicBuffTickAtUtc = next.AddSeconds(1);
            deltas.AddRange(TickPeriodicBuffs(maxHealth));
        }
        if (_periodicBuffs.Count == 0)
            _nextPeriodicBuffTickAtUtc = null;
        return deltas;
    }

    /// <summary>버프 1초 진행. 이번 초에 발동한 버프의 체력 변화량을 돌려주고 만료된 버프를 뺀다.</summary>
    public List<int> TickPeriodicBuffs(int maxHealth)
    {
        var deltas = new List<int>();
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
                    deltas.Add(buff.Type == BuffSubType.HEALTH_ADD ? buff.Value : -buff.Value);
                }
                else if (buff.Duration <= 0 && buff.Type is BuffSubType.HEALTH_ADD or BuffSubType.HEALTH_DOWN)
                    _periodicBuffs.Remove(buff);
            }
            if (buff.Duration > 0 && buff.Remaining <= 0) _periodicBuffs.Remove(buff);
        }
        return deltas;
    }

    public void BeginInteraction(int interactId) => _pending.Add(interactId);
    public bool TryFinishInteraction(int interactId)
    {
        if (_pendingDoor == interactId) _pendingDoor = null;
        return _pending.Remove(interactId);
    }
    public int[] GetPendingInteractionIds() => _pending.ToArray();
    public void ClearPendingInteractions()
    {
        _pending.Clear();
        _pendingDoor = null;
    }

    internal int? PendingDoorInteractionId => _pendingDoor;

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

    /// <summary>소환석 잔액과 성공한 소환 횟수. 값이라 읽는 순간의 상태가 그대로 남고, 갱신은 통째로 바꾼다.</summary>
    public readonly record struct SummonStoneState(int StoneCount, int SuccessfulSummonCount)
    {
        private const int BaseSummonCost = 2;

        public static readonly SummonStoneState Empty = default;

        /// <summary>다음 소환 비용. 성공한 소환 횟수의 삼각수이고 최소 2, 상한은 없다.</summary>
        public int NextCost => CostAfter(SuccessfulSummonCount);

        public static int CostAfter(int successfulSummonCount)
        {
            long summonNumber = (long)Math.Max(0, successfulSummonCount) + 1;
            long cost = Math.Max(BaseSummonCost, summonNumber * (summonNumber + 1) / 2);
            return (int)Math.Min(int.MaxValue, cost);
        }
    }

    internal bool TryBeginWindOrbTick(long itemUid, DateTime nowUtc, double intervalSeconds)
    {
        if (_windOrbNextAttackAtUtc.TryGetValue(itemUid, out DateTime nextTickAtUtc) && nowUtc < nextTickAtUtc)
            return false;

        _windOrbNextAttackAtUtc[itemUid] = nowUtc.AddSeconds(intervalSeconds);
        return true;
    }

    internal void ResetWindOrbEngagement(long itemUid) =>
        _windOrbEngagedAtUtc.Remove(itemUid);

    internal bool HasCompletedWindOrbSpinup(long itemUid, DateTime nowUtc, double durationSeconds)
    {
        if (!_windOrbEngagedAtUtc.TryGetValue(itemUid, out DateTime engagedAtUtc))
        {
            engagedAtUtc = nowUtc;
            _windOrbEngagedAtUtc[itemUid] = engagedAtUtc;
        }

        return (nowUtc - engagedAtUtc).TotalSeconds >= durationSeconds;
    }

    /// <summary>
    ///     파도 오브가 발동할 시각이 됐는지. 처음 본 오브는 위상만큼 미룬 첫 발동 시각만 잡고 false를 돌려준다.
    ///     발동이 확정되면 ScheduleNextWaveOrbAttack으로 다음 시각을 잡는다.
    /// </summary>
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

    /// <summary>오브가 절단·드롭으로 사라지면 그 UID의 발동 시각을 지운다.</summary>
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

    /// <summary>바람 칼날 피격 면역: 면역 창 안이면 거짓, 아니면 새 창을 열고 참.</summary>
    internal bool TryClaimWindShock(DateTime nowUtc, double immunitySeconds)
    {
        if (_windShockImmuneUntilUtc.HasValue && nowUtc < _windShockImmuneUntilUtc.Value)
        {
            return false;
        }

        _windShockImmuneUntilUtc = nowUtc.AddSeconds(immunitySeconds);
        return true;
    }

    /// <summary>상처: 바람 칼날에 맞으면 걸리고, 걸린 동안은 PvP 충격 치명타가 열린다.</summary>
    internal void ApplyWound(DateTime untilUtc) => _woundUntilUtc = untilUtc;

    internal bool IsWounded(DateTime nowUtc) => _woundUntilUtc.HasValue && nowUtc < _woundUntilUtc.Value;

    /// <summary>이동 구간에서 획득 반경에 닿은 바닥 아이템. 다음 자동 줍기 틱이 집는다.</summary>
    internal sealed record ReachableItem(long GroundItemUid, AreaType Area, Vector3f Position);

    /// <summary>화상 한 건: 누가 어떤 오브로 어느 구역에서 걸었는지, 언제 끝나고 다음 틱이 언제인지.</summary>
    internal readonly record struct SunBurnState(long OwnerId, int WeaponItemId, AreaType Area, DateTime UntilUtc, DateTime NextTickAtUtc);

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
