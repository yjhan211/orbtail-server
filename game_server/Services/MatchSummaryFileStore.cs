using System.Text.Json;
using network.common;

namespace game_server.services;

public sealed class MatchSummaryFileStore
{
    public const int DefaultMaxSummaries = 50;
    private const int SummaryEventPreviewLimit = 500;

    private readonly string _directory;
    private readonly int _maxSummaries;
    private readonly object _syncRoot = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly JsonSerializerOptions _compactJsonOptions = new(JsonSerializerDefaults.Web);

    public MatchSummaryFileStore(string? directory = null, int maxSummaries = DefaultMaxSummaries)
    {
        _directory = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(AppContext.BaseDirectory, "match-summaries")
            : Path.GetFullPath(directory);
        _maxSummaries = Math.Max(1, maxSummaries);
    }

    public string DirectoryPath => _directory;

    public MatchSummaryDocument Save(
        long matchingId,
        string endReason,
        long winnerPlayerId,
        IReadOnlyCollection<GameEventEntry> events)
    {
        if (matchingId <= 0) throw new ArgumentOutOfRangeException(nameof(matchingId));

        lock (_syncRoot)
        {
            Directory.CreateDirectory(_directory);
            string path = GetPath(matchingId);
            if (File.Exists(path))
                return ReadFile(path)!;

            var orderedEvents = events.OrderBy(entry => entry.Seq).ToList();
            string rawEventsFile = Path.GetFileName(GetRawEventsPath(matchingId));
            WriteRawEvents(matchingId, orderedEvents);
            var document = BuildDocument(matchingId, endReason, winnerPlayerId, orderedEvents) with
            {
                RawEventCount = orderedEvents.Count,
                RawEventsFile = rawEventsFile
            };
            string temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, _jsonOptions));
            File.Move(temporaryPath, path, true);
            PruneOldFiles();
            return document;
        }
    }

    public MatchSummaryDocument? Read(long matchingId)
    {
        lock (_syncRoot)
        {
            string path = GetPath(matchingId);
            return File.Exists(path) ? ReadFile(path) : null;
        }
    }

    public IReadOnlyList<GameEventEntry> ReadRawEvents(long matchingId, int limit = 5_000, long? sinceSeq = null)
    {
        lock (_syncRoot)
        {
            string path = GetRawEventsPath(matchingId);
            if (!File.Exists(path))
                return [];

            IEnumerable<GameEventEntry> events = File.ReadLines(path)
                .Select(ReadRawEvent)
                .Where(entry => entry != null)
                .Select(entry => entry!);
            if (sinceSeq.HasValue)
                events = events.Where(entry => entry.Seq > sinceSeq.Value);
            return events.Reverse().Take(Math.Max(1, limit)).ToList();
        }
    }

    public IReadOnlyList<MatchSummaryListItem> ListRecent(int limit = 20)
    {
        lock (_syncRoot)
        {
            if (!Directory.Exists(_directory)) return [];

            return Directory.EnumerateFiles(_directory, "match-*.json")
                .Select(ReadFile)
                .Where(document => document != null)
                .Select(document => new MatchSummaryListItem(
                    document!.MatchingId,
                    document.StartedAtUtc,
                    document.EndedAtUtc,
                    document.EndReason,
                    document.WinnerPlayerId,
                    document.Participants.Count,
                    document.RawEventCount > 0 ? document.RawEventCount : document.Events.Count))
                .OrderByDescending(item => item.EndedAtUtc)
                .Take(Math.Clamp(limit, 1, _maxSummaries))
                .ToList();
        }
    }

    private MatchSummaryDocument BuildDocument(
        long matchingId,
        string endReason,
        long winnerPlayerId,
        IReadOnlyCollection<GameEventEntry> sourceEvents)
    {
        var events = sourceEvents.OrderBy(entry => entry.Seq).ToList();
        var startedEvent = events.FirstOrDefault(entry => entry.Type == "MATCH_STARTED") ?? events.FirstOrDefault();
        var endedEvent = events.LastOrDefault(entry => entry.Type is "MATCH_ENDED" or "MATCH_ABANDONED");
        DateTimeOffset endedAtUtc = endedEvent != null
            ? DateTimeOffset.FromUnixTimeMilliseconds(endedEvent.TimestampUnixMs)
            : DateTimeOffset.UtcNow;
        DateTimeOffset startedAtUtc = startedEvent != null
            ? DateTimeOffset.FromUnixTimeMilliseconds(startedEvent.TimestampUnixMs)
            : endedAtUtc;

        var finalStats = events.LastOrDefault(entry => entry.FinalPlayerStats is { Count: > 0 })
            ?.FinalPlayerStats ?? [];
        var playerIds = events.SelectMany(GetPlayerIds)
            .Concat(finalStats.Select(player => player.PlayerId))
            .Where(playerId => playerId != 0)
            .Distinct()
            .OrderBy(playerId => playerId)
            .ToList();

        var participants = playerIds.Select(playerId =>
        {
            var stats = finalStats.FirstOrDefault(player => player.PlayerId == playerId);
            var elimination = events.LastOrDefault(entry =>
                entry.Type == "ELIMINATE" && entry.PlayerId == playerId);
            bool isBot = BotPlayerManager.IsBotPlayerId(playerId) ||
                         events.Any(entry => entry.PlayerId == playerId && entry.IsBot);
            var stoneEvents = events.Where(entry =>
                    entry.PlayerId == playerId && entry.Type == "SUMMON_STONE_AWARDED")
                .ToList();
            return new MatchSummaryParticipant(
                playerId,
                isBot,
                stats is { Rank: > 0 } ? stats.Rank : null,
                stats?.KillCount ?? 0,
                stats?.TotalDamageDealt ?? 0,
                stats?.TotalRecovery ?? 0,
                elimination?.Description,
                events.Count(entry => entry.PlayerId == playerId && entry.Type == "ORB_SUMMON_SUCCEEDED"),
                events.Count(entry => entry.PlayerId == playerId && entry.Type == "SURVIVOR_ORB_BOARD_STATE" &&
                                      IsOrbMergeOutcome(entry.Outcome)),
                stoneEvents.Sum(entry => entry.SummonStoneDelta ?? 0))
            {
                RoomSummonStonesEarned = stoneEvents.Where(entry => !IsCorridorArea(entry.Area))
                    .Sum(entry => entry.SummonStoneDelta ?? 0),
                CorridorSummonStonesEarned = stoneEvents.Where(entry => IsCorridorArea(entry.Area))
                    .Sum(entry => entry.SummonStoneDelta ?? 0),
                CoreSummonStonesEarned = stoneEvents.Where(entry =>
                        string.Equals(entry.Outcome, "core", StringComparison.OrdinalIgnoreCase))
                    .Sum(entry => entry.SummonStoneDelta ?? 0)
            };
        }).ToList();

        var integrityCounters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["areaMismatch"] = CountTypes(events, "AREA_MISMATCH"),
            ["stalePacket"] = CountTypes(events, "STALE_PACKET"),
            ["interpolationSnap"] = CountTypes(events, "INTERPOLATION_SNAP"),
            ["sfxPoolExhausted"] = CountTypes(events, "SFX_POOL_EXHAUSTED")
        };

        return new MatchSummaryDocument(
            matchingId,
            startedAtUtc,
            endedAtUtc,
            string.IsNullOrWhiteSpace(endReason) ? endedEvent?.EndReason ?? "unknown" : endReason,
            winnerPlayerId != 0 ? winnerPlayerId : endedEvent?.WinnerPlayerId ?? 0,
            participants,
            integrityCounters,
            events.TakeLast(SummaryEventPreviewLimit).ToList())
        {
            Metrics = BuildMetrics(events, startedAtUtc, endedAtUtc)
        };
    }

    private static IEnumerable<long> GetPlayerIds(GameEventEntry entry)
    {
        if (entry.PlayerId != 0) yield return entry.PlayerId;
        if (entry.ActorPlayerId != 0) yield return entry.ActorPlayerId;
        if (entry.TargetPlayerId is > 0) yield return entry.TargetPlayerId.Value;
    }

    private static int CountTypes(IEnumerable<GameEventEntry> events, string type) =>
        events.Count(entry => string.Equals(entry.Type, type, StringComparison.OrdinalIgnoreCase));

    private static SurvivorMatchMetrics BuildMetrics(
        IReadOnlyList<GameEventEntry> events,
        DateTimeOffset startedAtUtc,
        DateTimeOffset endedAtUtc)
    {
        var eliminationCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["pvp"] = 0,
            ["mental"] = 0,
            ["closure"] = 0,
            ["other"] = 0
        };
        foreach (var elimination in events.Where(entry => entry.Type == "ELIMINATE"))
            eliminationCounts[ClassifyElimination(events, elimination)]++;

        var stoneEvents = events.Where(entry => entry.Type == "SUMMON_STONE_AWARDED").ToList();
        var afterimageKillEvents = events.Where(entry => entry.Type == "AFTERIMAGE_KILLED").ToList();
        var reinforcementReleaseEvents = events
            .Where(entry => entry.Type == "SURVIVOR_REINFORCEMENT_RELEASED").ToList();
        var densityEvents = events
            .Where(entry => entry.Type == "SURVIVOR_MONSTER_DENSITY_SAMPLE").ToList();
        var botMovementPerformanceEvents = events
            .Where(entry => entry.Type == "SURVIVOR_BOT_MOVEMENT_TICK_PERFORMANCE").ToList();
        var stoneSources = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["room"] = stoneEvents.Where(entry => !IsCorridorArea(entry.Area))
                .Sum(entry => entry.SummonStoneDelta ?? 0),
            ["corridor"] = stoneEvents.Where(entry => IsCorridorArea(entry.Area))
                .Sum(entry => entry.SummonStoneDelta ?? 0),
            ["core"] = stoneEvents.Where(entry =>
                    string.Equals(entry.Outcome, "core", StringComparison.OrdinalIgnoreCase))
                .Sum(entry => entry.SummonStoneDelta ?? 0)
        };
        var areaContention = BuildAreaContention(events);
        var rewardAreaSnapshots = events
            .Where(entry => entry.Type == "SURVIVOR_REWARD_AREA_SNAPSHOT")
            .Select(entry => new MatchRewardAreaSnapshotMetric(
                entry.PhaseIndex ?? 0,
                Math.Max(0, entry.TimestampUnixMs - startedAtUtc.ToUnixTimeMilliseconds()),
                entry.Outcome,
                entry.RewardAreaStates ?? []))
            .ToList();
        var coreContestedEntries = events
            .Where(entry => entry.Type == "SURVIVOR_CORE_CONTESTED_ENTRY")
            .Select(entry => new MatchCoreContestedEntryMetric(
                entry.PlayerId,
                entry.Area ?? string.Empty,
                entry.MonsterId ?? 0,
                entry.CoreCurrentHealth ?? 0,
                entry.CoreMaxHealth ?? 0,
                Math.Max(0, entry.TimestampUnixMs - startedAtUtc.ToUnixTimeMilliseconds()),
                entry.AlreadyPresentPlayerIds ?? []))
            .ToList();
        var hotspotDensity = BuildHotspotDensity(densityEvents, reinforcementReleaseEvents);
        int botMovementTickSampleCount = botMovementPerformanceEvents
            .Sum(entry => entry.BotMovementTickSampleCount ?? 0);
        int botMovementTickSkipCount = botMovementPerformanceEvents
            .Sum(entry => entry.BotMovementTickSkipCount ?? 0);
        int botMovementTickAttemptCount = botMovementTickSampleCount + botMovementTickSkipCount;
        double? botMovementTickP50Milliseconds = MaxNullable(
            botMovementPerformanceEvents.Select(entry => entry.BotMovementTickP50Milliseconds));
        double? botMovementTickP95Milliseconds = MaxNullable(
            botMovementPerformanceEvents.Select(entry => entry.BotMovementTickP95Milliseconds));
        double? botMovementTickP99Milliseconds = MaxNullable(
            botMovementPerformanceEvents.Select(entry => entry.BotMovementTickP99Milliseconds));
        int botMovementMaxConsecutiveSkipCount = botMovementPerformanceEvents
            .Select(entry => entry.BotMovementMaxConsecutiveSkipCount ?? 0).DefaultIfEmpty(0).Max();


        return new SurvivorMatchMetrics
        {
            MatchDurationSeconds = Math.Max(0d, (endedAtUtc - startedAtUtc).TotalSeconds),
            FirstTier2ElapsedMilliseconds = GetFirstElapsedMilliseconds(events, "SURVIVOR_FIRST_T2", startedAtUtc),
            FirstTier3ElapsedMilliseconds = GetFirstElapsedMilliseconds(events, "SURVIVOR_FIRST_T3", startedAtUtc),
            FirstBoardFullElapsedMilliseconds = GetFirstElapsedMilliseconds(events, "SURVIVOR_ORB_BOARD_FULL", startedAtUtc),
            FirstEncounterElapsedMilliseconds = GetFirstElapsedMilliseconds(events, "SURVIVOR_ENCOUNTER_START", startedAtUtc),
            FirstEliminationElapsedMilliseconds = GetFirstElapsedMilliseconds(events, "SURVIVOR_FIRST_ELIMINATION", startedAtUtc),
            PvpEliminationCount = eliminationCounts["pvp"],
            EliminationCounts = eliminationCounts,
            SummonStoneSources = stoneSources,
            CoreKillCount = afterimageKillEvents.Count(entry =>
                string.Equals(entry.Outcome, "core", StringComparison.OrdinalIgnoreCase)),
            NormalKillCount = afterimageKillEvents.Count(entry =>
                string.Equals(entry.Outcome, "normal", StringComparison.OrdinalIgnoreCase) &&
                !IsCorridorArea(entry.Area)),
            ReinforcementKillCount = afterimageKillEvents.Count(entry =>
                string.Equals(entry.Outcome, "reinforcement", StringComparison.OrdinalIgnoreCase)),
            CorridorKillCount = afterimageKillEvents.Count(entry => IsCorridorArea(entry.Area)),
            ContestedAreaEntryCount = areaContention.Sum(metric => metric.ContestedEntryCount),
            AreaContention = areaContention,
            RewardAreaSnapshots = rewardAreaSnapshots,
            CoreContestedEntries = coreContestedEntries,
            ReinforcementReleasedCount = reinforcementReleaseEvents
                .Sum(entry => entry.ReinforcementReleasedCount ?? 0),
            MonsterDensitySampleCount = densityEvents.Count,
            MonsterContactSampleCount = densityEvents.Count(entry => entry.HasAttackableMonster == true),
            MonsterContactRatio = densityEvents.Count == 0
                ? 0d
                : densityEvents.Count(entry => entry.HasAttackableMonster == true) / (double)densityEvents.Count,
            MaxConcurrentAliveAfterimages = densityEvents
                .Select(entry => entry.GlobalAliveMonsterCount ?? 0).DefaultIfEmpty(0).Max(),
            HotspotDensity = hotspotDensity,
            BotMovementTickP50Milliseconds = botMovementTickP50Milliseconds,
            BotMovementTickP95Milliseconds = botMovementTickP95Milliseconds,
            BotMovementTickP99Milliseconds = botMovementTickP99Milliseconds,
            BotMovementTickSampleCount = botMovementTickSampleCount,
            BotMovementTickSkipCount = botMovementTickSkipCount,
            BotMovementTickSkipRate = botMovementTickAttemptCount == 0
                ? 0d
                : botMovementTickSkipCount / (double)botMovementTickAttemptCount,
            BotMovementMaxConsecutiveSkipCount = botMovementMaxConsecutiveSkipCount
        };
    }

    private static long? GetFirstElapsedMilliseconds(
        IEnumerable<GameEventEntry> events,
        string type,
        DateTimeOffset startedAtUtc)
    {
        var entry = events.FirstOrDefault(candidate =>
            string.Equals(candidate.Type, type, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
            return null;
        return Math.Max(0, entry.TimestampUnixMs - startedAtUtc.ToUnixTimeMilliseconds());
    }

    private static string ClassifyElimination(
        IReadOnlyList<GameEventEntry> events,
        GameEventEntry elimination)
    {
        if (elimination.IsAreaClosureElimination == true || elimination.IsOvertimeElimination == true ||
            string.Equals(elimination.DamageSourceType, "closure", StringComparison.OrdinalIgnoreCase))
            return "closure";
        if (string.Equals(elimination.DamageSourceType, "pvp", StringComparison.OrdinalIgnoreCase) ||
            elimination.ActorPlayerId != 0 && elimination.ActorPlayerId != elimination.PlayerId)
            return "pvp";

        bool hasNearbyCombatElimination = events.Any(entry =>
            entry.Type == "SURVIVOR_COMBAT_ELIMINATION" &&
            entry.TargetPlayerId == elimination.PlayerId &&
            entry.TimestampUnixMs <= elimination.TimestampUnixMs &&
            elimination.TimestampUnixMs - entry.TimestampUnixMs <= 5_000);
        if (hasNearbyCombatElimination)
            return "pvp";

        return string.Equals(elimination.Outcome ?? elimination.Description, "MENTAL_ZERO",
            StringComparison.OrdinalIgnoreCase)
            ? "mental"
            : "other";
    }

    private static IReadOnlyList<MatchAreaContentionMetric> BuildAreaContention(
        IReadOnlyList<GameEventEntry> events)
    {
        var currentAreaByPlayer = new Dictionary<long, string>();
        var occupantsByArea = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);
        var accumulators = new Dictionary<string, AreaContentionAccumulator>(StringComparer.OrdinalIgnoreCase);

        void RemovePlayer(long playerId)
        {
            if (!currentAreaByPlayer.Remove(playerId, out string? previousArea))
                return;
            if (occupantsByArea.TryGetValue(previousArea, out var occupants))
                occupants.Remove(playerId);
        }

        foreach (var entry in events)
        {
            if (entry.Type == "ELIMINATE")
            {
                RemovePlayer(entry.PlayerId);
                continue;
            }

            bool isEntry = entry.Type is "SPAWN_ASSIGNMENT" or "AREA_ENTER";
            if (!isEntry || entry.PlayerId == 0)
                continue;

            string? area = entry.Area ?? entry.ToArea;
            RemovePlayer(entry.PlayerId);
            if (string.IsNullOrWhiteSpace(area) || IsCorridorArea(area) ||
                string.Equals(area, "None", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!occupantsByArea.TryGetValue(area, out var occupants))
            {
                occupants = [];
                occupantsByArea[area] = occupants;
            }
            if (!accumulators.TryGetValue(area, out var accumulator))
            {
                accumulator = new AreaContentionAccumulator(area);
                accumulators[area] = accumulator;
            }

            accumulator.EntryCount++;
            if (occupants.Count > 0)
                accumulator.ContestedEntryCount++;
            occupants.Add(entry.PlayerId);
            currentAreaByPlayer[entry.PlayerId] = area;
            accumulator.UniqueVisitors.Add(entry.PlayerId);
            accumulator.MaxConcurrentPlayers = Math.Max(accumulator.MaxConcurrentPlayers, occupants.Count);
        }

        return accumulators.Values
            .OrderBy(accumulator => accumulator.Area, StringComparer.Ordinal)
            .Select(accumulator => new MatchAreaContentionMetric(
                accumulator.Area,
                accumulator.EntryCount,
                accumulator.ContestedEntryCount,
                accumulator.MaxConcurrentPlayers,
                accumulator.UniqueVisitors.Count))
            .ToList();
    }

    private static IReadOnlyList<MatchHotspotDensityMetric> BuildHotspotDensity(
        IReadOnlyList<GameEventEntry> densityEvents,
        IReadOnlyList<GameEventEntry> reinforcementReleaseEvents)
    {
        var releasedByArea = reinforcementReleaseEvents
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Area))
            .GroupBy(entry => entry.Area!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(entry => entry.ReinforcementReleasedCount ?? 0),
                StringComparer.OrdinalIgnoreCase);

        return densityEvents
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Area))
            .GroupBy(entry => entry.Area!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var ordered = group.OrderBy(entry => entry.TimestampUnixMs).ToList();
                int contactSampleCount = ordered.Count(entry => entry.HasAttackableMonster == true);
                var gaps = MeasureNoContactGaps(ordered);
                return new MatchHotspotDensityMetric(
                    group.Key,
                    ordered.Count,
                    contactSampleCount,
                    ordered.Count == 0 ? 0d : contactSampleCount / (double)ordered.Count,
                    ordered.Select(entry => entry.AliveMonsterCount ?? 0).DefaultIfEmpty(0).Max(),
                    releasedByArea.GetValueOrDefault(group.Key),
                    gaps.Count,
                    gaps.LongestMilliseconds);
            })
            .ToList();
    }

    private static (int Count, long LongestMilliseconds) MeasureNoContactGaps(
        IReadOnlyList<GameEventEntry> orderedSamples)
    {
        const long sampleSpanMilliseconds = 1_000;
        const long maximumContinuousSampleGapMilliseconds = 1_500;
        const long reportThresholdMilliseconds = 2_000;
        int count = 0;
        long longestMilliseconds = 0;
        long? gapStartedAt = null;
        long? lastGapSampleAt = null;

        void CompleteGap()
        {
            if (!gapStartedAt.HasValue || !lastGapSampleAt.HasValue)
                return;

            long durationMilliseconds =
                lastGapSampleAt.Value - gapStartedAt.Value + sampleSpanMilliseconds;
            if (durationMilliseconds >= reportThresholdMilliseconds)
            {
                count++;
                longestMilliseconds = Math.Max(longestMilliseconds, durationMilliseconds);
            }

            gapStartedAt = null;
            lastGapSampleAt = null;
        }

        foreach (var sample in orderedSamples)
        {
            if (sample.HasAttackableMonster != false)
            {
                CompleteGap();
                continue;
            }

            if (!gapStartedAt.HasValue ||
                lastGapSampleAt.HasValue &&
                sample.TimestampUnixMs - lastGapSampleAt.Value > maximumContinuousSampleGapMilliseconds)
            {
                CompleteGap();
                gapStartedAt = sample.TimestampUnixMs;
            }

            lastGapSampleAt = sample.TimestampUnixMs;
        }

        CompleteGap();
        return (count, longestMilliseconds);
    }

    private static double? MaxNullable(IEnumerable<double?> values)
    {
        var availableValues = values.Where(value => value.HasValue).Select(value => value!.Value).ToList();
        return availableValues.Count == 0 ? null : availableValues.Max();
    }

    private static bool IsCorridorArea(string? area) =>
        !string.IsNullOrWhiteSpace(area) &&
        Enum.TryParse(area, true, out AreaType parsedArea) &&
        parsedArea.IsCorridor();

    private void WriteRawEvents(long matchingId, IReadOnlyCollection<GameEventEntry> events)
    {
        string path = GetRawEventsPath(matchingId);
        string temporaryPath = path + ".tmp";
        using (var writer = new StreamWriter(temporaryPath, false, new System.Text.UTF8Encoding(false)))
        {
            foreach (var entry in events)
                writer.WriteLine(JsonSerializer.Serialize(entry, _compactJsonOptions));
        }
        File.Move(temporaryPath, path, true);
    }

    private GameEventEntry? ReadRawEvent(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<GameEventEntry>(line, _compactJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
    private MatchSummaryDocument? ReadFile(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<MatchSummaryDocument>(File.ReadAllText(path), _jsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    ///     사람은 merge, 봇은 bot_merge로 보드 변경 사유를 남긴다.
    ///     merge만 세면 봇 머지가 항상 0으로 집계된다.
    /// </summary>
    private static bool IsOrbMergeOutcome(string? outcome) =>
        string.Equals(outcome, "merge", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(outcome, "bot_merge", StringComparison.OrdinalIgnoreCase);

    private void PruneOldFiles()
    {
        var files = new DirectoryInfo(_directory).EnumerateFiles("match-*.json")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .Skip(_maxSummaries)
            .ToList();
        foreach (var file in files)
        {
            var document = ReadFile(file.FullName);
            file.Delete();
            if (document != null)
            {
                string rawEventsPath = GetRawEventsPath(document.MatchingId);
                if (File.Exists(rawEventsPath))
                    File.Delete(rawEventsPath);
            }
        }
    }

    private string GetPath(long matchingId) => Path.Combine(_directory, $"match-{matchingId}.json");
    private string GetRawEventsPath(long matchingId) => Path.Combine(_directory, $"match-{matchingId}.events.jsonl");

    private sealed class AreaContentionAccumulator(string area)
    {
        public string Area { get; } = area;
        public int EntryCount { get; set; }
        public int ContestedEntryCount { get; set; }
        public int MaxConcurrentPlayers { get; set; }
        public HashSet<long> UniqueVisitors { get; } = [];
    }
}

public sealed record MatchSummaryDocument(
    long MatchingId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    string EndReason,
    long WinnerPlayerId,
    IReadOnlyList<MatchSummaryParticipant> Participants,
    IReadOnlyDictionary<string, int> IntegrityCounters,
    IReadOnlyList<GameEventEntry> Events)
{
    public int RawEventCount { get; init; }
    public string? RawEventsFile { get; init; }
    public SurvivorMatchMetrics Metrics { get; init; } = new();
}

public sealed record MatchSummaryParticipant(
    long PlayerId,
    bool IsBot,
    int? FinalRank,
    int KillCount,
    int TotalDamageDealt,
    int TotalRecovery,
    string? EliminationReason,
    int SummonCount,
    int MergeCount,
    int SummonStonesEarned)
{
    public int RoomSummonStonesEarned { get; init; }
    public int CorridorSummonStonesEarned { get; init; }
    public int CoreSummonStonesEarned { get; init; }
}

public sealed record SurvivorMatchMetrics
{
    public double MatchDurationSeconds { get; init; }
    public long? FirstTier2ElapsedMilliseconds { get; init; }
    public long? FirstTier3ElapsedMilliseconds { get; init; }
    public long? FirstBoardFullElapsedMilliseconds { get; init; }
    public long? FirstEncounterElapsedMilliseconds { get; init; }
    public long? FirstEliminationElapsedMilliseconds { get; init; }
    public int PvpEliminationCount { get; init; }
    public IReadOnlyDictionary<string, int> EliminationCounts { get; init; } =
        new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> SummonStoneSources { get; init; } =
        new Dictionary<string, int>();
    public int CoreKillCount { get; init; }
    public int NormalKillCount { get; init; }
    public int ReinforcementKillCount { get; init; }
    public int CorridorKillCount { get; init; }
    public int ContestedAreaEntryCount { get; init; }
    public IReadOnlyList<MatchAreaContentionMetric> AreaContention { get; init; } = [];
    public IReadOnlyList<MatchRewardAreaSnapshotMetric> RewardAreaSnapshots { get; init; } = [];
    public IReadOnlyList<MatchCoreContestedEntryMetric> CoreContestedEntries { get; init; } = [];
    public int ReinforcementReleasedCount { get; init; }
    public int MonsterDensitySampleCount { get; init; }
    public int MonsterContactSampleCount { get; init; }
    public double MonsterContactRatio { get; init; }
    public int MaxConcurrentAliveAfterimages { get; init; }
    public IReadOnlyList<MatchHotspotDensityMetric> HotspotDensity { get; init; } = [];
    public double? BotMovementTickP50Milliseconds { get; init; }
    public double? BotMovementTickP95Milliseconds { get; init; }
    public double? BotMovementTickP99Milliseconds { get; init; }
    public int BotMovementTickSampleCount { get; init; }
    public int BotMovementTickSkipCount { get; init; }
    public double BotMovementTickSkipRate { get; init; }
    public int BotMovementMaxConsecutiveSkipCount { get; init; }
}

public sealed record MatchRewardAreaSnapshotMetric(
    int PhaseIndex,
    long ElapsedMilliseconds,
    string? Reason,
    IReadOnlyList<MonsterRewardAreaTelemetry> Areas);

public sealed record MatchCoreContestedEntryMetric(
    long EnteringPlayerId,
    string Area,
    int MonsterId,
    int CoreCurrentHealth,
    int CoreMaxHealth,
    long ElapsedMilliseconds,
    IReadOnlyList<long> AlreadyPresentPlayerIds);

public sealed record MatchAreaContentionMetric(
    string Area,
    int EntryCount,
    int ContestedEntryCount,
    int MaxConcurrentPlayers,
    int UniqueVisitorCount);

public sealed record MatchHotspotDensityMetric(
    string Area,
    int SampleCount,
    int ContactSampleCount,
    double ContactRatio,
    int MaxAliveMonsterCount,
    int ReinforcementReleasedCount,
    int LongNoContactGapCount,
    long LongestNoContactGapMilliseconds);

public sealed record MatchSummaryListItem(
    long MatchingId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    string EndReason,
    long WinnerPlayerId,
    int ParticipantCount,
    int EventCount);
