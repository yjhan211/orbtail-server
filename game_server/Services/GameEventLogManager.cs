using System.Collections.Concurrent;
using System.Globalization;

namespace game_server.services;

/// <summary>
///     Keeps recent in-game event logs for ops/debugging and derives social-evidence logs from raw actions.
/// </summary>
public class GameEventLogManager
{
    private const int MaxEventsPerMatching = 500;
    private const int FollowInWindowSeconds = 6;

    private readonly ConcurrentDictionary<long, MatchingEventLog> _logs = new();
    private long _nextSeq;

    public void SetPlayerArea(long matchingId, long playerId, string area)
    {
        var log = _logs.GetOrAdd(matchingId, _ => new MatchingEventLog());
        log.SetPlayerArea(playerId, area, DateTimeOffset.UtcNow);
    }

    public void LogMove(long matchingId, long playerId, string fromArea, string toArea, bool isBot)
    {
        var log = _logs.GetOrAdd(matchingId, _ => new MatchingEventLog());
        var now = DateTimeOffset.UtcNow;

        log.AddMoveAndDerived(
            playerId,
            fromArea,
            toArea,
            isBot,
            now,
            FollowInWindowSeconds,
            CreateEntry);
    }

    public void LogResource(long matchingId, long playerId, int staminaDelta, int corruptionDelta,
        int stamina, int corruption, bool staminaConverted, string reason, bool isBot)
    {
        var parts = new List<string>();
        if (staminaDelta != 0) parts.Add($"체력{(staminaDelta >= 0 ? "+" : "")}{staminaDelta}");
        if (corruptionDelta != 0) parts.Add($"오염{(corruptionDelta >= 0 ? "+" : "")}{corruptionDelta}");
        parts.Add($"(체력 {stamina}/오염 {corruption})");
        if (staminaConverted) parts.Add("[전환]");
        if (!string.IsNullOrEmpty(reason)) parts.Add($"<{reason}>");
        Append(matchingId, "RESOURCE", playerId, isBot, string.Join(" ", parts));
    }

    public void LogMission(long matchingId, long playerId, string description, bool isBot)
    {
        Append(matchingId, "MISSION", playerId, isBot, description);
    }

    public void LogSchoolActivityStart(long matchingId, long playerId, int taskId, string area, int interactId,
        string activityReason, bool isBot)
    {
        var log = _logs.GetOrAdd(matchingId, _ => new MatchingEventLog());
        var now = DateTimeOffset.UtcNow;
        log.AddSchoolActivityStart(
            playerId,
            taskId,
            area,
            interactId,
            activityReason,
            isBot,
            now,
            CreateEntry);
    }

    public void LogSchoolActivityComplete(long matchingId, long playerId, int taskId, string area, int interactId,
        float scoreDelta, int contributionDelta, string activityReason, bool isBot)
    {
        var log = _logs.GetOrAdd(matchingId, _ => new MatchingEventLog());
        var now = DateTimeOffset.UtcNow;
        log.AddSchoolActivityComplete(
            playerId,
            taskId,
            area,
            interactId,
            scoreDelta,
            contributionDelta,
            activityReason,
            isBot,
            now,
            CreateEntry);
    }

    public void LogElimination(long matchingId, long playerId, string reason, bool isBot)
    {
        Append(matchingId, "ELIMINATE", playerId, isBot, reason);
    }

    public void LogInteraction(long matchingId, long playerId, string description, bool isBot)
    {
        Append(matchingId, "INTERACT", playerId, isBot, description);
    }

    public GameEventEntry LogStatement(
        long matchingId,
        int roundId,
        long speakerPlayerId,
        long listenerPlayerId,
        string area,
        string questionId,
        string questionText,
        string answerType,
        string answerText,
        IReadOnlyCollection<long> linkedLogIds,
        bool isBot)
    {
        var linked = linkedLogIds.Distinct().ToList();
        var linkedText = linked.Count == 0 ? "-" : string.Join(",", linked);
        var description =
            $"{FormatPlayer(speakerPlayerId)} -> {FormatPlayer(listenerPlayerId)} {questionId} {answerType} \"{answerText}\" linkedLogs={linkedText}";

        return Append(matchingId, "STATEMENT", speakerPlayerId, isBot, description, entry =>
        {
            entry.StatementId = entry.Seq;
            entry.RoundId = roundId;
            entry.SpeakerPlayerId = speakerPlayerId;
            entry.ListenerPlayerId = listenerPlayerId;
            entry.Area = area;
            entry.AreaId = area;
            entry.QuestionId = questionId;
            entry.QuestionText = questionText;
            entry.AnswerType = answerType;
            entry.AnswerText = answerText;
            entry.LinkedLogIds = linked;
            entry.SaidAtUnixMs = entry.TimestampUnixMs;
        });
    }

