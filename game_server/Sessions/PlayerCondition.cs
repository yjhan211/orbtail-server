using game_server.services;
using network.common;
using network.common.data;

namespace game_server.sessions;

/// <summary>
///     사람 플레이어 하나의 자원·수면·주기 버프 상태와 계산 규칙.
///     세션이 소유하며 매치 잠금 안에서 갱신한다. 타이머·패킷·로그·탈락 처리는 소유자가 맡는다.
/// </summary>
internal sealed class PlayerCondition
{
    private const double SwarmSleepWarmupSeconds = 1d;
    private const double SwarmSleepCombatLockSeconds = 3d;
    private const float SwarmSleepRecoveryRatioPerSecond = 0.05f;
    private readonly List<PeriodicBuffEntry> _periodicBuffs = [];
    private int _swarmSleepGrantedTicks;

    public int Health { get; set; } = Config.MAX_HEALTH;
    public bool IsSleeping { get; set; }
    public DateTime SleepStartedAtUtc { get; set; } = DateTime.MinValue;
    public DateTime LastCombatAtUtc { get; set; } = DateTime.MinValue;
    public DateTime HealLockUntilUtc { get; set; } = DateTime.MinValue;
    public bool HasPeriodicBuffs => _periodicBuffs.Count != 0;

    public void ChangeHealth(int healthDelta, int maxHealth)
    {
        Health = Math.Clamp(Health + healthDelta, 0, maxHealth);
    }

    public bool CanSleep(DateTime nowUtc) =>
        (nowUtc - LastCombatAtUtc).TotalSeconds >= SwarmSleepCombatLockSeconds && nowUtc >= HealLockUntilUtc;

    public void ResetSleep()
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

    public void AddPeriodicBuff(BuffSubType type, int value, int interval, int duration = 0)
    {
        _periodicBuffs.RemoveAll(buff => buff.Type == type);
        _periodicBuffs.Add(new PeriodicBuffEntry(type, value, interval, duration));
    }

    public void ClearPeriodicBuffs() => _periodicBuffs.Clear();

    public void TickPeriodicBuffs(int maxHealth, Action<int> apply)
    {
        foreach (var buff in _periodicBuffs.ToArray())
        {
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

    public (int Health, bool Periodic) ApplyItemBuffs(int itemId, IReadOnlyCollection<int> activeBuffIds)
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
                    health += PassiveBuffUtility.ApplyIncrease(value, activeBuffIds, BuffSubType.RECOVERY_ITEM_EFFECT_ADD);
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
