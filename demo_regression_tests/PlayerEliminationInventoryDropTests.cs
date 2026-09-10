using game_server.logging;
using game_server.matches;
using game_server.matches.results;
using game_server.players;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class PlayerEliminationInventoryDropTests
{
    private const int HopeOrbT1 = 107000010;
    private const int ForgetOrbT1 = 107000020;

    public PlayerEliminationInventoryDropTests()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();
    }

    [Theory]
    [InlineData(7001)]
    [InlineData(-7001)]
    public void PlayersAndBotsUseTheSameEliminationScatterPolicy(long playerId)
    {
        const long matchingId = 99001;
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(matchingId);
        var player = RegisterPlayer(match, playerId);
        var eventLogs = new GameEventLogManager(id => store.GetOrNull(id)?.EventLog);
        var service = TestGameSessionServices.CreateEliminationService(
            store,
            eventLogs,
            new MatchSummaryFileStore(),
            NullLogger.Instance);

        TestGameSessionServices.Orbs(match, playerId).AddItem(HopeOrbT1);
        TestGameSessionServices.Orbs(match, playerId).AddItem(ForgetOrbT1);

        using (match.Enter())
        {
            service.EliminatePlayer(
                match,
                player,
                EliminationReason.HEALTH_ZERO,
                deferGameOver: true);
        }

        Assert.Empty(TestGameSessionServices.Orbs(match, playerId).GetAllItems());
        Assert.Equal(
            new[] { HopeOrbT1, ForgetOrbT1 },
            match.GroundItems.GetSnapshot(player.CurrentArea)
                .Select(item => item.ItemId)
                .OrderBy(itemId => itemId)
                .ToArray());
    }

    [Fact]
    public void RepeatedEliminationDoesNotDropTheSameInventoryTwice()
    {
        const long matchingId = 99002;
        const long botPlayerId = -7002;
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(matchingId);
        var player = RegisterPlayer(match, botPlayerId);
        var eventLogs = new GameEventLogManager(id => store.GetOrNull(id)?.EventLog);
        var service = TestGameSessionServices.CreateEliminationService(
            store,
            eventLogs,
            new MatchSummaryFileStore(),
            NullLogger.Instance);

        TestGameSessionServices.Orbs(match, botPlayerId).AddItem(HopeOrbT1);

        using (match.Enter())
        {
            service.EliminatePlayer(
                match,
                player,
                EliminationReason.HEALTH_ZERO,
                deferGameOver: true);
            service.EliminatePlayer(
                match,
                player,
                EliminationReason.HEALTH_ZERO,
                deferGameOver: true);
        }

        Assert.Empty(TestGameSessionServices.Orbs(match, botPlayerId).GetAllItems());
        Assert.Single(match.GroundItems.GetSnapshot(player.CurrentArea));
        Assert.Single(
            eventLogs.GetRecent(matchingId),
            entry => entry.Type == GameEventType.EliminationDrop);
    }

    private static Player RegisterPlayer(MatchRuntime match, long playerId)
    {
        Player player;
        if (playerId < 0)
        {
            match.Bots.RegisterBots(
                match.MatchingId,
                Config.SWARM_MATCH_MAP,
                [playerId],
                new Dictionary<long, Cell> { [playerId] = new Cell(0, 0) });
            player = match.Bots.GetBot(match.MatchingId, playerId)!.Player;
        }
        else
        {
            player = new Player { Profile = new PlayerInfo { PlayerId = playerId } };
        }

        player.CurrentArea = AreaType.S2Classroom1;
        player.Position = new Vector3f(0f, 0f, 0f);
        match.RegisterParticipant(player);
        return player;
    }

    private static string FindNetworkBasePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "network", "Common", "csv");
            if (Directory.Exists(candidate))
                return Path.Combine(directory.FullName, "network");
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find network/Common/csv.");
    }
}