    public void LogClosure(long matchingId, string area)
    {
        Append(matchingId, "CLOSURE", 0, false, $"구역 폐쇄: {area}");
    }

    public void LogSystem(long matchingId, string description)
    {
        Append(matchingId, "SYSTEM", 0, false, description);
    }

    public List<GameEventEntry> GetRecent(long matchingId, int limit = MaxEventsPerMatching, long? sinceSeq = null)
    {
        if (!_logs.TryGetValue(matchingId, out var log)) return new List<GameEventEntry>();
        return log.Snapshot(limit, sinceSeq);
    }

    public void Clear(long matchingId)
    {
        _logs.TryRemove(matchingId, out _);
    }

    private GameEventEntry Append(long matchingId, string type, long playerId, bool isBot, string description,
        Action<GameEventEntry>? configure = null)
    {
        var entry = CreateEntry(type, playerId, isBot, description, DateTimeOffset.UtcNow, configure);
        var log = _logs.GetOrAdd(matchingId, _ => new MatchingEventLog());
        log.Add(entry);
        return entry;
    }

    private GameEventEntry CreateEntry(string type, long playerId, bool isBot, string description,
        DateTimeOffset timestamp, Action<GameEventEntry>? configure = null)
    {
        var entry = new GameEventEntry
        {
            Seq = Interlocked.Increment(ref _nextSeq),
            TimestampUnixMs = timestamp.ToUnixTimeMilliseconds(),
            Type = type,
            PlayerId = playerId,
            ActorPlayerId = playerId,
            IsBot = isBot,
            Description = description
        };

        configure?.Invoke(entry);
        return entry;
    }

    private static string FormatPlayer(long playerId) => $"Player{playerId}";

    private sealed class MatchingEventLog
    {
        private readonly Dictionary<ActivityKey, DateTimeOffset> _activeSchoolActivities = new();
        private readonly LinkedList<GameEventEntry> _entries = new();
        private readonly Dictionary<long, AreaPresenceState> _playerAreas = new();
        private readonly object _lock = new();

        public void SetPlayerArea(long playerId, string area, DateTimeOffset timestamp)
        {
            lock (_lock)
            {
                if (!IsTrackableArea(area))
                {
                    _playerAreas.Remove(playerId);
                    return;
                }

                if (_playerAreas.TryGetValue(playerId, out var existing) &&
                    string.Equals(existing.Area, area, StringComparison.Ordinal))
                    return;

                _playerAreas[playerId] = new AreaPresenceState(area, timestamp);
            }
        }

