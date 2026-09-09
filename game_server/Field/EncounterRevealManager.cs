using System.Collections.Concurrent;
using network.common;
using network.common.data.models;

namespace game_server.field;

/// <summary>매치 런타임의 플레이어 쌍별 쿨타임을 사용해 복도 발견·힌트를 판정한다.</summary>
public sealed class EncounterRevealManager
{
    public const int CorridorHintEventType = 1;
    public const int CorridorRevealEventType = 2;

    public const int PairCooldownSeconds = 10;
    public const int CorridorRevealDelayMs = 900;

    private const float CorridorHintDistance = 3.2f;
    private const float CorridorRevealDistance = 1.45f;
    private const int CorridorHintCooldownSeconds = 4;

    private MatchEncounterState? _state = new();

    internal void Release() => Interlocked.Exchange(ref _state, null);


    public CorridorEncounterDecision ResolveCorridorEncounter(
        long actorPlayerId,
        Vector3f actorPosition,
        IEnumerable<(long PlayerId, Vector3f Position)> candidates,
        int riskEventChanceDownPercent = 0,
        int escapeChanceAddPercent = 0)
    {
        if (Volatile.Read(ref _state) is not { } state)
            return CorridorEncounterDecision.None;
        var now = DateTime.UtcNow;
        CorridorEncounterDecision bestHint = CorridorEncounterDecision.None;

        foreach (var candidate in candidates)
        {
            if (candidate.PlayerId == 0 || candidate.PlayerId == actorPlayerId)
                continue;

            float sqrDistance = SqrDistance(actorPosition, candidate.Position);
            if (sqrDistance <= CorridorRevealDistance * CorridorRevealDistance &&
                !IsPairCoolingDown(state, actorPlayerId, candidate.PlayerId, now))
            {
                if (ShouldSuppressReveal(state, riskEventChanceDownPercent, escapeChanceAddPercent))
                    continue;

                SetPairCooldown(state, actorPlayerId, candidate.PlayerId, now);
                return new CorridorEncounterDecision(
                    CorridorRevealEventType,
                    candidate.PlayerId,
                    PairCooldownSeconds,
                    0);
            }

            if (sqrDistance > CorridorHintDistance * CorridorHintDistance)
                continue;

            var key = PairKey.Create(actorPlayerId, candidate.PlayerId);
            if (state.CorridorHintCooldownUntil.TryGetValue(key, out var hintUntil) && hintUntil > now)
                continue;

            state.CorridorHintCooldownUntil[key] = now.AddSeconds(CorridorHintCooldownSeconds);
            bestHint = new CorridorEncounterDecision(
                CorridorHintEventType,
                candidate.PlayerId,
                CorridorHintCooldownSeconds,
                CorridorRevealDelayMs);
            break;
        }

        return bestHint;
    }

    private bool ShouldSuppressReveal(MatchEncounterState state, int riskEventChanceDownPercent, int escapeChanceAddPercent)
    {
        int riskReduction = Math.Clamp(riskEventChanceDownPercent, 0, 95);
        int escapeChance = Math.Clamp(escapeChanceAddPercent, 0, 95);

        if (riskReduction <= 0 && escapeChance <= 0)
            return false;

        lock (state.Random)
        {
            if (riskReduction > 0 && state.Random.Next(100) < riskReduction)
                return true;

            return escapeChance > 0 && state.Random.Next(100) < escapeChance;
        }
    }

    private bool IsPairCoolingDown(MatchEncounterState state, long a, long b, DateTime now)
    {
        var key = PairKey.Create(a, b);
        if (!state.PairCooldownUntil.TryGetValue(key, out var until))
            return false;

        if (until > now)
            return true;

        state.PairCooldownUntil.TryRemove(key, out _);
        return false;
    }

    private void SetPairCooldown(MatchEncounterState state, long a, long b, DateTime now)
    {
        var key = PairKey.Create(a, b);
        state.PairCooldownUntil[key] = now.AddSeconds(PairCooldownSeconds);
    }

    private static float SqrDistance(Vector3f a, Vector3f b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    internal sealed class MatchEncounterState
    {
        public ConcurrentDictionary<PairKey, DateTime> PairCooldownUntil { get; } = new();
        public ConcurrentDictionary<PairKey, DateTime> CorridorHintCooldownUntil { get; } = new();
        public Random Random { get; } = new();
    }
    internal readonly record struct PairKey(long A, long B)
    {
        public static PairKey Create(long a, long b)
        {
            return a <= b
                ? new PairKey(a, b)
                : new PairKey(b, a);
        }
    }

}

public readonly record struct CorridorEncounterDecision(
    int EventType,
    long TargetPlayerId,
    int CooldownSeconds,
    int RevealDelayMs)
{
    public static CorridorEncounterDecision None => new(0, 0, 0, 0);
    public bool HasEvent => EventType != 0 && TargetPlayerId != 0;
}
