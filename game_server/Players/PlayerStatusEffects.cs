using network.common;

namespace game_server.players;

internal enum PlayerStatusEffectKind
{
    HealingBlocked,
    WaveSlow,
    Wound,
    WindShockImmunity,
    MonsterContactImmunity,
    Sleep,
    SunBurn
}

/// <summary>
///     플레이어 한 명의 지속 효과를 종류별로 하나씩 보관한다. 호출자는 매치 잠금을 보유한다.
/// </summary>
internal sealed class PlayerStatusEffects
{
    private const double SwarmSleepWarmupSeconds = 1d;
    private const float SwarmSleepRecoveryRatioPerSecond = 0.05f;
    private readonly Dictionary<PlayerStatusEffectKind, StatusEffectState> _effects = new();

    public bool IsActive(PlayerStatusEffectKind kind, DateTime nowUtc) => _effects.TryGetValue(kind, out var state) && state.IsActive(nowUtc);

    public DateTime GetExpiresAt(PlayerStatusEffectKind kind) => _effects.GetValueOrDefault(kind) switch
    {
        TimedEffectState timed => timed.UntilUtc,
        SunBurnState burn => burn.UntilUtc,
        _ => default
    };

    public void Apply(PlayerStatusEffectKind kind, DateTime untilUtc)
    {
        if (kind is PlayerStatusEffectKind.Sleep or PlayerStatusEffectKind.SunBurn)
        {
            throw new ArgumentException($"{kind} has its own state and cannot be applied as a timed effect.", nameof(kind));
        }
        _effects[kind] = new TimedEffectState(untilUtc);
    }

    public bool TryApply(PlayerStatusEffectKind kind, DateTime nowUtc, double durationSeconds)
    {
        if (IsActive(kind, nowUtc))
        {
            return false;
        }
        Apply(kind, nowUtc.AddSeconds(durationSeconds));
        return true;
    }

    public bool HasSleep() => _effects.ContainsKey(PlayerStatusEffectKind.Sleep);

    internal void StartSleep() => _effects.TryAdd(PlayerStatusEffectKind.Sleep, new SleepRecoveryState(DateTime.MinValue, 0));

    internal void StopSleep() => _effects.Remove(PlayerStatusEffectKind.Sleep);

    public int GetSleepRecovery(DateTime nowUtc, int health, int maxHealth)
    {
        if (_effects.GetValueOrDefault(PlayerStatusEffectKind.Sleep) is not SleepRecoveryState sleep)
        {
            return 0;
        }

        if (sleep.StartedAtUtc == DateTime.MinValue)
        {
            _effects[PlayerStatusEffectKind.Sleep] = sleep with { StartedAtUtc = nowUtc };
            return 0;
        }

        double elapsed = (nowUtc - sleep.StartedAtUtc).TotalSeconds;
        if (elapsed < SwarmSleepWarmupSeconds)
        {
            return 0;
        }

        int due = (int)Math.Floor(elapsed - SwarmSleepWarmupSeconds) + 1;
        int pending = due - sleep.ProcessedRecoveryCount;
        if (pending <= 0)
        {
            return 0;
        }

        _effects[PlayerStatusEffectKind.Sleep] = sleep with { ProcessedRecoveryCount = due };
        if (IsActive(PlayerStatusEffectKind.HealingBlocked, nowUtc) || health >= maxHealth)
        {
            return 0;
        }

        int perTick = Math.Max(1, (int)MathF.Round(maxHealth * SwarmSleepRecoveryRatioPerSecond));
        return Math.Min(perTick * pending, maxHealth - health);
    }

    internal SunBurnState? SunBurn => _effects.GetValueOrDefault(PlayerStatusEffectKind.SunBurn) is SunBurnState burn && burn.IsActive(default) ? burn : null;

    internal void ApplySunBurn(SunBurnState burn) => _effects[PlayerStatusEffectKind.SunBurn] = burn;

    internal abstract record StatusEffectState
    {
        public abstract bool IsActive(DateTime nowUtc);
    }

    private sealed record TimedEffectState(DateTime UntilUtc) : StatusEffectState
    {
        public override bool IsActive(DateTime nowUtc) => nowUtc < UntilUtc;
    }

    private sealed record SleepRecoveryState(DateTime StartedAtUtc, int ProcessedRecoveryCount) : StatusEffectState
    {
        public override bool IsActive(DateTime nowUtc) => true;
    }

    internal sealed record SunBurnState(long OwnerId, int WeaponItemId, AreaType Area, DateTime UntilUtc, DateTime NextTickAtUtc) : StatusEffectState
    {
        public override bool IsActive(DateTime nowUtc) => NextTickAtUtc <= UntilUtc;
    }
}
