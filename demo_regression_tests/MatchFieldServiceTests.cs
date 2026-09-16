using game_server;
using game_server.matches;
using game_server.players.bots;
using game_server.sessions;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;

namespace demo_regression_tests;

public sealed class MatchFieldServiceTests
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
            .GetRequiredService<MatchFieldService>(provider);
        var match = store.GetOrCreate(947008);
        var first = new game_server.players.Player(new network.common.data.models.PlayerInfo { PlayerId = firstId }) {
            Health = 1,
            Position = new network.common.data.models.Vector3f(10000, 10000, 0)
        };
        var second = new game_server.players.Player(new network.common.data.models.PlayerInfo { PlayerId = secondId }) {
            Health = 2,
            Position = new network.common.data.models.Vector3f(10000, 10000, 0)
        };
        using (match.Enter())
        {
            match.RegisterPlayer(first);
            match.RegisterPlayer(second);
            match.Closures.InitializeMatching([]);
            match.Closures.GameStartTime = DateTime.UtcNow.AddSeconds(-network.common.Config.SWARM_MATCH_DURATION_SECONDS - 100);
            Assert.Empty(match.GetSessions());
            Assert.True(MatchFieldService.GetDamagePerTick(match, first.Position, DateTime.UtcNow) >= 2);

            service.ProcessDamageTick(match);

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
        Assert.Throws<InvalidOperationException>(() => service.ProcessDamageTick(match));
        Assert.Throws<InvalidOperationException>(() => service.ProcessClosureTick(match));
    }

    [Fact]
    public void Process_DoesNothingAfterMatchEnded()
    {
        var match = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(947002);
        var service = CreateService();
        lock (match.MatchLock)
        {
            match.TryMarkEnded();
            service.ProcessDamageTick(match);
            Assert.True(match.IsEnded);
        }
    }

    [Fact]
    public void PressureField_WithoutStartTimeIsSafe()
    {
        var match = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(947004);
        Assert.Equal(double.MaxValue, match.Closures.GetSafeDistance(DateTime.UtcNow));
        lock (match.MatchLock)
            Assert.Equal(0, MatchFieldService.GetDamagePerTick(match, null, DateTime.UtcNow));
    }

    [Fact]
    public void PressureField_UsesEachMatchStartTimeAndClampsAtEnd()
    {
        var first = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(947005);
        var second = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(947006);
        var now = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
        first.Closures.InitializeMatching([]);
        first.Closures.GameStartTime = now.AddSeconds(-SwarmPressureField.HoldSeconds - SwarmPressureField.ShrinkSeconds / 2);
        second.Closures.InitializeMatching([]);
        second.Closures.GameStartTime = now;
        double expected = SwarmPressureField.GetSafeDistanceAtProgress(0.5);
        Assert.Equal(expected, first.Closures.GetSafeDistance(now));
        Assert.Equal(double.MaxValue, second.Closures.GetSafeDistance(now));
        double endExpected = SwarmPressureField.GetSafeDistanceAtProgress(1);
        Assert.Equal(endExpected, first.Closures.GetSafeDistance(now.AddSeconds(SwarmPressureField.ShrinkSeconds)));
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
        match.Bots.RegisterBots(match.MatchingId,
            [-1, -2], new Dictionary<long, network.common.data.models.Cell> { [-1] = cell, [-2] = cell });
        match.Closures.InitializeMatching(MatchFieldService.SwarmFieldClosureSchedule.Value);
        match.Closures.GameStartTime = DateTime.UtcNow.AddSeconds(-network.common.Config.SWARM_MATCH_DURATION_SECONDS - 100);
        var bots = match.Bots.GetBots().ToList();
        foreach (var bot in bots) match.RegisterPlayer(bot.Player);
        var healthBefore = bots.Select(bot => bot.Player.Health).ToArray();
        lock (match.MatchLock)
        {
            foreach (var bot in bots)
                Assert.Equal(0, MatchFieldService.GetDamagePerTick(match, bot.Player.Position!, DateTime.UtcNow));
        }

        lock (match.MatchLock)
            CreateService().ProcessDamageTick(match);

        Assert.Equal(healthBefore, bots.Select(bot => bot.Player.Health).ToArray());
        Assert.All(bots, bot => Assert.False(bot.Player.IsEliminated));
    }

    private static MatchFieldService CreateService()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var sessions = new GameSessionRegistry(NullLogger<GameSessionRegistry>.Instance);
        var eliminations = TestGameSessionServices.CreateEliminationService(store, NullLogger.Instance);
        var results = new MatchResultService(store, NullLogger.Instance);
        return new MatchFieldService(NullLogger<MatchFieldService>.Instance, new game_server.players.PlayerOrbTrailService(), TestGameSessionServices.CreateHealthService(store),
            new MatchCleanupService(store, NullLogger.Instance),
            eliminations, results);
    }
}
