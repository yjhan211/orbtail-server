using game_server.matches;
using game_server.players;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;

namespace server_tests;

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
        var service = TestGameSessionServices.CreateEliminationService(store, NullLogger.Instance);

        TestGameSessionServices.Orbs(match, playerId).AddOrb(HopeOrbT1);
        TestGameSessionServices.Orbs(match, playerId).AddOrb(ForgetOrbT1);

        using (match.Enter())
        {
            service.EliminatePlayer(
                match,
                player,
                EliminationReason.HEALTH_ZERO,
                deferGameOver: true);
        }

        Assert.Empty(TestGameSessionServices.Orbs(match, playerId).GetAllOrbs());
        Assert.Equal(
            new[] { HopeOrbT1, ForgetOrbT1 },
            match.GroundItems.GetItemsInArea(GameMapData.GetCurrentArea(player.GameInfo.ObjectInfo.MapId, player.GameInfo.ObjectInfo.Cell))
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
        var service = TestGameSessionServices.CreateEliminationService(store, NullLogger.Instance);

        TestGameSessionServices.Orbs(match, botPlayerId).AddOrb(HopeOrbT1);

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

        Assert.Empty(TestGameSessionServices.Orbs(match, botPlayerId).GetAllOrbs());
        Assert.Single(match.GroundItems.GetItemsInArea(GameMapData.GetCurrentArea(player.GameInfo.ObjectInfo.MapId, player.GameInfo.ObjectInfo.Cell)));
    }

    private static Player RegisterPlayer(MatchRuntime match, long playerId)
    {
        Player player;
        if (playerId < 0)
        {
            match.Bots.RegisterBots(match.MatchingId,
                [playerId],
                new Dictionary<long, Cell> { [playerId] = new Cell(0, 0) });
            player = match.Bots.GetBot(playerId)!.Player;
        }
        else
        {
            player = new Player(new PlayerInfo { PlayerId = playerId });
        }

        player.InitializeSpawn(network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Classroom1)));
        match.RegisterPlayer(player);
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
