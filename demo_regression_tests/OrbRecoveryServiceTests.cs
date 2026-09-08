using game_server.matches;
using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class OrbRecoveryServiceTests
{
    public OrbRecoveryServiceTests()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "server.sln")))
            directory = directory.Parent;
        if (directory == null) throw new DirectoryNotFoundException("Repository root not found.");
        network.common.data.helpers.GameDataHelper.SetBasePath(Path.Combine(directory.FullName, "network"));
        network.common.data.helpers.GameDataHelper.Initialize();
    }
    [Fact]
    public void Recovery_AggregatesDueOrbsAndRemovesMissingOrbClocks()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(947301);
        var service = new OrbRecoveryService(store,
            new GameEventLogManager(id => store.GetOrNull(id)?.EventLog), NullLogger<OrbRecoveryService>.Instance);
        var bot = new BotPlayerState { PlayerId = 11, Health = Config.MAX_HEALTH - 12 };
        var first = new ProximityCombatActor(11, AreaType.None, new Vector3f(0, 0, 0),
            107000040, 0, 0, 0, WeaponItemUid: 1);
        var second = first with { WeaponItemId = 107000041, WeaponItemUid = 2 };
        var now = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
        using (MatchRuntimeStore.Enter(match))
        {
            service.Process(match.MatchingId, [first, second], [], [bot], now);
            Assert.Equal(Config.MAX_HEALTH - 12, bot.Health);
            service.Process(match.MatchingId, [first, second], [], [bot], now.AddSeconds(5));
            Assert.Equal(Config.MAX_HEALTH, bot.Health);
            Assert.Equal(2, match.Presentation.OrbRecoveryReadyAtUtc.Count);
            service.Process(match.MatchingId, [], [], [bot], now.AddSeconds(6));
            Assert.Empty(match.Presentation.OrbRecoveryReadyAtUtc);
            match.TryMarkTerminal();
        }
    }

    [Fact]
    public void Recovery_DoesNotShareClocksBetweenMatches()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var firstMatch = store.GetOrCreate(947302);
        var secondMatch = store.GetOrCreate(947303);
        var service = new OrbRecoveryService(store,
            new GameEventLogManager(id => store.GetOrNull(id)?.EventLog), NullLogger<OrbRecoveryService>.Instance);
        var actor = new ProximityCombatActor(11, AreaType.None, new Vector3f(0, 0, 0),
            107000040, 0, 0, 0, WeaponItemUid: 1);
        var now = DateTime.UtcNow;
        using (MatchRuntimeStore.Enter(firstMatch))
            service.Process(firstMatch.MatchingId, [actor], [], [], now);
        using (MatchRuntimeStore.Enter(secondMatch))
        {
            var bot = new BotPlayerState { PlayerId = 11, Health = 10 };
            service.Process(secondMatch.MatchingId, [actor], [], [bot], now.AddSeconds(5));
            Assert.Equal(10, bot.Health);
            secondMatch.TryMarkTerminal();
        }
        using (MatchRuntimeStore.Enter(firstMatch)) firstMatch.TryMarkTerminal();
    }
}
