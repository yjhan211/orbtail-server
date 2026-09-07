using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
///     사람 플레이어 하나의 자원·수면·주기 버프 상태와 계산 규칙.
///     세션이 소유하며 매치 잠금 안에서 갱신한다. 타이머·패킷·로그·탈락 처리는 소유자가 맡는다.
/// </summary>
internal sealed class PlayerConditionState
{
    private const double SwarmSleepWarmupSeconds = 1d;
    private const double SwarmSleepCombatLockSeconds = 3d;
    private const float SwarmSleepRecoveryRatioPerSecond = 0.05f;
    private readonly List<PeriodicBuffEntry> _periodicBuffs = [];
    private int _swarmSleepGrantedTicks;

    public int Stamina { get; set; } = 100;
    public int Corruption { get; set; }
    public bool IsSleeping { get; set; }
    public DateTime SleepStartedAtUtc { get; set; } = DateTime.MinValue;
    public DateTime LastCombatAtUtc { get; set; } = DateTime.MinValue;
    public DateTime HealLockUntilUtc { get; set; } = DateTime.MinValue;
    public bool HasPeriodicBuffs => _periodicBuffs.Count != 0;

    public int ChangeResources(int staminaDelta, int corruptionDelta, int maxStamina, int maxCorruption)
    {
        int conversion = 0;
        if (staminaDelta != 0)
        {
            int next = Stamina + staminaDelta;
            conversion = next < 0 ? -next * 2 : 0;
            Stamina = Math.Clamp(next, 0, maxStamina);
        }
        int total = corruptionDelta + conversion;
        if (total != 0) Corruption = Math.Clamp(Corruption + total, 0, maxCorruption);
        return conversion;
    }

    public bool CanSleep(DateTime nowUtc) =>
        (nowUtc - LastCombatAtUtc).TotalSeconds >= SwarmSleepCombatLockSeconds && nowUtc >= HealLockUntilUtc;

    public void ResetSleep()
    {
        SleepStartedAtUtc = DateTime.MinValue;
        _swarmSleepGrantedTicks = 0;
    }

    public int GetSleepRecovery(DateTime nowUtc, bool eliminated, int maxCorruption)
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
        if (Corruption <= 0) return 0;
        int perTick = Math.Max(1, (int)MathF.Round(maxCorruption * SwarmSleepRecoveryRatioPerSecond));
        return Math.Min(perTick * pending, Corruption);
    }

    public void AddPeriodicBuff(BuffSubType type, int value, int interval, int duration = 0)
    {
        _periodicBuffs.RemoveAll(buff => buff.Type == type);
        _periodicBuffs.Add(new PeriodicBuffEntry(type, value, interval, duration));
    }

    public void ClearPeriodicBuffs() => _periodicBuffs.Clear();

    public void TickPeriodicBuffs(int maxStamina, int maxCorruption, Action<int, int> apply)
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
                    BuffSubType.CONDITION_ADD => Stamina < maxStamina,
                    BuffSubType.CORRUPTION_DOWN => Corruption > 0,
                    BuffSubType.CORRUPTION_ADD => Corruption < maxCorruption,
                    _ => false
                };
                if (canApply)
                {
                    if (buff.Type == BuffSubType.CONDITION_ADD) apply(buff.Value, 0);
                    else apply(0, buff.Type == BuffSubType.CORRUPTION_DOWN ? -buff.Value : buff.Value);
                }
                else if (buff.Duration <= 0 && buff.Type is BuffSubType.CONDITION_ADD or BuffSubType.CORRUPTION_DOWN or BuffSubType.CORRUPTION_ADD)
                    _periodicBuffs.Remove(buff);
            }
            if (buff.Duration > 0 && buff.Remaining <= 0) _periodicBuffs.Remove(buff);
        }
    }

    public (int Stamina, int Corruption, bool Periodic) ApplyItemBuffs(int itemId, IReadOnlyCollection<int> activeBuffIds)
    {
        int stamina = 0, corruption = 0;
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
                case BuffSubType.CONDITION_ADD:
                    stamina += PassiveBuffUtility.ApplyIncrease(value, activeBuffIds, BuffSubType.RECOVERY_ITEM_EFFECT_ADD);
                    break;
                case BuffSubType.CORRUPTION_DOWN:
                    corruption -= PassiveBuffUtility.ApplyIncrease(value, activeBuffIds, BuffSubType.RECOVERY_ITEM_EFFECT_ADD);
                    break;
                case BuffSubType.CORRUPTION_ADD:
                    corruption += value;
                    break;
            }
        }
        return (stamina, corruption, periodic);
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
