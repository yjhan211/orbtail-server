using System.Text.Json;

namespace game_server.services;

public sealed class MatchSummaryFileStore
{
    public const int DefaultMaxSummaries = 50;

    private readonly string _directory;
    private readonly int _maxSummaries;
    private readonly object _syncRoot = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

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

            var document = BuildDocument(matchingId, endReason, winnerPlayerId, events);
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
                    document.Events.Count))
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
                                      string.Equals(entry.Outcome, "merge", StringComparison.OrdinalIgnoreCase)),
                events.Where(entry => entry.PlayerId == playerId && entry.Type == "SUMMON_STONE_AWARDED")
                    .Sum(entry => entry.SummonStoneDelta ?? 0));
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
            events);
    }

    private static IEnumerable<long> GetPlayerIds(GameEventEntry entry)
    {
        if (entry.PlayerId != 0) yield return entry.PlayerId;
        if (entry.ActorPlayerId != 0) yield return entry.ActorPlayerId;
        if (entry.TargetPlayerId is > 0) yield return entry.TargetPlayerId.Value;
    }

    private static int CountTypes(IEnumerable<GameEventEntry> events, string type) =>
        events.Count(entry => string.Equals(entry.Type, type, StringComparison.OrdinalIgnoreCase));

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

    private void PruneOldFiles()
    {
        var files = new DirectoryInfo(_directory).EnumerateFiles("match-*.json")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .Skip(_maxSummaries)
            .ToList();
        foreach (var file in files) file.Delete();
    }

    private string GetPath(long matchingId) => Path.Combine(_directory, $"match-{matchingId}.json");
}

public sealed record MatchSummaryDocument(
    long MatchingId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    string EndReason,
    long WinnerPlayerId,
    IReadOnlyList<MatchSummaryParticipant> Participants,
    IReadOnlyDictionary<string, int> IntegrityCounters,
    IReadOnlyList<GameEventEntry> Events);

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
    int SummonStonesEarned);

public sealed record MatchSummaryListItem(
    long MatchingId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    string EndReason,
    long WinnerPlayerId,
    int ParticipantCount,
    int EventCount);
