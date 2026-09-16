using game_server.matches;
using game_server.matches.monsters;
using game_server.players;
using game_server.players.bots;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class MatchOwnedStateTests
{
    public MatchOwnedStateTests()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "network", "Common", "csv")))
            directory = directory.Parent;
        GameDataHelper.SetBasePath(Path.Combine(directory!.FullName, "network"));
        GameDataHelper.Initialize();
    }

    [Fact]
    public void RuntimeOwnsServiceInstances_AndDifferentMatchesStayIsolated()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(941001);
        var second = store.GetOrCreate(941002);
        Assert.NotSame(first.GroundItems, second.GroundItems);
        Assert.NotSame(first, second);
        Assert.NotSame(first.Closures, second.Closures);
        var roster = first;

        roster.RegisterPlayer(new Player(new network.common.data.models.PlayerInfo { PlayerId = 11 }));
        TestGameSessionServices.AddSummonStones(first, 11, 9);
        TestGameSessionServices.Orbs(first, 11).AddOrb(107000010);

        Assert.Same(first.GetPlayer(11)!.Orbs,
            TestGameSessionServices.Orbs(store.GetOrThrow(first.MatchingId), 11));
        Assert.Null(second.GetPlayer(11));
        Assert.Empty(second.GetOrbs(11).GetAllOrbs());
        Assert.Equal(9, TestGameSessionServices.SummonStones(store.GetOrThrow(first.MatchingId), 11).StoneCount);
        Assert.Equal(0, TestGameSessionServices.SummonStones(second, 11).StoneCount);
        Assert.DoesNotContain(second.BuildGameResult(), row => row.playerId == 11);
        Assert.Equal((false, (long?)null), second.CheckGameOver());
    }

    [Fact]
    public void TerminalRemoval_DetachesAllState_AndLateCallsCannotRecreateMatch()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(941003);
        var sibling = store.GetOrCreate(941004);
        var ground = runtime.GroundItems;
        var roster = runtime;
        var closures = runtime.Closures;
        AreaType area = GameMapData.GetAreas(Config.SWARM_MATCH_MAP).First().AreaType;

        TestGameSessionServices.AddSummonStones(sibling, 11, 7);
        ground.SpawnItems(area, 0, 0, [107000010]);
        roster.RegisterPlayer(new Player(new network.common.data.models.PlayerInfo { PlayerId = 11 }));
        TestGameSessionServices.AddSummonStones(runtime, 11, 5);
        TestGameSessionServices.Orbs(runtime, 11).AddOrb(107000010);
        closures.InitializeMatching([]);

        using (MatchRuntimeStore.Enter(runtime))
            Assert.True(runtime.TryMarkEnded());

        Assert.Null(store.GetOrNull(runtime.MatchingId));
        Assert.Empty(ground.GetItemsInArea(area));
        Assert.DoesNotContain(roster.BuildGameResult(), row => row.playerId == 11);
        Assert.Null(closures.GameStartTime);
        Assert.Equivalent(SummonStoneStateInfo.Empty, TestGameSessionServices.SummonStones(runtime, 11));
        Assert.Throws<InvalidOperationException>(() => TestGameSessionServices.Orbs(runtime, 11));
        Assert.Throws<InvalidOperationException>(() => TestGameSessionServices.AddSummonStones(runtime, 11, 1));
        Assert.Equal(7, TestGameSessionServices.SummonStones(sibling, 11).StoneCount);
        Assert.Single(store.ActiveIds());
    }

    [Fact]
    public void ConcurrentClosureInitialization_UsesOneStateInRuntime()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(941005);
        var created = new bool[16];
        Parallel.For(0, created.Length, i =>
        {
            using (MatchRuntimeStore.Enter(runtime))
                created[i] = runtime.Closures.InitializeMatching([]);
        });
        Assert.Single(created, value => value);
        Assert.NotNull(runtime.Closures.GameStartTime);
    }

    [Fact]
    public void BotElimination_RemovesInventoryOnce_AndKeepsOtherMatchesUntouched()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(941101);
        var sibling = store.GetOrCreate(941102);
        const long botId = -42;
        foreach (var runtime in new[] { match, sibling })
        {
            runtime.Bots.RegisterBots(runtime.MatchingId,
                [botId], new Dictionary<long, Cell> { [botId] = network.common.data.GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9) });
            runtime.RegisterPlayer(runtime.Bots.GetBot(botId)!.Player);
            runtime.RegisterPlayer(new Player(new network.common.data.models.PlayerInfo { PlayerId = 11 }));
            TestGameSessionServices.Orbs(runtime, botId).AddOrb(107000010);
        }
        var bot = match.Bots.GetBot(botId)!;
        var service = TestGameSessionServices.CreateEliminationService(store, NullLogger.Instance);
        bot.Movement.WaypointIndex = 3;
        using (MatchRuntimeStore.Enter(match))
        {
            service.EliminatePlayer(match, bot.Player, EliminationReason.HEALTH_ZERO, attackerPlayerId: 11);
            var entry = match.BuildGameResult().Single(row => row.playerId == botId);
            Assert.Equal(PlayerMatchStatus.ELIMINATED, entry.finalStatus);
            Assert.True(bot.Player.IsEliminated);
            Assert.Same(bot.Player, match.GetPlayer(botId));
            Assert.Equal(0, bot.Movement.WaypointIndex);
            Assert.Empty(TestGameSessionServices.Orbs(match, botId).GetAllOrbs());
            int drops = match.GroundItems.GetItemsInArea(bot.Player.GameInfo.ObjectInfo.Area).Count;
            var eliminatedAt = entry.eliminatedAt;

            service.EliminatePlayer(match, bot.Player, EliminationReason.HEALTH_ZERO, attackerPlayerId: 99);
            Assert.Equal(drops, match.GroundItems.GetItemsInArea(bot.Player.GameInfo.ObjectInfo.Area).Count);
            Assert.Equal(eliminatedAt, match.BuildGameResult().Single(row => row.playerId == botId).eliminatedAt);
            Assert.Equal(11, match.BuildGameResult().Single(row => row.playerId == botId).attackerPlayerId);
        }
        Assert.False(sibling.Bots.GetBot(botId)!.Player.IsEliminated);
        Assert.NotEmpty(TestGameSessionServices.Orbs(sibling, botId).GetAllOrbs());
        Assert.NotEqual(PlayerMatchStatus.ELIMINATED, sibling.BuildGameResult().Single(row => row.playerId == botId).finalStatus);
    }
    [Theory]
    [InlineData(42)]
    [InlineData(-42)]
    public void Elimination_UsesSharedStateAndRecordsRankOnceWithoutSession(long playerId)
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(941103);
        var player = new Player(new PlayerInfo { PlayerId = playerId });
        if (playerId < 0)
        {
            match.Bots.RegisterBots(match.MatchingId,
                [playerId], new Dictionary<long, Cell> { [playerId] = new(0, 0) });
            player = match.Bots.GetBot(playerId)!.Player;
        }
        match.RegisterPlayer(player);
        match.RegisterPlayer(new Player(new PlayerInfo { PlayerId = 11 }));
        var service = TestGameSessionServices.CreateEliminationService(store, NullLogger.Instance);
        using (match.Enter())
        {
            service.EliminatePlayer(match, player, EliminationReason.PRESSURE_FIELD,
                deferGameOver: true, attackerPlayerId: 11, forcedRank: 5);
            var eliminatedAt = player.EliminatedAt;
            service.EliminatePlayer(match, player, EliminationReason.HEALTH_ZERO,
                deferGameOver: true, attackerPlayerId: 99, forcedRank: 9);
            Assert.Null(player.Session);
            Assert.True(player.IsEliminated);
            Assert.Equal(PlayerMatchStatus.ELIMINATED, player.Status);
            Assert.Equal(EliminationReason.PRESSURE_FIELD, player.EliminationReason);
            Assert.Equal(5, player.EliminationRank);
            Assert.Equal(11, player.AttackerPlayerId);
            Assert.Equal(eliminatedAt, player.EliminatedAt);
            Assert.Equal((true, (long?)11), match.CheckGameOver());
            Assert.False(match.IsEnded);
        }
    }
    [Fact]
    public void HumanEliminationWithoutSessionDropsInventoryAtStoredPosition()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(941104);
        var player = new Player(new PlayerInfo { PlayerId = 42 })
        {

            Position = TestMapPosition.In(AreaType.S2Corridor9)
        };
        match.RegisterPlayer(player);
        var service = TestGameSessionServices.CreateEliminationService(store, NullLogger.Instance);
        using (match.Enter())
        {
            TestGameSessionServices.Orbs(match, player.PlayerId).AddOrb(107000010);
            service.EliminatePlayer(match, player, EliminationReason.HEALTH_ZERO, deferGameOver: true);
            service.EliminatePlayer(match, player, EliminationReason.HEALTH_ZERO, deferGameOver: true);
            Assert.Null(player.Session);
            Assert.Equal(player.GameInfo.ObjectInfo.Area, player.EliminatedArea);
            Assert.Empty(TestGameSessionServices.Orbs(match, player.PlayerId).GetAllOrbs());
            Assert.Single(match.GroundItems.GetItemsInArea(player.GameInfo.ObjectInfo.Area));
        }
    }
}
