using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
///     Preserves the process-wide attack-event id sequence. Match runtime recreation must not
///     reuse an id that can still appear in logs or delayed work from an earlier match.
/// </summary>
internal sealed class SwarmAttackEventIdSequence
{
    private long _lastEventId;

    public long Allocate() => Interlocked.Increment(ref _lastEventId);
}

internal sealed record SwarmAttackParticipantSnapshot(
    long ItemUid,
    int ItemId,
    int SnapshotOrdinal,
    int CurrentOrdinal,
    int Tier,
    Vector3f Origin,
    int Damage);

internal sealed class SwarmPvpAttackEvent
{
    public long AttackEventId;
    public long MatchingId;
    public long AttackerId;
    public long TargetId;
    public AreaType Area;
    public OrbColor Color;
    public DateTime StartedAtUtc;
    public DateTime LaunchAtUtc;
    public int SnapshotDamageBudget;
    public List<SwarmAttackParticipantSnapshot> Participants = new();
}

internal sealed record PendingSwarmAttackVisual(
    long AttackEventId,
    long AttackerId,
    long TargetId,
    AreaType Area,
    OrbColor Color,
    int Kind,
    int Ordinal,
    int Tier,
    Vector3f Origin,
    float Radius,
    DateTime DueAtUtc);

internal sealed record PendingSwarmAttackHit(
    long AttackEventId,
    long FeedbackTargetId,
    IReadOnlyList<ProximityCombatAttack> Attacks,
    DateTime DueAtUtc);

/// <summary>
///     Owns one match's dormant attribute-attack lifecycle: active telegraphs, cooldowns, sticky
///     targets, and delayed visual/hit work. Keys are match-local; the injected id sequence remains
///     process-wide. The enclosing match execution gate serializes every mutation.
/// </summary>
public sealed class SwarmPvpAttackEventState
{
    private readonly SwarmAttackEventIdSequence _eventIds;
    private readonly Dictionary<(long AttackerId, OrbColor Color), SwarmPvpAttackEvent> _activeEvents = new();
    private readonly Dictionary<(long AttackerId, OrbColor Color), DateTime> _nextReadyAtUtc = new();
    private readonly Dictionary<long, DateTime> _nextAttributeAtUtc = new();
    private readonly Dictionary<long, long> _currentTargets = new();
    private readonly List<PendingSwarmAttackVisual> _pendingVisuals = new();
    private readonly List<PendingSwarmAttackHit> _pendingHits = new();

    internal SwarmPvpAttackEventState(SwarmAttackEventIdSequence eventIds)
    {
        _eventIds = eventIds ?? throw new ArgumentNullException(nameof(eventIds));
    }

    public bool IsEmpty =>
        _activeEvents.Count == 0 &&
        _nextReadyAtUtc.Count == 0 &&
        _nextAttributeAtUtc.Count == 0 &&
        _currentTargets.Count == 0 &&
        _pendingVisuals.Count == 0 &&
        _pendingHits.Count == 0;

    internal long AllocateAttackEventId() => _eventIds.Allocate();

    internal bool IsAttributeBlocked(long attackerId, DateTime nowUtc) =>
        _nextAttributeAtUtc.TryGetValue(attackerId, out DateTime nextAttributeAtUtc) &&
        nowUtc < nextAttributeAtUtc;

    internal bool IsColorBlocked(long attackerId, OrbColor color, DateTime nowUtc)
    {
        var key = (attackerId, color);
        return _activeEvents.ContainsKey(key) ||
               _nextReadyAtUtc.TryGetValue(key, out DateTime nextReadyAtUtc) && nowUtc < nextReadyAtUtc;
    }

    internal void RegisterEvent(
        SwarmPvpAttackEvent attackEvent,
        DateTime nextReadyAtUtc,
        DateTime nextAttributeAtUtc)
    {
        var key = (attackEvent.AttackerId, attackEvent.Color);
        _activeEvents[key] = attackEvent;
        _nextReadyAtUtc[key] = nextReadyAtUtc;
        _nextAttributeAtUtc[attackEvent.AttackerId] = nextAttributeAtUtc;
        _currentTargets[attackEvent.AttackerId] = attackEvent.TargetId;
    }

    internal bool TryGetCurrentTarget(long attackerId, out long targetId) =>
        _currentTargets.TryGetValue(attackerId, out targetId);

    internal void ClearCurrentTarget(long attackerId) =>
        _currentTargets.Remove(attackerId);

    internal IEnumerable<SwarmPvpAttackEvent> TakeReadyEvents(DateTime nowUtc)
    {
        List<SwarmPvpAttackEvent> readyEvents = _activeEvents.Values
            .Where(attackEvent => nowUtc >= attackEvent.LaunchAtUtc)
            .ToList();
        foreach (SwarmPvpAttackEvent attackEvent in readyEvents)
        {
            _activeEvents.Remove((attackEvent.AttackerId, attackEvent.Color));
            yield return attackEvent;
        }
    }

    internal void EnqueueVisual(PendingSwarmAttackVisual visual) =>
        _pendingVisuals.Add(visual);

    internal IEnumerable<PendingSwarmAttackVisual> TakeDueVisuals(DateTime nowUtc)
    {
        for (int index = _pendingVisuals.Count - 1; index >= 0; index--)
        {
            PendingSwarmAttackVisual visual = _pendingVisuals[index];
            if (nowUtc < visual.DueAtUtc)
                continue;

            _pendingVisuals.RemoveAt(index);
            yield return visual;
        }
    }

    internal void EnqueueHit(PendingSwarmAttackHit hit) =>
        _pendingHits.Add(hit);

    internal IEnumerable<PendingSwarmAttackHit> TakeDueHits(DateTime nowUtc)
    {
        for (int index = _pendingHits.Count - 1; index >= 0; index--)
        {
            PendingSwarmAttackHit hit = _pendingHits[index];
            if (nowUtc < hit.DueAtUtc)
                continue;

            _pendingHits.RemoveAt(index);
            yield return hit;
        }
    }
}
