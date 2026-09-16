using network.common;

namespace game_server.players;

internal enum PlayerStatusEffectKind
{
    HealingBlocked,
    WaveSlow,
    Wound,
    WindShockImmunity,
    MonsterContactImmunity
}

/// <summary>
///     플레이어 한 명의 지속 효과·화상·수면 회복 상태를 보관한다. 호출자는 매치 잠금을 보유한다.
///     효과 적용은 종료 시각을 교체하며, 실제 피해·회복·이동 처리는 해당 서비스가 담당한다.
/// </summary>
internal sealed class PlayerStatusEffects
{
    private const double SwarmSleepWarmupSeconds = 1d;
    private const float SwarmSleepRecoveryRatioPerSecond = 0.05f;
    private readonly Dictionary<PlayerStatusEffectKind, DateTime> _expiresAt = new();
    private SleepRecoveryState? _sleep;

    public bool HasSleep() => _sleep != null;
    internal void StartSleep() => _sleep ??= new SleepRecoveryState();
    internal void StopSleep() => _sleep = null;
    public int GetSleepRecovery(DateTime nowUtc, int health, int maxHealth)
    {
        if (_sleep == null) return 0;
        if (_sleep.StartedAtUtc == DateTime.MinValue)
        {
            _sleep.StartedAtUtc = nowUtc;
            return 0;
        }
        double elapsed = (nowUtc - _sleep.StartedAtUtc).TotalSeconds;
        if (elapsed < SwarmSleepWarmupSeconds)
        {
            return 0;
        }
        int due = (int)Math.Floor(elapsed - SwarmSleepWarmupSeconds) + 1;
        int pending = due - _sleep.ProcessedRecoveryCount;
        if (pending <= 0)
        {
            return 0;
        }
        _sleep.ProcessedRecoveryCount = due;
        if (IsActive(PlayerStatusEffectKind.HealingBlocked, nowUtc) || health >= maxHealth)
        {
            return 0;
        }
        int perTick = Math.Max(1, (int)MathF.Round(maxHealth * SwarmSleepRecoveryRatioPerSecond));
        return Math.Min(perTick * pending, maxHealth - health);
    }

    private sealed class SleepRecoveryState
    {
        public DateTime StartedAtUtc { get; set; } = DateTime.MinValue;
        public int ProcessedRecoveryCount { get; set; }
    }

    internal SunBurnState? SunBurn { get; set; }

    public DateTime GetExpiresAt(PlayerStatusEffectKind kind) => _expiresAt.GetValueOrDefault(kind);

    public bool IsActive(PlayerStatusEffectKind kind, DateTime nowUtc) => nowUtc < GetExpiresAt(kind);

    public void Apply(PlayerStatusEffectKind kind, DateTime untilUtc) => _expiresAt[kind] = untilUtc;

    public bool TryApply(PlayerStatusEffectKind kind, DateTime nowUtc, double durationSeconds)
    {
        if (IsActive(kind, nowUtc))
        {
            return false;
        }
        Apply(kind, nowUtc.AddSeconds(durationSeconds));
        return true;
    }

    internal readonly record struct SunBurnState(long OwnerId, int WeaponItemId, AreaType Area, DateTime UntilUtc, DateTime NextTickAtUtc);
}
