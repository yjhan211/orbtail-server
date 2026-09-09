using game_server;
using game_server.bots;
using game_server.logging;
using game_server.field;
using game_server.matches.results;
using game_server.matches;
using game_server.sessions;
using Microsoft.Extensions.Logging.Abstractions;
using network.common.data;

namespace demo_regression_tests;

public sealed class MatchEnvironmentServiceTests
{
    [Fact]
    public void Process_RequiresTheSuppliedMatchLock()
    {
        var match = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(947001);
        var service = CreateService();
        Assert.Throws<InvalidOperationException>(() => service.ProcessTick(match, []));
    }

    [Fact]
    public void Process_DoesNothingAfterMatchEnded()
    {
        var match = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(947002);
        var service = CreateService();
        lock (match.MatchLock)
        {
            match.TryMarkEnded();
            service.ProcessTick(match, []);
            Assert.True(match.IsEnded);
        }
    }

    [Fact]
    public void PressureField_WithoutStartTimeIsSafe()
    {
        var match = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(947004);
        Assert.Equal(double.MaxValue, MatchPressureFieldPolicy.GetSafeDistance(match, DateTime.UtcNow));
        Assert.Equal(0, MatchPressureFieldPolicy.GetDamagePerTick(match, null, DateTime.UtcNow));
    }

    [Fact]
    public void PressureField_UsesEachMatchStartTimeAndClampsAtEnd()
    {
        var first = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(947005);
        var second = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(947006);
        var now = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
        first.Closures.InitializeMatching().GameStartTime =
            now.AddSeconds(-MatchPressureFieldPolicy.HoldSeconds - MatchPressureFieldPolicy.ShrinkSeconds / 2);
        second.Closures.InitializeMatching().GameStartTime = now;
        double expected = MatchPressureFieldPolicy.Enabled
            ? SwarmPressureField.GetSafeDistanceAtProgress(0.5)
            : double.MaxValue;
        Assert.Equal(expected, MatchPressureFieldPolicy.GetSafeDistance(first, now));
        Assert.Equal(double.MaxValue, MatchPressureFieldPolicy.GetSafeDistance(second, now));
        double endExpected = MatchPressureFieldPolicy.Enabled
            ? SwarmPressureField.GetSafeDistanceAtProgress(1)
            : double.MaxValue;
        Assert.Equal(endExpected, MatchPressureFieldPolicy.GetSafeDistance(first,
            now.AddSeconds(MatchPressureFieldPolicy.ShrinkSeconds)));
    }

    private static MatchEnvironmentService CreateService()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var sessions = new GameSessionRegistry(NullLogger<GameSessionRegistry>.Instance);
        var logs = new GameEventLogManager(id => store.GetOrNull(id)?.EventLog);
        var eliminations = TestGameSessionServices.CreateEliminationService(
            store, logs, new MatchSummaryFileStore(), NullLogger.Instance);
        var results = new MatchResultService(store, logs, new MatchSummaryFileStore(), NullLogger.Instance);
        return new MatchEnvironmentService(logs,
            new MatchCleanupService(store, logs, new MatchSummaryFileStore(), NullLogger.Instance),
            new BotEliminationService(logs, results, NullLogger.Instance),
            eliminations, results,
            NullLogger<MatchEnvironmentService>.Instance);
    }
}
