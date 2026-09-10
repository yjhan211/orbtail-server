using game_server.players;
using game_server.players.bots;
using game_server.combat;
using game_server.logging;
using game_server.orbs;
using game_server.matches;
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
            TestGameSessionServices.CreateHealthService(store, store.EventLogs), NullLogger<OrbRecoveryService>.Instance);
        var bot = new BotPlayerState { PlayerId = 11, Player = { Health = Config.MAX_HEALTH - 12 } };
        var first = new ProximityCombatActor(11, AreaType.None, new Vector3f(0, 0, 0),
            107000040, 0, 0, 0, WeaponItemUid: 1);
        var second = first with { WeaponItemId = 107000041, WeaponItemUid = 2 };
        var now = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
        using (MatchRuntimeStore.Enter(match))
        {
            service.Process(match.MatchingId, [first, second], [bot.Player], now);
            Assert.Equal(Config.MAX_HEALTH - 12, bot.Player.Health);
            service.Process(match.MatchingId, [first, second], [bot.Player], now.AddSeconds(5));
            Assert.Equal(Config.MAX_HEALTH, bot.Player.Health);
            Assert.Equal(2, match.OrbRecovery.ReadyAtUtc.Count);
            service.Process(match.MatchingId, [], [bot.Player], now.AddSeconds(6));
            Assert.Empty(match.OrbRecovery.ReadyAtUtc);
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
        var logs = new GameEventLogManager(id => store.GetOrNull(id)?.EventLog);
        var service = new OrbRecoveryService(store, TestGameSessionServices.CreateHealthService(store, logs), NullLogger<OrbRecoveryService>.Instance);
        var player = new Player { Profile = new PlayerInfo { PlayerId = playerId }, Health = Config.MAX_HEALTH - 1 };
        var actor = new ProximityCombatActor(playerId, AreaType.None, new Vector3f(),
            107000040, 0, 0, 0, WeaponItemUid: 1);
        var now = DateTime.UtcNow;
        using (match.Enter())
        {
            match.RegisterParticipant(player);
            service.Process(match.MatchingId, [actor], [player], now);
            service.Process(match.MatchingId, [actor], [player], now.AddSeconds(5));
            Assert.Equal(Config.MAX_HEALTH, player.Health);
            Assert.Equal(1, logs.GetResultStats(match.MatchingId, playerId).TotalRecovery);
            service.Process(match.MatchingId, [actor], [player], now.AddSeconds(10));
            Assert.Equal(1, logs.GetResultStats(match.MatchingId, playerId).TotalRecovery);
            player.ApplyDamage(Config.MAX_HEALTH);
            player.Status = PlayerMatchStatus.ELIMINATED;
            service.Process(match.MatchingId, [actor], [player], now.AddSeconds(15));
            Assert.Equal(0, player.Health);
        }
    }
    [Fact]
    public void Recovery_DoesNotShareClocksBetweenMatches()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var firstMatch = store.GetOrCreate(947302);
        var secondMatch = store.GetOrCreate(947303);
        var service = new OrbRecoveryService(store,
            TestGameSessionServices.CreateHealthService(store, store.EventLogs), NullLogger<OrbRecoveryService>.Instance);
        var actor = new ProximityCombatActor(11, AreaType.None, new Vector3f(0, 0, 0),
            107000040, 0, 0, 0, WeaponItemUid: 1);
        var now = DateTime.UtcNow;
        using (MatchRuntimeStore.Enter(firstMatch))
            service.Process(firstMatch.MatchingId, [actor], [], now);
        using (MatchRuntimeStore.Enter(secondMatch))
        {
            var bot = new BotPlayerState { PlayerId = 11, Player = { Health = 10 } };
            service.Process(secondMatch.MatchingId, [actor], [bot.Player], now.AddSeconds(5));
            Assert.Equal(10, bot.Player.Health);
            secondMatch.TryMarkEnded();
        }
        using (MatchRuntimeStore.Enter(firstMatch)) firstMatch.TryMarkEnded();
    }
}
