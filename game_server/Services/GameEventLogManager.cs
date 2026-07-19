using System.Collections.Concurrent;
using System.Globalization;
using network.common.data;

namespace game_server.services;

/// <summary>
///     Keeps recent in-game event logs for ops/debugging and derives social-evidence logs from raw actions.
/// </summary>
public class GameEventLogManager
{
    private const int MaxEventsPerMatching = 500;
    private const int FollowInWindowSeconds = 6;

    private readonly ConcurrentDictionary<long, MatchingEventLog> _logs = new();
    private readonly ConcurrentDictionary<long, SurvivorCombatState> _survivorCombatStates = new();
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
        var occurredAt = DateTimeOffset.UtcNow;
        LogFirstSurvivorElimination(matchingId, playerId, reason, isBot, occurredAt);
        AppendAt(matchingId, "ELIMINATE", playerId, isBot, reason, occurredAt);
    }

    public void LogSurvivorTierReached(
        long matchingId,
        long playerId,
        int itemId,
        int tier,
        bool isBot,
        DateTimeOffset? occurredAt = null)
    {
        if (tier is < 2 or > 3)
            return;

        var timestamp = occurredAt ?? DateTimeOffset.UtcNow;
        var state = _survivorCombatStates.GetOrAdd(matchingId, _ => new SurvivorCombatState());
        lock (state.SyncRoot)
        {
            state.KnownPlayerIds.Add(playerId);
            if (tier == 2)
            {
                if (state.FirstTier2AtUnixMs.HasValue)
                    return;
                state.FirstTier2AtUnixMs = timestamp.ToUnixTimeMilliseconds();
            }
            else
            {
                if (state.FirstTier3AtUnixMs.HasValue)
                    return;
                state.FirstTier3AtUnixMs = timestamp.ToUnixTimeMilliseconds();
            }
        }

        AppendAt(
            matchingId,
            $"SURVIVOR_FIRST_T{tier}",
            playerId,
            isBot,
            $"First T{tier}: {FormatPlayer(playerId)} equipped or crafted Item{itemId}.",
            timestamp,
            entry =>
            {
                entry.WeaponItemId = itemId;
                entry.WeaponTier = tier;
                entry.IsFirstMilestone = true;
                entry.OccurredAtUnixMs = entry.TimestampUnixMs;
            });
    }

    public void LogSurvivorTargetAcquired(
        long matchingId,
        long attackerPlayerId,
        long targetPlayerId,
        string area,
        int weaponItemId,
        int targetWeaponItemId,
        bool isBot,
        DateTimeOffset occurredAt)
    {
        int weaponTier = BattleItemCombatData.Get(weaponItemId)?.Tier ?? 0;
        int targetWeaponTier = BattleItemCombatData.Get(targetWeaponItemId)?.Tier ?? 0;
        var state = _survivorCombatStates.GetOrAdd(matchingId, _ => new SurvivorCombatState());
        bool isFirstEncounter;
        lock (state.SyncRoot)
        {
            state.KnownPlayerIds.Add(attackerPlayerId);
            state.KnownPlayerIds.Add(targetPlayerId);
            isFirstEncounter = !state.FirstEncounterAtUnixMs.HasValue;
            if (isFirstEncounter)
                state.FirstEncounterAtUnixMs = occurredAt.ToUnixTimeMilliseconds();

            state.EngagementsByAttacker[attackerPlayerId] = new SurvivorCombatEngagement(
                targetPlayerId,
                area,
                weaponItemId,
                weaponTier,
                targetWeaponTier,
                occurredAt);
        }

        AppendAt(
            matchingId,
            "SURVIVOR_ENCOUNTER_START",
            attackerPlayerId,
            isBot,
            $"{FormatPlayer(attackerPlayerId)} acquired {FormatPlayer(targetPlayerId)} in {area}; aim window started.",
            occurredAt,
            entry =>
            {
                entry.TargetPlayerId = targetPlayerId;
                entry.Area = area;
                entry.WeaponItemId = weaponItemId;
                entry.WeaponTier = weaponTier;
                entry.TargetWeaponTier = targetWeaponTier;
                entry.HitCount = 0;
                entry.ElapsedMilliseconds = 0;
                entry.IsFirstMilestone = isFirstEncounter;
                entry.OccurredAtUnixMs = entry.TimestampUnixMs;
            });
    }

    public void LogSurvivorTargetLost(
        long matchingId,
        long attackerPlayerId,
        long targetPlayerId,
        string reason,
        bool isBot,
        DateTimeOffset occurredAt)
    {
        if (!_survivorCombatStates.TryGetValue(matchingId, out var state))
            return;

        SurvivorCombatEngagement engagement;
        lock (state.SyncRoot)
        {
            if (!state.EngagementsByAttacker.TryGetValue(attackerPlayerId, out engagement!) ||
                engagement.TargetPlayerId != targetPlayerId)
            {
                return;
            }

            state.EngagementsByAttacker.Remove(attackerPlayerId);
        }

        long elapsedMilliseconds = Math.Max(
            0,
            (long)(occurredAt - engagement.StartedAt).TotalMilliseconds);
        bool escaped = string.Equals(reason, "out_of_range_or_los", StringComparison.Ordinal);
        AppendAt(
            matchingId,
            "SURVIVOR_ENCOUNTER_END",
            attackerPlayerId,
            isBot,
            $"{FormatPlayer(targetPlayerId)} left {FormatPlayer(attackerPlayerId)}'s engagement: reason={reason}, hits={engagement.HitCount}, elapsedMs={elapsedMilliseconds}.",
            occurredAt,
            entry =>
            {
                entry.TargetPlayerId = targetPlayerId;
                entry.Area = engagement.Area;
                entry.WeaponItemId = engagement.WeaponItemId;
                entry.WeaponTier = engagement.WeaponTier;
                entry.TargetWeaponTier = engagement.TargetWeaponTier;
                entry.ElapsedMilliseconds = elapsedMilliseconds;
                entry.HitCount = engagement.HitCount;
                entry.Escaped = escaped;
                entry.Outcome = reason;
                entry.OccurredAtUnixMs = entry.TimestampUnixMs;
            });
    }

    public void LogSurvivorHit(
        long matchingId,
        long attackerPlayerId,
        long targetPlayerId,
        int weaponItemId,
        int damage,
        bool isLethal,
        bool isBot,
        DateTimeOffset occurredAt)
    {
        var state = _survivorCombatStates.GetOrAdd(matchingId, _ => new SurvivorCombatState());
        int weaponTier = BattleItemCombatData.Get(weaponItemId)?.Tier ?? 0;
        int targetWeaponTier;
        int hitCount;
        int killCount = 0;
        long elapsedMilliseconds;
        long? previousHitGapMilliseconds;
        bool isFirstElimination = false;

        lock (state.SyncRoot)
        {
            state.KnownPlayerIds.Add(attackerPlayerId);
            state.KnownPlayerIds.Add(targetPlayerId);
            if (!state.EngagementsByAttacker.TryGetValue(attackerPlayerId, out var engagement) ||
                engagement.TargetPlayerId != targetPlayerId)
            {
                engagement = new SurvivorCombatEngagement(
                    targetPlayerId,
                    "",
                    weaponItemId,
                    weaponTier,
                    0,
                    occurredAt);
                state.EngagementsByAttacker[attackerPlayerId] = engagement;
            }

            targetWeaponTier = engagement.TargetWeaponTier;
            elapsedMilliseconds = Math.Max(
                0,
                (long)(occurredAt - engagement.StartedAt).TotalMilliseconds);
            previousHitGapMilliseconds = engagement.LastHitAt.HasValue
                ? Math.Max(0, (long)(occurredAt - engagement.LastHitAt.Value).TotalMilliseconds)
                : null;
            engagement.LastHitAt = occurredAt;
            engagement.HitCount++;
            hitCount = engagement.HitCount;

            if (isLethal)
            {
                state.KillCountsByPlayer.TryGetValue(attackerPlayerId, out int previousKillCount);
                killCount = previousKillCount + 1;
                state.KillCountsByPlayer[attackerPlayerId] = killCount;
                isFirstElimination = !state.FirstEliminationAtUnixMs.HasValue;
                if (isFirstElimination)
                    state.FirstEliminationAtUnixMs = occurredAt.ToUnixTimeMilliseconds();

                var endingAttackers = state.EngagementsByAttacker
                    .Where(pair => pair.Key == targetPlayerId || pair.Value.TargetPlayerId == targetPlayerId)
                    .Select(pair => pair.Key)
                    .ToList();
                foreach (long endingAttacker in endingAttackers)
                    state.EngagementsByAttacker.Remove(endingAttacker);
            }
        }

        AppendAt(
            matchingId,
            "SURVIVOR_HIT",
            attackerPlayerId,
            isBot,
            $"{FormatPlayer(attackerPlayerId)} hit {FormatPlayer(targetPlayerId)} for {damage}; hit={hitCount}, elapsedMs={elapsedMilliseconds}, gapMs={previousHitGapMilliseconds?.ToString() ?? "first"}.",
            occurredAt,
            entry =>
            {
                entry.TargetPlayerId = targetPlayerId;
                entry.WeaponItemId = weaponItemId;
                entry.WeaponTier = weaponTier;
                entry.TargetWeaponTier = targetWeaponTier;
                entry.Damage = damage;
                entry.ElapsedMilliseconds = elapsedMilliseconds;
                entry.PreviousHitGapMilliseconds = previousHitGapMilliseconds;
                entry.HitCount = hitCount;
                entry.Outcome = isLethal ? "eliminated" : "hit";
                entry.OccurredAtUnixMs = entry.TimestampUnixMs;
            });

        if (!isLethal)
            return;

        if (isFirstElimination)
        {
            AppendAt(
                matchingId,
                "SURVIVOR_FIRST_ELIMINATION",
                targetPlayerId,
                BotPlayerManager.IsBotPlayerId(targetPlayerId),
                $"First elimination: {FormatPlayer(targetPlayerId)} was eliminated by {FormatPlayer(attackerPlayerId)}.",
                occurredAt,
                entry =>
                {
                    entry.WeaponItemId = weaponItemId;
                    entry.WeaponTier = weaponTier;
                    entry.TargetWeaponTier = targetWeaponTier;
                    entry.ElapsedMilliseconds = elapsedMilliseconds;
                    entry.HitCount = hitCount;
                    entry.IsFirstMilestone = true;
                    entry.Outcome = "combat";
                    entry.OccurredAtUnixMs = entry.TimestampUnixMs;
                });
        }

        AppendAt(
            matchingId,
            "SURVIVOR_COMBAT_ELIMINATION",
            attackerPlayerId,
            isBot,
            $"{FormatPlayer(attackerPlayerId)} eliminated {FormatPlayer(targetPlayerId)} with T{weaponTier}; killCount={killCount}, elapsedMs={elapsedMilliseconds}.",
            occurredAt,
            entry =>
            {
                entry.TargetPlayerId = targetPlayerId;
                entry.WeaponItemId = weaponItemId;
                entry.WeaponTier = weaponTier;
                entry.TargetWeaponTier = targetWeaponTier;
                entry.ElapsedMilliseconds = elapsedMilliseconds;
                entry.HitCount = hitCount;
                entry.KillCount = killCount;
                entry.IsFirstMilestone = isFirstElimination;
                entry.Outcome = "eliminated";
                entry.OccurredAtUnixMs = entry.TimestampUnixMs;
            });
    }

    public void LogInteraction(long matchingId, long playerId, string description, bool isBot)
    {
        Append(matchingId, "INTERACT", playerId, isBot, description);
    }

    public GameEventEntry LogRoomEncounterReveal(long matchingId, long actorPlayerId, long targetPlayerId,
        string area, int interactId, bool isBot)
    {
        return Append(matchingId, "ROOM_ENCOUNTER_REVEAL", actorPlayerId, isBot,
            $"{FormatPlayer(actorPlayerId)} encountered {FormatPlayer(targetPlayerId)} while exploring {area}.",
            entry =>
            {
                entry.Area = area;
                entry.ActivityId = interactId;
                entry.EncounteredPlayerIds = new List<long> { targetPlayerId };
                entry.OccurredAtUnixMs = entry.TimestampUnixMs;
            });
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
        _survivorCombatStates.TryRemove(matchingId, out _);
    }

    private GameEventEntry Append(long matchingId, string type, long playerId, bool isBot, string description,
        Action<GameEventEntry>? configure = null)
    {
        return AppendAt(matchingId, type, playerId, isBot, description, DateTimeOffset.UtcNow, configure);
    }

    private GameEventEntry AppendAt(long matchingId, string type, long playerId, bool isBot, string description,
        DateTimeOffset timestamp, Action<GameEventEntry>? configure = null)
    {
        var entry = CreateEntry(type, playerId, isBot, description, timestamp, configure);
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

    private void LogFirstSurvivorElimination(
        long matchingId,
        long playerId,
        string reason,
        bool isBot,
        DateTimeOffset occurredAt)
    {
        var state = _survivorCombatStates.GetOrAdd(matchingId, _ => new SurvivorCombatState());
        bool isFirstElimination;
        lock (state.SyncRoot)
        {
            state.KnownPlayerIds.Add(playerId);
            isFirstElimination = !state.FirstEliminationAtUnixMs.HasValue;
            if (isFirstElimination)
                state.FirstEliminationAtUnixMs = occurredAt.ToUnixTimeMilliseconds();

            var endingAttackers = state.EngagementsByAttacker
                .Where(pair => pair.Key == playerId || pair.Value.TargetPlayerId == playerId)
                .Select(pair => pair.Key)
                .ToList();
            foreach (long endingAttacker in endingAttackers)
                state.EngagementsByAttacker.Remove(endingAttacker);
        }

        if (!isFirstElimination)
            return;

        AppendAt(
            matchingId,
            "SURVIVOR_FIRST_ELIMINATION",
            playerId,
            isBot,
            $"First elimination: {FormatPlayer(playerId)}, reason={reason}.",
            occurredAt,
            entry =>
            {
                entry.IsFirstMilestone = true;
                entry.Outcome = reason;
                entry.OccurredAtUnixMs = entry.TimestampUnixMs;
            });
    }


    private sealed class SurvivorCombatState
    {
        public object SyncRoot { get; } = new();
        public long? FirstEncounterAtUnixMs { get; set; }
        public long? FirstTier2AtUnixMs { get; set; }
        public long? FirstTier3AtUnixMs { get; set; }
        public long? FirstEliminationAtUnixMs { get; set; }
        public HashSet<long> KnownPlayerIds { get; } = new();
        public Dictionary<long, int> KillCountsByPlayer { get; } = new();
        public Dictionary<long, SurvivorCombatEngagement> EngagementsByAttacker { get; } = new();
    }

    private sealed class SurvivorCombatEngagement
    {
        public SurvivorCombatEngagement(
            long targetPlayerId,
            string area,
            int weaponItemId,
            int weaponTier,
            int targetWeaponTier,
            DateTimeOffset startedAt)
        {
            TargetPlayerId = targetPlayerId;
            Area = area;
            WeaponItemId = weaponItemId;
            WeaponTier = weaponTier;
            TargetWeaponTier = targetWeaponTier;
            StartedAt = startedAt;
        }

        public long TargetPlayerId { get; }
        public string Area { get; }
        public int WeaponItemId { get; }
        public int WeaponTier { get; }
        public int TargetWeaponTier { get; }
        public DateTimeOffset StartedAt { get; }
        public DateTimeOffset? LastHitAt { get; set; }
        public int HitCount { get; set; }
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

    public long? TargetPlayerId { get; set; }
    public int? WeaponItemId { get; set; }
    public int? WeaponTier { get; set; }
    public int? TargetWeaponTier { get; set; }
    public int? Damage { get; set; }
    public long? ElapsedMilliseconds { get; set; }
    public long? PreviousHitGapMilliseconds { get; set; }
    public int? HitCount { get; set; }
    public int? KillCount { get; set; }
    public bool? Escaped { get; set; }
    public bool? IsFirstMilestone { get; set; }
    public string? Outcome { get; set; }

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
