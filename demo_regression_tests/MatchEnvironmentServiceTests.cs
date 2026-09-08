using game_server.matches;
using game_server.sessions;
using game_server.network;
using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;
using network.common.data;

namespace demo_regression_tests;

public sealed class MatchEnvironmentServiceTests
{
    [Fact]
    public void Process_RequiresTheSuppliedMatchLock()
    {
        var match = new MatchRuntimeStore(NullLogger.Instance).GetOrCreate(947001);
        var service = CreateService(GameServerDevOptions.Disabled);
        Assert.Throws<InvalidOperationException>(() => service.Process(match, []));
    }

    [Fact]
    public void Process_DoesNothingAfterMatchEnded()
    {
        var match = new MatchRuntimeStore(NullLogger.Instance).GetOrCreate(947002);
        var service = CreateService(GameServerDevOptions.Disabled);
        lock (match.Sync)
        {
            match.TryMarkTerminal();
            service.Process(match, []);
            Assert.True(match.IsTerminal);
        }
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Process_PreservesDeveloperModesWithoutSurvivors(bool disableEnd, bool cutDummy, bool crossfire)
    {
        var match = new MatchRuntimeStore(NullLogger.Instance).GetOrCreate(947003);
        var service = CreateService(new GameServerDevOptions
        {
            DisableGameEnd = disableEnd, CutDummy = cutDummy, CrossfireSandbox = crossfire
        });
        lock (match.Sync)
        {
            service.Process(match, []);
            Assert.False(match.IsTerminal);
        }
    }

    [Fact]
    public void PressureField_WithoutStartTimeIsSafe()
    {
        var match = new MatchRuntimeStore(NullLogger.Instance).GetOrCreate(947004);
        Assert.Equal(double.MaxValue, MatchPressureFieldPolicy.GetSafeDistance(match, DateTime.UtcNow));
        Assert.Equal(0, MatchPressureFieldPolicy.GetDamagePerTick(match, null, DateTime.UtcNow));
    }

    [Fact]
    public void PressureField_UsesEachMatchStartTimeAndClampsAtEnd()
    {
        var first = new MatchRuntimeStore(NullLogger.Instance).GetOrCreate(947005);
        var second = new MatchRuntimeStore(NullLogger.Instance).GetOrCreate(947006);
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

    private static MatchEnvironmentService CreateService(GameServerDevOptions options)
    {
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var sessions = new GameSessionRegistry(NullLogger<GameSessionRegistry>.Instance);
        var logs = new GameEventLogManager(id => store.Get(id)?.EventLog);
        var eliminations = TestGameSessionServices.CreateEliminationService(
            store, logs, new MatchSummaryFileStore(), options, NullLogger.Instance);
        return new MatchEnvironmentService(logs,
            new MatchCleanupService(store, logs, new MatchSummaryFileStore(), NullLogger.Instance),
            new BotEliminationService(logs, eliminations, NullLogger.Instance),
            eliminations,
            options, NullLogger<MatchEnvironmentService>.Instance);
    }
}
