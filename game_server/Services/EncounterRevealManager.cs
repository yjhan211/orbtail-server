using System.Collections.Concurrent;
using network.common;
using network.common.data.models;

namespace game_server.services;

public sealed class EncounterRevealManager
{
    public const int CorridorHintEventType = 1;
    public const int CorridorRevealEventType = 2;
    public const int RoomRevealEventType = 3;
    public const int RoomDiscoveryEventType = 4;
    public const int RoomEncounterEventType = 5;
    public const int RoomEncounterInspectResultEventType = 6;
    public const int RoomEncounterLeaveResultEventType = 7;

    public const int PairCooldownSeconds = 10;
    public const int CorridorRevealDelayMs = 900;

    private const int RoomEncounterRollPercent = 100;
    public const int RoomDiscoveryReadyAction = 0;
    public const int RoomEncounterActionInspect = 1;
    public const int RoomEncounterActionLeave = 2;
    public const int RoomDiscoveryActionHidePresence = 3;
    public const int RoomDiscoveryActionFace = 4;
    public const int RoomDiscoveryActionLeave = 5;
    public const int RoomDiscoveryHideEncounterChancePercent = 50;
    private const float CorridorHintDistance = 3.2f;
    private const float CorridorRevealDistance = 1.45f;
    private const int CorridorHintCooldownSeconds = 4;

    private readonly ConcurrentDictionary<PairKey, DateTime> _pairCooldownUntil = new();
    private readonly ConcurrentDictionary<PairKey, DateTime> _corridorHintCooldownUntil = new();
    private readonly ConcurrentDictionary<RoomDiscoveryKey, RoomDiscoveryPending> _pendingRoomDiscoveries = new();
    private readonly ConcurrentDictionary<RoomEncounterTurnKey, RoomEncounterTurnPending> _pendingRoomEncounterTurns =
        new();
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