        public void AddMoveAndDerived(
            long playerId,
            string fromArea,
            string toArea,
            bool isBot,
            DateTimeOffset timestamp,
            int followInWindowSeconds,
            Func<string, long, bool, string, DateTimeOffset, Action<GameEventEntry>?, GameEventEntry> createEntry)
        {
            lock (_lock)
            {
                var rawMove = createEntry(
                    "MOVE",
                    playerId,
                    isBot,
                    $"{fromArea} -> {toArea}",
                    timestamp,
                    entry =>
                    {
                        entry.FromArea = fromArea;
                        entry.ToArea = toArea;
                    });
                AddNoLock(rawMove);

                if (_playerAreas.TryGetValue(playerId, out var previous) && IsTrackableArea(previous.Area))
                {
                    var durationSeconds = Math.Max(0, (timestamp - previous.EnteredAt).TotalSeconds);
                    var areaStay = createEntry(
                        "AREA_STAY",
                        playerId,
                        isBot,
                        $"{FormatPlayer(playerId)} stayed in {previous.Area} for {FormatSeconds(durationSeconds)}.",
                        timestamp,
                        entry =>
                        {
                            entry.Area = previous.Area;
                            entry.EnteredAtUnixMs = previous.EnteredAt.ToUnixTimeMilliseconds();
                            entry.ExitedAtUnixMs = timestamp.ToUnixTimeMilliseconds();
                            entry.DurationSeconds = durationSeconds;
                            entry.SourceEventSeq = rawMove.Seq;
                        });
                    AddNoLock(areaStay);
                }

                if (!IsTrackableArea(toArea))
                {
                    _playerAreas.Remove(playerId);
                    return;
                }

                var alreadyPresent = _playerAreas
                    .Where(pair => pair.Key != playerId && string.Equals(pair.Value.Area, toArea, StringComparison.Ordinal))
                    .OrderBy(pair => pair.Value.EnteredAt)
                    .ToList();
                var alreadyPresentIds = alreadyPresent.Select(pair => pair.Key).ToList();

                var areaEnter = createEntry(
                    "AREA_ENTER",
                    playerId,
                    isBot,
                    BuildAreaEnterDescription(playerId, fromArea, toArea, alreadyPresentIds),
                    timestamp,
                    entry =>
                    {
                        entry.FromArea = fromArea;
                        entry.ToArea = toArea;
                        entry.Area = toArea;
                        entry.EnteredAtUnixMs = timestamp.ToUnixTimeMilliseconds();
                        entry.AlreadyPresentPlayerIds = alreadyPresentIds;
                        entry.SourceEventSeq = rawMove.Seq;
                    });
                AddNoLock(areaEnter);

                if (alreadyPresentIds.Count > 0)
                {
                    var encounter = createEntry(
                        "ENCOUNTER",
                        playerId,
                        isBot,
                        $"{FormatPlayer(playerId)} encountered {FormatPlayers(alreadyPresentIds)} in {toArea}.",
                        timestamp,
                        entry =>
                        {
                            entry.Area = toArea;
                            entry.EncounteredPlayerIds = alreadyPresentIds;
                            entry.OccurredAtUnixMs = timestamp.ToUnixTimeMilliseconds();
                            entry.SourceEventSeq = areaEnter.Seq;
                        });
                    AddNoLock(encounter);
                }

                var recentEntries = alreadyPresent
                    .Select(pair => new
                    {
                        PlayerId = pair.Key,
                        SecondsAfter = (timestamp - pair.Value.EnteredAt).TotalSeconds
                    })
                    .Where(entry => entry.SecondsAfter >= 0 && entry.SecondsAfter <= followInWindowSeconds)
                    .OrderBy(entry => entry.SecondsAfter)
                    .ToList();

                if (recentEntries.Count > 0)
                {
                    var recentIds = recentEntries.Select(entry => entry.PlayerId).ToList();
                    var followIn = createEntry(
                        "FOLLOW_IN_CANDIDATE",
                        playerId,
                        isBot,
                        BuildFollowInDescription(playerId, toArea, recentEntries
                            .Select(entry => (entry.PlayerId, entry.SecondsAfter))
                            .ToList()),
                        timestamp,
                        entry =>
                        {
                            entry.Area = toArea;
                            entry.RecentPlayerIds = recentIds;
                            entry.OccurredAtUnixMs = timestamp.ToUnixTimeMilliseconds();
                            entry.SourceEventSeq = areaEnter.Seq;
                        });
                    AddNoLock(followIn);
                }

                _playerAreas[playerId] = new AreaPresenceState(toArea, timestamp);
            }
        }

        public void AddSchoolActivityStart(
            long playerId,
            int taskId,
            string area,
            int interactId,
            string activityReason,
            bool isBot,
            DateTimeOffset timestamp,
            Func<string, long, bool, string, DateTimeOffset, Action<GameEventEntry>?, GameEventEntry> createEntry)
        {
            lock (_lock)
            {
                _activeSchoolActivities[new ActivityKey(playerId, taskId, interactId)] = timestamp;
                AddNoLock(createEntry(
                    "SCHOOL_ACTIVITY_START",
                    playerId,
                    isBot,
                    $"{FormatPlayer(playerId)} started school activity {taskId} in {area}.",
                    timestamp,
                    entry =>
                    {
                        entry.Area = area;
                        entry.TaskId = taskId;
                        entry.ActivityId = interactId;
                        entry.ActivityReason = activityReason;
                        entry.StartedAtUnixMs = timestamp.ToUnixTimeMilliseconds();
                    }));
            }
        }

        public void AddSchoolActivityComplete(
            long playerId,
            int taskId,
            string area,
            int interactId,
            float scoreDelta,
            int contributionDelta,
            string activityReason,
            bool isBot,
            DateTimeOffset timestamp,
            Func<string, long, bool, string, DateTimeOffset, Action<GameEventEntry>?, GameEventEntry> createEntry)
        {
            lock (_lock)
            {
                DateTimeOffset? startedAt = null;
                var key = new ActivityKey(playerId, taskId, interactId);
                if (_activeSchoolActivities.Remove(key, out var recordedStart))
                    startedAt = recordedStart;

                var rawMission = createEntry(
                    "MISSION",
                    playerId,
                    isBot,
                    $"Checklist task completed: TaskId={taskId}, Score+{scoreDelta.ToString("0.##", CultureInfo.InvariantCulture)}, Contribution+{contributionDelta}",
                    timestamp,
                    null);
                AddNoLock(rawMission);

                // TODO: For non-interact checklist completions, add explicit START logs at the action trigger point.
                AddNoLock(createEntry(
                    "SCHOOL_ACTIVITY_COMPLETE",
                    playerId,
                    isBot,
                    $"{FormatPlayer(playerId)} completed school activity {taskId} in {area}.",
                    timestamp,
                    entry =>
                    {
                        entry.Area = area;
                        entry.TaskId = taskId;
                        entry.ActivityId = interactId;
                        entry.ActivityReason = activityReason;
                        entry.StartedAtUnixMs = startedAt?.ToUnixTimeMilliseconds();
                        entry.CompletedAtUnixMs = timestamp.ToUnixTimeMilliseconds();
                        entry.ScoreDelta = scoreDelta;
                        entry.ContributionDelta = contributionDelta;
                        entry.SourceEventSeq = rawMission.Seq;
                    }));
            }
        }

