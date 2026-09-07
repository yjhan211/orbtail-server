using game_server.network;
using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;

namespace demo_regression_tests;

internal static class TestGameSessionServices
{
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