        var now = DateTime.UtcNow;
        var candidates = candidatePlayerIds
            .Where(id => id != 0 && id != actorPlayerId)
            .Distinct()
            .Where(id => !IsPairCoolingDown(matchingId, actorPlayerId, id, now))
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
            SetPairCooldown(matchingId, actorPlayerId, targetPlayerId, now);
        }

        return true;
    }

    public void RegisterPendingRoomDiscovery(long matchingId, long discovererPlayerId, long targetPlayerId,
        AreaType area)
    {
        if (matchingId <= 0 || discovererPlayerId == 0 || targetPlayerId == 0 ||
            discovererPlayerId == targetPlayerId || area == AreaType.None)
            return;

        var key = RoomDiscoveryKey.Create(matchingId, discovererPlayerId, targetPlayerId);
        _pendingRoomDiscoveries[key] = new RoomDiscoveryPending(matchingId, discovererPlayerId, targetPlayerId, area,
            DateTime.UtcNow);
    }

    public bool TryConsumePendingRoomDiscovery(long matchingId, long discovererPlayerId, long targetPlayerId,
        AreaType area, out RoomDiscoveryResolution resolution)
    {
        resolution = default;
        if (matchingId <= 0 || discovererPlayerId == 0 || targetPlayerId == 0 || area == AreaType.None)
            return false;

        var key = RoomDiscoveryKey.Create(matchingId, discovererPlayerId, targetPlayerId);
        if (!_pendingRoomDiscoveries.TryGetValue(key, out var pending))
            return false;

        if (pending.Area != area)
            return false;

        if (!_pendingRoomDiscoveries.TryRemove(key, out pending))
            return false;

        resolution = new RoomDiscoveryResolution(
            pending.MatchingId,
            pending.DiscovererPlayerId,
            pending.TargetPlayerId,
            pending.Area,
            pending.CreatedAtUtc);
        return true;
    }

    public bool HasPendingRoomDiscovery(long matchingId, long discovererPlayerId, long targetPlayerId,
        AreaType area)
    {
        if (matchingId <= 0 || discovererPlayerId == 0 || targetPlayerId == 0 || area == AreaType.None)
            return false;

        var key = RoomDiscoveryKey.Create(matchingId, discovererPlayerId, targetPlayerId);
        return _pendingRoomDiscoveries.TryGetValue(key, out var pending) && pending.Area == area;
    }

    public bool ShouldDiscoveryHideTriggerEncounter()
    {
        lock (_rng)
        {
            return _rng.Next(100) < RoomDiscoveryHideEncounterChancePercent;
        }
    }

    public void RegisterPendingRoomEncounterTurn(long matchingId, long playerA, long playerB, AreaType area)
    {
        if (matchingId <= 0 || playerA == 0 || playerB == 0 || playerA == playerB || area == AreaType.None)
            return;

        var key = RoomEncounterTurnKey.Create(matchingId, playerA, playerB);
        _pendingRoomEncounterTurns[key] = new RoomEncounterTurnPending(
            matchingId,
            Math.Min(playerA, playerB),
            Math.Max(playerA, playerB),
            area,
            DateTime.UtcNow);
    }

    public bool TrySubmitRoomEncounterChoice(long matchingId, long actorPlayerId, long otherPlayerId,
        AreaType area, int actionType, out RoomEncounterTurnResolution resolution)
    {
        resolution = default;
        if (matchingId <= 0 || actorPlayerId == 0 || otherPlayerId == 0 || area == AreaType.None)
            return false;

        var key = RoomEncounterTurnKey.Create(matchingId, actorPlayerId, otherPlayerId);
        if (!_pendingRoomEncounterTurns.TryGetValue(key, out var pending) || pending.Area != area)
            return false;

        pending.Choices[actorPlayerId] = NormalizeRoomEncounterAction(actionType);
        if (!pending.Choices.ContainsKey(pending.PlayerA) || !pending.Choices.ContainsKey(pending.PlayerB))
            return true;

        if (!_pendingRoomEncounterTurns.TryRemove(key, out pending))
            return false;

        resolution = pending.ToResolution();
        return true;
    }

    public bool TryConsumePendingRoomEncounterTurn(long matchingId, long playerA, long playerB, AreaType area,
        out RoomEncounterTurnResolution resolution)
    {
        resolution = default;
        if (matchingId <= 0 || playerA == 0 || playerB == 0 || area == AreaType.None)
            return false;

        var key = RoomEncounterTurnKey.Create(matchingId, playerA, playerB);
        if (!_pendingRoomEncounterTurns.TryGetValue(key, out var pending) || pending.Area != area)
            return false;

        if (!_pendingRoomEncounterTurns.TryRemove(key, out pending))
            return false;

        resolution = pending.ToResolution();
        return true;
    }

    public List<long> ConsumePendingRoomDiscoverers(long matchingId, long targetPlayerId, AreaType area)
    {
        var result = new List<long>();
        if (matchingId <= 0 || targetPlayerId == 0 || area == AreaType.None)
            return result;

        foreach (var entry in _pendingRoomDiscoveries)
        {
            var pending = entry.Value;
            if (pending.MatchingId != matchingId || pending.TargetPlayerId != targetPlayerId || pending.Area != area)
                continue;

            if (_pendingRoomDiscoveries.TryRemove(entry.Key, out _))
                result.Add(pending.DiscovererPlayerId);
        }

        return result;
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

        foreach (var key in _pendingRoomDiscoveries.Keys)
            if (key.MatchingId == matchingId)
                _pendingRoomDiscoveries.TryRemove(key, out _);

        foreach (var key in _pendingRoomEncounterTurns.Keys)
            if (key.MatchingId == matchingId)
                _pendingRoomEncounterTurns.TryRemove(key, out _);
    }

    public static int NormalizeRoomEncounterAction(int actionType)
    {
        return actionType == RoomEncounterActionLeave
            ? RoomEncounterActionLeave
            : RoomEncounterActionInspect;
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

    private readonly record struct RoomDiscoveryKey(long MatchingId, long DiscovererPlayerId, long TargetPlayerId)
    {
        public static RoomDiscoveryKey Create(long matchingId, long discovererPlayerId, long targetPlayerId)
        {
            return new RoomDiscoveryKey(matchingId, discovererPlayerId, targetPlayerId);
        }
    }

    private readonly record struct RoomDiscoveryPending(
        long MatchingId,
        long DiscovererPlayerId,
        long TargetPlayerId,
        AreaType Area,
        DateTime CreatedAtUtc);

    private readonly record struct RoomEncounterTurnKey(long MatchingId, long A, long B)
    {
        public static RoomEncounterTurnKey Create(long matchingId, long a, long b)
        {
            return a <= b
                ? new RoomEncounterTurnKey(matchingId, a, b)
                : new RoomEncounterTurnKey(matchingId, b, a);
        }
    }

    private sealed class RoomEncounterTurnPending
    {
        public RoomEncounterTurnPending(long matchingId, long playerA, long playerB, AreaType area,
            DateTime createdAtUtc)
        {
            MatchingId = matchingId;
            PlayerA = playerA;
            PlayerB = playerB;
            Area = area;
            CreatedAtUtc = createdAtUtc;
        }

        public long MatchingId { get; }
        public long PlayerA { get; }
        public long PlayerB { get; }
        public AreaType Area { get; }
        public DateTime CreatedAtUtc { get; }
        public ConcurrentDictionary<long, int> Choices { get; } = new();

        public RoomEncounterTurnResolution ToResolution()
        {
            return new RoomEncounterTurnResolution(
                MatchingId,
                PlayerA,
                PlayerB,
                Area,
                Choices.TryGetValue(PlayerA, out int actionA) ? actionA : RoomEncounterActionInspect,
                Choices.TryGetValue(PlayerB, out int actionB) ? actionB : RoomEncounterActionInspect,
                CreatedAtUtc,
                DateTime.UtcNow);
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

public readonly record struct RoomDiscoveryResolution(
    long MatchingId,
    long DiscovererPlayerId,
    long TargetPlayerId,
    AreaType Area,
    DateTime CreatedAtUtc);

public readonly record struct RoomEncounterTurnResolution(
    long MatchingId,
    long PlayerA,
    long PlayerB,
    AreaType Area,
    int PlayerAAction,
    int PlayerBAction,
    DateTime CreatedAtUtc,
    DateTime ResolvedAtUtc)
{
    public int GetActionFor(long playerId)
    {
        if (playerId == PlayerA) return PlayerAAction;
        if (playerId == PlayerB) return PlayerBAction;
        return EncounterRevealManager.RoomEncounterActionInspect;
    }

    public long GetOtherPlayer(long playerId)
    {
        if (playerId == PlayerA) return PlayerB;
        if (playerId == PlayerB) return PlayerA;
        return 0;
    }
}
