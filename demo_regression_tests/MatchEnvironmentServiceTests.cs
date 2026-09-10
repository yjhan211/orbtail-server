using game_server;
using game_server.players.bots;
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
    [Theory]
    [InlineData(1, -2)]
    [InlineData(-1, 2)]
    public void Process_SettlesHumanAndBotWithoutSessionsUsingPreDamageHealth(long firstId, long secondId)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "network", "Common", "csv")))
            directory = directory.Parent;
        network.common.data.helpers.GameDataHelper.SetBasePath(Path.Combine(directory!.FullName, "network"));
        network.common.data.helpers.GameDataHelper.Initialize();
        using var provider = GameServerDependencyInjectionTests.CreateProvider();
        var store = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<MatchRuntimeStore>(provider);
        var service = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<MatchEnvironmentService>(provider);
        var match = store.GetOrCreate(947008);
        var first = new game_server.players.Player
        {
            Profile = new network.common.data.models.PlayerInfo { PlayerId = firstId },
            Health = 1,
            Position = new network.common.data.models.Vector3f(10000, 10000, 0)
        };
        var second = new game_server.players.Player
        {
            Profile = new network.common.data.models.PlayerInfo { PlayerId = secondId },
            Health = 2,
            Position = new network.common.data.models.Vector3f(10000, 10000, 0)
        };
        using (match.Enter())
        {
            match.RegisterParticipant(first);
            match.RegisterParticipant(second);
            match.Closures.InitializeMatching().GameStartTime =
                DateTime.UtcNow.AddSeconds(-network.common.Config.SWARM_MATCH_DURATION_SECONDS - 100);
            Assert.Empty(match.GetSessions());
            Assert.True(MatchPressureFieldPolicy.GetDamagePerTick(match, first.Position, DateTime.UtcNow) >= 2);

            service.ProcessTick(match);

            Assert.Equal(0, first.Health);
            Assert.Equal(0, second.Health);
            Assert.True(first.IsEliminated);
            Assert.Equal(2, first.EliminationRank);
            Assert.False(second.IsEliminated);
            Assert.True(match.IsEnded);
        }
    }

    [Fact]
    public void Process_RequiresTheSuppliedMatchLock()
    {
        var match = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(947001);
        var service = CreateService();
        Assert.Throws<InvalidOperationException>(() => service.ProcessTick(match));
    }

    [Fact]
    public void Process_DoesNothingAfterMatchEnded()
    {
        var match = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(947002);
        var service = CreateService();
        lock (match.MatchLock)
        {
            match.TryMarkEnded();
            service.ProcessTick(match);
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

    [Fact]
    public void Process_AfterFinalClosure_DoesNotApplyOvertimeDamageInSafeCenter()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "network", "Common", "csv")))
            directory = directory.Parent;
        network.common.data.helpers.GameDataHelper.SetBasePath(Path.Combine(directory!.FullName, "network"));
        network.common.data.helpers.GameDataHelper.Initialize();
        var match = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(947007);
        var center = SwarmPressureField.CenterCell;
        var cell = new network.common.data.models.Cell((int)center.X, (int)center.Y);
        match.Bots.RegisterBots(match.MatchingId, network.common.Config.SWARM_MATCH_MAP,
            [-1, -2], new Dictionary<long, network.common.data.models.Cell> { [-1] = cell, [-2] = cell });
        var closure = match.Closures.InitializeMatching(wavesOverride:
            AreaClosureManager.BuildSwarmFieldWaves(MatchPressureFieldPolicy.HoldSeconds, MatchPressureFieldPolicy.ShrinkSeconds));
        Assert.NotEmpty(closure.Waves);
        closure.GameStartTime = DateTime.UtcNow.AddSeconds(-network.common.Config.SWARM_MATCH_DURATION_SECONDS - 100);
        var bots = match.Bots.GetBots(match.MatchingId).ToList();
        foreach (var bot in bots) match.RegisterParticipant(bot.Player);
        var healthBefore = bots.Select(bot => bot.Player.Health).ToArray();
        foreach (var bot in bots)
            Assert.Equal(0, MatchPressureFieldPolicy.GetDamagePerTick(match, bot.Player.Position!, DateTime.UtcNow));

        lock (match.MatchLock)
            CreateService().ProcessTick(match);

        Assert.Equal(healthBefore, bots.Select(bot => bot.Player.Health).ToArray());
        Assert.All(bots, bot => Assert.False(bot.Player.IsEliminated));
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
            eliminations, results,
            NullLogger<MatchEnvironmentService>.Instance);
    }
}