        public void Add(GameEventEntry entry)
        {
            lock (_lock)
            {
                AddNoLock(entry);
            }
        }

        public List<GameEventEntry> Snapshot(int limit, long? sinceSeq)
        {
            lock (_lock)
            {
                IEnumerable<GameEventEntry> q = _entries;
                if (sinceSeq.HasValue) q = q.Where(e => e.Seq > sinceSeq.Value);
                return q.Reverse().Take(limit).ToList();
            }
        }

        private void AddNoLock(GameEventEntry entry)
        {
            _entries.AddLast(entry);
            while (_entries.Count > MaxEventsPerMatching) _entries.RemoveFirst();
        }

        private static bool IsTrackableArea(string area) =>
            !string.IsNullOrWhiteSpace(area) &&
            !string.Equals(area, "None", StringComparison.OrdinalIgnoreCase);

        private static string BuildAreaEnterDescription(long playerId, string fromArea, string toArea,
            IReadOnlyCollection<long> alreadyPresentIds)
        {
            var description = $"{FormatPlayer(playerId)} entered {toArea} from {fromArea}.";
            return alreadyPresentIds.Count == 0
                ? description
                : $"{description} Already present: {FormatPlayers(alreadyPresentIds)}.";
        }

        private static string BuildFollowInDescription(
            long playerId,
            string area,
            IReadOnlyCollection<(long PlayerId, double SecondsAfter)> recentEntries)
        {
            var details = string.Join(", ", recentEntries.Select(entry =>
                $"{FormatPlayer(entry.PlayerId)} ({FormatSeconds(entry.SecondsAfter)} earlier)"));
            return $"{FormatPlayer(playerId)} entered {area} shortly after {details}.";
        }

        private static string FormatPlayers(IEnumerable<long> playerIds) =>
            string.Join(", ", playerIds.Select(FormatPlayer));

        private static string FormatPlayer(long playerId) => $"Player{playerId}";

        private static string FormatSeconds(double seconds) =>
            $"{seconds.ToString("0.#", CultureInfo.InvariantCulture)}s";
    }

    private readonly record struct ActivityKey(long PlayerId, int TaskId, int InteractId);

    private readonly record struct AreaPresenceState(string Area, DateTimeOffset EnteredAt);
}

public class GameEventEntry
{
    public long Seq { get; set; }
    public long TimestampUnixMs { get; set; }
    public string Type { get; set; } = "";
    public long PlayerId { get; set; }
    public long ActorPlayerId { get; set; }
    public bool IsBot { get; set; }
    public string Description { get; set; } = "";

    public string? FromArea { get; set; }
    public string? ToArea { get; set; }
    public string? Area { get; set; }
    public long? EnteredAtUnixMs { get; set; }
    public long? ExitedAtUnixMs { get; set; }
    public long? OccurredAtUnixMs { get; set; }
    public double? DurationSeconds { get; set; }
    public List<long>? AlreadyPresentPlayerIds { get; set; }
    public List<long>? EncounteredPlayerIds { get; set; }
    public List<long>? RecentPlayerIds { get; set; }
    public long? SourceEventSeq { get; set; }

    public int? TaskId { get; set; }
    public int? ActivityId { get; set; }
    public long? StartedAtUnixMs { get; set; }
    public long? CompletedAtUnixMs { get; set; }
    public float? ScoreDelta { get; set; }
    public int? ContributionDelta { get; set; }
    public string? ActivityReason { get; set; }

    public long? StatementId { get; set; }
    public int? RoundId { get; set; }
    public long? SpeakerPlayerId { get; set; }
    public long? ListenerPlayerId { get; set; }
    public string? AreaId { get; set; }
    public string? QuestionId { get; set; }
    public string? QuestionText { get; set; }
    public string? AnswerType { get; set; }
    public string? AnswerText { get; set; }
    public List<long>? LinkedLogIds { get; set; }
    public long? SaidAtUnixMs { get; set; }
}
