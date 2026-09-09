using game_server.logging;
using System.Text.Json;

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
    private readonly object _syncRoot = new();
    private readonly JsonSerializerOptions _compactJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    public string DirectoryPath { get; } = string.IsNullOrWhiteSpace(directory)
        ? Path.Combine(AppContext.BaseDirectory, "match-summaries")
        : Path.GetFullPath(directory);

    public MatchSummaryDocument Save(long matchingId, string endReason, long winnerPlayerId, IReadOnlyCollection<GameEventEntry> events)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(matchingId);

        lock (_syncRoot)
        {
            Directory.CreateDirectory(DirectoryPath);
            string path = GetPath(matchingId);
            if (File.Exists(path))
            {
                return ReadFile(path)!;
            }

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
            {
                return [];
            }

            var events = File.ReadLines(path).Select(ReadRawEvent).Where(entry => entry != null).Select(entry => entry!);
            if (sinceSeq.HasValue)
            {
                events = events.Where(entry => entry.Seq > sinceSeq.Value);
            }

            return events.Reverse().Take(Math.Max(1, limit)).ToList();
        }
    }

    private static MatchSummaryDocument BuildDocument(long matchingId, string endReason, long winnerPlayerId, IReadOnlyCollection<GameEventEntry> sourceEvents)
    {
        var events = sourceEvents.OrderBy(entry => entry.Seq).ToList();
        var startedEvent = events.FirstOrDefault(entry => entry.Type == "MATCH_STARTED") ?? events.FirstOrDefault();
        var endedEvent = events.LastOrDefault(entry => entry.Type is "MATCH_ENDED" or "MATCH_ABANDONED");
        var endedAtUtc = endedEvent != null ? DateTimeOffset.FromUnixTimeMilliseconds(endedEvent.TimestampUnixMs) : DateTimeOffset.UtcNow;
        var startedAtUtc = startedEvent != null ? DateTimeOffset.FromUnixTimeMilliseconds(startedEvent.TimestampUnixMs) : endedAtUtc;
        var finalStats = events.LastOrDefault(entry => entry.FinalPlayerStats is { Count: > 0 })?.FinalPlayerStats ?? [];
        return new MatchSummaryDocument(
            matchingId, startedAtUtc, endedAtUtc,
            string.IsNullOrWhiteSpace(endReason) ? endedEvent?.EndReason ?? "unknown" : endReason,
            winnerPlayerId != 0 ? winnerPlayerId : endedEvent?.WinnerPlayerId ?? 0,
            finalStats.ToList(), events.TakeLast(SummaryEventPreviewLimit).ToList());
    }

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
                string rawEventsPath = GetRawEventsPath(document.MatchingId);
                if (File.Exists(rawEventsPath))
                    File.Delete(rawEventsPath);
            }
        }
    }

    private string GetPath(long matchingId) => Path.Combine(DirectoryPath, $"match-{matchingId}.json");
    private string GetRawEventsPath(long matchingId) => Path.Combine(DirectoryPath, $"match-{matchingId}.events.jsonl");

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
