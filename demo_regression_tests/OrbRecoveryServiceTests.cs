using game_server.matches;
using game_server.matches.monsters;
using game_server.players;
using game_server.players.bots;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class PlayerOrbRecoveryTests
{
    public PlayerOrbRecoveryTests()
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
        var service = new PlayerOrbService(TestGameSessionServices.CreateHealthService(store), TestGameSessionServices.CreateCombatDamageService(), new PlayerOrbTrailService());
        var bot = new Bot { PlayerId = 11, Player = { Health = Config.MAX_HEALTH - 12 } };
        var first = new ProximityCombatActor(11, AreaType.None, new Vector3f(0, 0, 0),
            107000040, 0, 0, 0, WeaponItemUid: 1);
        var second = first with { WeaponItemId = 107000041, WeaponItemUid = 2 };
        var now = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
        using (MatchRuntimeStore.Enter(match))
        {
            match.RegisterParticipant(bot.Player);
            service.ProcessOrbRecovery(match, [first, second], now);
            Assert.Equal(Config.MAX_HEALTH - 12, bot.Player.Health);
            service.ProcessOrbRecovery(match, [first, second], now.AddSeconds(5));
            Assert.Equal(Config.MAX_HEALTH, bot.Player.Health);
            Assert.Equal(2, bot.Player.OrbRecoveryReadyAtUtc.Count);
            service.ProcessOrbRecovery(match, [], now.AddSeconds(6));
            Assert.Empty(bot.Player.OrbRecoveryReadyAtUtc);
            match.TryMarkEnded();
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void RecoveryUsesTheSameCapAndRecordsEffectiveAmountOnce(long playerId)
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(948021);
        var service = new PlayerOrbService(TestGameSessionServices.CreateHealthService(store), TestGameSessionServices.CreateCombatDamageService(), new PlayerOrbTrailService());
        var player = new Player { Profile = new PlayerInfo { PlayerId = playerId }, Health = Config.MAX_HEALTH - 1 };
        var actor = new ProximityCombatActor(playerId, AreaType.None, new Vector3f(),
            107000040, 0, 0, 0, WeaponItemUid: 1);
        var now = DateTime.UtcNow;
        using (match.Enter())
        {
            match.RegisterParticipant(player);
            service.ProcessOrbRecovery(match, [actor], now);
            service.ProcessOrbRecovery(match, [actor], now.AddSeconds(5));
            Assert.Equal(Config.MAX_HEALTH, player.Health);
            Assert.Equal(1, player.RecoveryTotal);
            service.ProcessOrbRecovery(match, [actor], now.AddSeconds(10));
            Assert.Equal(1, player.RecoveryTotal);
            player.ApplyDamage(Config.MAX_HEALTH);
            player.Status = PlayerMatchStatus.ELIMINATED;
            service.ProcessOrbRecovery(match, [actor], now.AddSeconds(15));
            Assert.Equal(0, player.Health);
        }
    }
    [Fact]
    public void RecoveryRequiresMatchLockAndSkipsEndedMatch()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(947304);
        var service = new PlayerOrbService(TestGameSessionServices.CreateHealthService(store), TestGameSessionServices.CreateCombatDamageService(), new PlayerOrbTrailService());
        var actor = new ProximityCombatActor(11, AreaType.None, new Vector3f(),
            107000040, 0, 0, 0, WeaponItemUid: 1);
        Assert.Throws<InvalidOperationException>(() => service.ProcessOrbRecovery(match, [actor], DateTime.UtcNow));
        using (match.Enter())
        {
            match.TryMarkEnded();
            service.ProcessOrbRecovery(match, [actor], DateTime.UtcNow);
            Assert.All(match.GetPlayers(), player => Assert.Empty(player.OrbRecoveryReadyAtUtc));
        }
    }

    [Fact]
    public void Recovery_DoesNotShareClocksBetweenMatches()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var firstMatch = store.GetOrCreate(947302);
        var secondMatch = store.GetOrCreate(947303);
        var service = new PlayerOrbService(TestGameSessionServices.CreateHealthService(store), TestGameSessionServices.CreateCombatDamageService(), new PlayerOrbTrailService());
        var actor = new ProximityCombatActor(11, AreaType.None, new Vector3f(0, 0, 0),
            107000040, 0, 0, 0, WeaponItemUid: 1);
        var now = DateTime.UtcNow;
        using (MatchRuntimeStore.Enter(firstMatch))
            service.ProcessOrbRecovery(firstMatch, [actor], now);
        using (MatchRuntimeStore.Enter(secondMatch))
        {
            var bot = new Bot { PlayerId = 11, Player = { Health = 10 } };
            secondMatch.RegisterParticipant(bot.Player);
            service.ProcessOrbRecovery(secondMatch, [actor], now.AddSeconds(5));
            Assert.Equal(10, bot.Player.Health);
            secondMatch.TryMarkEnded();
        }
        using (MatchRuntimeStore.Enter(firstMatch)) firstMatch.TryMarkEnded();
    }
}
