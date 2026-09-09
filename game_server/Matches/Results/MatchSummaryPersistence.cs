using game_server.logging;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace game_server.matches.results;

/// <summary>
///     Cleanup-safe snapshot of the metadata and events used to write one match summary.
/// </summary>
internal sealed class MatchSummaryPersistenceRequest
{
    private readonly byte[] _serializedEvents;

    internal MatchSummaryPersistenceRequest(
        long matchingId,
        string endReason,
        long winnerId,
        int eventCount,
        byte[] serializedEvents)
    {
        MatchingId = matchingId;
        EndReason = endReason;
        WinnerId = winnerId;
        EventCount = eventCount;
        _serializedEvents = serializedEvents;
    }

    internal long MatchingId { get; }
    internal string EndReason { get; }
    internal long WinnerId { get; }
    internal int EventCount { get; }
    internal ReadOnlyMemory<byte> SerializedEvents => _serializedEvents;
}

/// <summary>
///     매치 요약 영속 공용 경로 (#297 중복 단일화) — 세션 정산과 봇 전용 매치 정산이 같은 저장·로그를 쓴다.
/// </summary>
internal static class MatchSummaryPersistence
{
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     Freezes the current metadata and event graph before runtime cleanup releases the live log.
    /// </summary>
    internal static MatchSummaryPersistenceRequest? Capture(
        GameEventLogManager gameEventLogManager,
        ILogger logger,
        long matchingId,
        string endReason,
        long winnerId)
    {
        try
        {
            var events = gameEventLogManager.GetForPersistence(matchingId);
            byte[] serializedEvents = JsonSerializer.SerializeToUtf8Bytes(events, SnapshotJsonOptions);
            return new MatchSummaryPersistenceRequest(
                matchingId,
                endReason,
                winnerId,
                events.Count,
                serializedEvents);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to capture match summary: MatchingId={MatchingId}", matchingId);
            return null;
        }
    }

    /// <summary>
    ///     Writes a previously captured best-effort observational summary. Callers run this after
    ///     packet enqueue and the terminal runtime commit, outside the match lifecycle monitor.
    /// </summary>
    internal static void Persist(
        MatchSummaryPersistenceRequest request,
        MatchSummaryFileStore matchSummaryFileStore,
        ILogger logger)
    {
        try
        {
            var events = JsonSerializer.Deserialize<List<GameEventEntry>>(
                    request.SerializedEvents.Span,
                    SnapshotJsonOptions)
                ?? throw new JsonException("The captured match event snapshot was empty.");
            var summary = matchSummaryFileStore.Save(
                request.MatchingId,
                request.EndReason,
                request.WinnerId,
                events);
            logger.LogInformation(
                "Match summary persisted: MatchingId={MatchingId}, EndReason={EndReason}, Events={EventCount}, Directory={Directory}",
                request.MatchingId,
                summary.EndReason,
                summary.RawEventCount,
                matchSummaryFileStore.DirectoryPath);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to persist match summary: MatchingId={MatchingId}",
                request.MatchingId);
        }
    }
}
