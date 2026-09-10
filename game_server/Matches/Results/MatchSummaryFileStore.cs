using System.Text.Json;
using game_server.logging;
using Microsoft.Extensions.Logging;

namespace game_server.matches.results;

/// <summary>
///     종료된 매치의 승자·참가자별 결과와 이벤트 기록을 파일로 저장한다.
///     결과 요약은 JSON, 전체 이벤트는 JSONL 파일로 남긴다.
///     같은 매치는 중복 저장하지 않고, 보관 개수를 넘으면 오래된 매치 파일부터 삭제한다.
/// </summary>
public sealed class MatchSummaryFileStore(
    string? directory = null,
    int maxSummaries = MatchSummaryFileStore.DefaultMaxSummaries)
{
    public const int DefaultMaxSummaries = 50;
    private const int SummaryEventPreviewLimit = 500;
    private readonly int _maxSummaries = Math.Max(1, maxSummaries);
    private readonly object _fileLock = new();
    private readonly JsonSerializerOptions _compactJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private string DirectoryPath { get; } = string.IsNullOrWhiteSpace(directory) ? Path.Combine(AppContext.BaseDirectory, "match-summaries") : Path.GetFullPath(directory);
    private string GetSummaryFilePath(long matchingId) => Path.Combine(DirectoryPath, $"match-{matchingId}.json");
    private string GetEventsFilePath(long matchingId) => Path.Combine(DirectoryPath, $"match-{matchingId}.events.jsonl");

    internal static MatchSummaryDocument? Prepare(
        GameEventLogManager gameEventLogManager,
        ILogger logger,
        long matchingId,
        string endReason,
        long winnerId,
        out IReadOnlyList<GameEventEntry> capturedEvents)
    {
        capturedEvents = [];
        try
        {
            var events = gameEventLogManager.GetForPersistence(matchingId)
                .OrderBy(entry => entry.Seq)
                .Select(entry => entry.CopyForPersistence())
                .ToList();
            capturedEvents = events;

            var startedEvent = events.FirstOrDefault(entry => entry.Type == GameEventType.MatchStarted) ?? events.FirstOrDefault();
            var endedEvent = events.LastOrDefault(entry => entry.Type is GameEventType.MatchEnded or GameEventType.MatchAbandoned);
            var endedAtUtc = endedEvent != null ? DateTimeOffset.FromUnixTimeMilliseconds(endedEvent.TimestampUnixMs) : DateTimeOffset.UtcNow;
            var startedAtUtc = startedEvent != null ? DateTimeOffset.FromUnixTimeMilliseconds(startedEvent.TimestampUnixMs) : endedAtUtc;
            var finalStats = events.LastOrDefault(entry => entry.FinalPlayerStats is { Count: > 0 })?.FinalPlayerStats ?? [];

            return new MatchSummaryDocument(
                matchingId, startedAtUtc, endedAtUtc,
                string.IsNullOrWhiteSpace(endReason) ? endedEvent?.EndReason ?? "unknown" : endReason,
                winnerId != 0 ? winnerId : endedEvent?.WinnerPlayerId ?? 0,
                finalStats.ToList(), events.TakeLast(SummaryEventPreviewLimit).ToList());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to prepare match summary: MatchingId={MatchingId}", matchingId);
            return null;
        }
    }

    internal void Save(MatchSummaryDocument document, IReadOnlyList<GameEventEntry> events, ILogger logger)
    {
        try
        {
            long matchingId = document.MatchingId;
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(matchingId);

            lock (_fileLock)
            {
                Directory.CreateDirectory(DirectoryPath);
                string path = GetSummaryFilePath(matchingId);
                if (File.Exists(path))
                {
                    document = ReadFile(path) ?? throw new InvalidDataException($"Invalid match summary: {path}");
                }
                else
                {
                    var orderedEvents = events.OrderBy(entry => entry.Seq).ToList();
                    string eventsPath = GetEventsFilePath(matchingId);
                    string temporaryEventsPath = eventsPath + ".tmp";
                    using (var writer = new StreamWriter(temporaryEventsPath, false, new System.Text.UTF8Encoding(false)))
                    {
                        foreach (var entry in orderedEvents)
                        {
                            writer.WriteLine(JsonSerializer.Serialize(entry, _compactJsonOptions));
                        }
                    }
                    File.Move(temporaryEventsPath, eventsPath, true);
                    document = document with
                    {
                        RawEventCount = orderedEvents.Count,
                        RawEventsFile = Path.GetFileName(eventsPath)
                    };
                    string temporaryPath = path + ".tmp";
                    File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, _jsonOptions));
                    File.Move(temporaryPath, path, true);
                    PruneOldFiles();
                }
            }

            logger.LogInformation(
                "Match summary persisted: MatchingId={MatchingId}, EndReason={EndReason}, Events={EventCount}, Directory={Directory}",
                matchingId,
                document.EndReason,
                document.RawEventCount,
                DirectoryPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist match summary: MatchingId={MatchingId}", document.MatchingId);
        }
    }
    public IReadOnlyList<GameEventEntry> ReadRawEvents(long matchingId, int limit = 5_000, long? sinceSeq = null)
    {
        lock (_fileLock)
        {
            string path = GetEventsFilePath(matchingId);
            if (!File.Exists(path))
            {
                return [];
            }

            var events = new List<GameEventEntry>();
            foreach (string line in File.ReadLines(path))
            {
                GameEventEntry? entry;
                try
                {
                    entry = JsonSerializer.Deserialize<GameEventEntry>(line, _compactJsonOptions);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (entry == null || (sinceSeq.HasValue && entry.Seq <= sinceSeq.Value))
                {
                    continue;
                }
                events.Add(entry);
            }

            events.Reverse();
            int maxCount = Math.Max(1, limit);
            if (events.Count > maxCount)
            {
                events.RemoveRange(maxCount, events.Count - maxCount);
            }
            return events;
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

    private void PruneOldFiles()
    {
        var files = new DirectoryInfo(DirectoryPath).EnumerateFiles("match-*.json")
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
                string rawEventsPath = GetEventsFilePath(document.MatchingId);
                if (File.Exists(rawEventsPath))
                    File.Delete(rawEventsPath);
            }
        }
    }
}

public sealed record MatchSummaryDocument(
    long MatchingId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    string EndReason,
    long WinnerPlayerId,
    IReadOnlyList<MatchFinalPlayerStats> Participants,
    IReadOnlyList<GameEventEntry> Events)
{
    public int RawEventCount { get; init; }
    public string? RawEventsFile { get; init; }
}
