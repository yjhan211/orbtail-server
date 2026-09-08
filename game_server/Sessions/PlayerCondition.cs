using network.common;
using network.common.data;

namespace game_server.sessions;

/// <summary>
///     사람 플레이어 하나의 체력·행동·주기 버프 상태와 변경 규칙.
///     피해·회복은 적용 결과를 반환한다. 요청한 변화량과 실제 변화량은 구분한다.
///     세션이 소유하며 매치 잠금 안에서 갱신한다. 매치 틱이 실행을 구동하며 패킷·로그·탈락 처리는 호출자가 맡는다.
/// </summary>
internal sealed class PlayerCondition
{
    private const double SwarmSleepWarmupSeconds = 1d;
    private const double SwarmSleepCombatLockSeconds = 3d;
    private const float SwarmSleepRecoveryRatioPerSecond = 0.05f;
    private readonly List<PeriodicBuffEntry> _periodicBuffs = [];
    private int _swarmSleepGrantedTicks;
    private DateTime? _nextPeriodicBuffTickAtUtc;

    private PlayerState _state = PlayerState.IDLE;

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
    public bool HasPeriodicBuffs => _periodicBuffs.Count != 0;

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

    public readonly record struct HealthChange(int Before, int After, int RequestedDelta)
    {
        public bool Changed => Before != After;
        public int ActualDelta => After - Before;
        public int Recovered => Math.Max(0, ActualDelta);
        public bool IsDepleted => After == 0;
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
        if (!HasPeriodicBuffs)
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
        while (HasPeriodicBuffs && _nextPeriodicBuffTickAtUtc is { } next && nowUtc >= next)
        {
            _nextPeriodicBuffTickAtUtc = next.AddSeconds(1);
            TickPeriodicBuffs(maxHealth, apply);
        }
        if (!HasPeriodicBuffs)
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

    public (int Health, bool Periodic) ApplyItemBuffs(int itemId)
    {
        int health = 0;
        bool periodic = false;
        foreach ((int buffId, int value, int interval) in GameItemData.Get(itemId).ConsumableBuffList)
        {
            var buff = GameBuffData.Get(buffId);
            if (buff.Type == BuffType.PERIODIC && interval > 0)
            {
                AddPeriodicBuff(buff.SubType, value, interval, itemId == 401000003 ? 15 : 0);
                periodic = true;
                continue;
            }
            switch (buff.SubType)
            {
                case BuffSubType.HEALTH_ADD:
                    health += value;
                    break;
                case BuffSubType.HEALTH_DOWN:
                    health -= value;
                    break;
            }
        }
        return (health, periodic);
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
