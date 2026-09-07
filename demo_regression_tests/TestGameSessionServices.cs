using game_server.network;
using game_server.services;
using game_server.sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace demo_regression_tests;

internal sealed class FakePlayerGrowthHandler(
    Action<GameClientSession, long, int, int>? pick = null,
    Action<GameClientSession, long, int, long, long>? orbDecision = null) : IPlayerGrowthHandler
{
    public void HandlePick(GameClientSession session, long matchingId, int offerId, int cardIndex) =>
        pick?.Invoke(session, matchingId, offerId, cardIndex);

    public void HandleOrbDecision(GameClientSession session, long matchingId, int action, long targetItemUid, long secondItemUid) =>
        orbDecision?.Invoke(session, matchingId, action, targetItemUid, secondItemUid);
}

internal static class TestGameSessionServices
{
    public static MatchEliminationService CreateEliminationService(
        MatchRuntimeStore store,
        GameEventLogManager logs,
        MatchSummaryFileStore summaries,
        GameServerDevOptions options,
        Func<long, List<GameClientSession>> getSessions,
        Microsoft.Extensions.Logging.ILogger logger)
    {
        var results = new MatchResultService(store, logs, summaries, options, getSessions, logger);
        return new MatchEliminationService(store, logs, results, options, getSessions, logger);
    }
    public static GameSessionLeaveHandler CreateLeaveHandler()
    {
        var sessions = new GameSessionRegistry(Microsoft.Extensions.Logging.Abstractions.NullLogger<GameSessionRegistry>.Instance);
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var logs = new GameEventLogManager(id => store.Get(id)?.EventLog);
        var cleanup = new MatchCleanupService(store, sessions, logs,
            new MatchSummaryFileStore(), NullLogger.Instance);
        return new GameSessionLeaveHandler(sessions, cleanup, NullLogger<GameSessionLeaveHandler>.Instance);
    }
}

internal sealed class FakeGameSessionLifecycle(Func<long, long, Action?>? prepareCompletion = null)
    : IGameSessionLifecycle
{
    public void PublishPlayerLeft(long playerId, long matchingId) { }
    public Action? PrepareGameCompletion(long playerId, long matchingId) =>
        prepareCompletion?.Invoke(playerId, matchingId);
    public void ReleaseMatchingReservation(long playerId, long matchingId) { }
}

internal sealed class FakeMatchEntryFailureHandler(Action<GameClientSession>? handle = null)
    : IMatchEntryFailureHandler
{
    public void Handle(GameClientSession session) => handle?.Invoke(session);
}
