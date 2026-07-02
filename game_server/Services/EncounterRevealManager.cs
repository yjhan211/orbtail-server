using System.Collections.Concurrent;
using network.common;
using network.common.data.models;

namespace game_server.services;

public sealed class EncounterRevealManager
{
    public const int CorridorHintEventType = 1;
    public const int CorridorRevealEventType = 2;
    public const int RoomRevealEventType = 3;

    public const int PairCooldownSeconds = 10;
    public const int CorridorRevealDelayMs = 900;

    private const int RoomEncounterRollPercent = 100;
    private const float CorridorHintDistance = 3.2f;
    private const float CorridorRevealDistance = 1.45f;
    private const int CorridorHintCooldownSeconds = 4;

    private readonly ConcurrentDictionary<PairKey, DateTime> _pairCooldownUntil = new();
    private readonly ConcurrentDictionary<PairKey, DateTime> _corridorHintCooldownUntil = new();
    private readonly Random _rng = new();

    public bool TryResolveRoomEncounter(
        long matchingId,
        long actorPlayerId,
        AreaType area,
        IEnumerable<long> candidatePlayerIds,
        out long targetPlayerId,
        int riskEventChanceDownPercent = 0,
        int escapeChanceAddPercent = 0)
    {
        targetPlayerId = 0;
        if (area == AreaType.None || area.IsCorridor())
            return false;

        var candidates = candidatePlayerIds
            .Where(id => id != 0 && id != actorPlayerId)
            .Distinct()
            .ToList();
        if (candidates.Count == 0)
            return false;

        lock (_rng)
        {
            if (RoomEncounterRollPercent < 100 && _rng.Next(100) >= RoomEncounterRollPercent)
                return false;

            if (ShouldSuppressReveal(riskEventChanceDownPercent, escapeChanceAddPercent))
                return false;

            targetPlayerId = candidates[_rng.Next(candidates.Count)];
        }

        return true;
    }

    public CorridorEncounterDecision ResolveCorridorEncounter(
        long matchingId,
        long actorPlayerId,
        Vector3f actorPosition,
        IEnumerable<(long PlayerId, Vector3f Position)> candidates,
        int riskEventChanceDownPercent = 0,
        int escapeChanceAddPercent = 0)
    {
        var now = DateTime.UtcNow;
        CorridorEncounterDecision bestHint = CorridorEncounterDecision.None;

        foreach (var candidate in candidates)
        {
            if (candidate.PlayerId == 0 || candidate.PlayerId == actorPlayerId)
                continue;

            float sqrDistance = SqrDistance(actorPosition, candidate.Position);
            if (sqrDistance <= CorridorRevealDistance * CorridorRevealDistance &&
                !IsPairCoolingDown(matchingId, actorPlayerId, candidate.PlayerId, now))
            {
                if (ShouldSuppressReveal(riskEventChanceDownPercent, escapeChanceAddPercent))
                    continue;

                SetPairCooldown(matchingId, actorPlayerId, candidate.PlayerId, now);
                return new CorridorEncounterDecision(
                    CorridorRevealEventType,
                    candidate.PlayerId,
                    PairCooldownSeconds,
                    0);
            }

            if (sqrDistance > CorridorHintDistance * CorridorHintDistance)
                continue;

            var key = PairKey.Create(matchingId, actorPlayerId, candidate.PlayerId);
            if (_corridorHintCooldownUntil.TryGetValue(key, out var hintUntil) && hintUntil > now)
                continue;

            _corridorHintCooldownUntil[key] = now.AddSeconds(CorridorHintCooldownSeconds);
            bestHint = new CorridorEncounterDecision(
                CorridorHintEventType,
                candidate.PlayerId,
                CorridorHintCooldownSeconds,
                CorridorRevealDelayMs);
            break;
        }

        return bestHint;
    }

    private bool ShouldSuppressReveal(int riskEventChanceDownPercent, int escapeChanceAddPercent)
    {
        int riskReduction = Math.Clamp(riskEventChanceDownPercent, 0, 95);
        int escapeChance = Math.Clamp(escapeChanceAddPercent, 0, 95);

        if (riskReduction <= 0 && escapeChance <= 0)
            return false;

        lock (_rng)
        {
            if (riskReduction > 0 && _rng.Next(100) < riskReduction)
                return true;

            return escapeChance > 0 && _rng.Next(100) < escapeChance;
        }
    }

    public void ClearMatching(long matchingId)
    {
        foreach (var key in _pairCooldownUntil.Keys)
            if (key.MatchingId == matchingId)
                _pairCooldownUntil.TryRemove(key, out _);

        foreach (var key in _corridorHintCooldownUntil.Keys)
            if (key.MatchingId == matchingId)
                _corridorHintCooldownUntil.TryRemove(key, out _);
    }

    private bool IsPairCoolingDown(long matchingId, long a, long b, DateTime now)
    {
        var key = PairKey.Create(matchingId, a, b);
        if (!_pairCooldownUntil.TryGetValue(key, out var until))
            return false;

        if (until > now)
            return true;

        _pairCooldownUntil.TryRemove(key, out _);
        return false;
    }

    private void SetPairCooldown(long matchingId, long a, long b, DateTime now)
    {
        var key = PairKey.Create(matchingId, a, b);
        _pairCooldownUntil[key] = now.AddSeconds(PairCooldownSeconds);
    }

    private static float SqrDistance(Vector3f a, Vector3f b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private readonly record struct PairKey(long MatchingId, long A, long B)
    {
        public static PairKey Create(long matchingId, long a, long b)
        {
            return a <= b
                ? new PairKey(matchingId, a, b)
                : new PairKey(matchingId, b, a);
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
