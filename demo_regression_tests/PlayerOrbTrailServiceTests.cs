using game_server.matches;
using game_server.players;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class PlayerOrbTrailServiceTests
{
    public PlayerOrbTrailServiceTests()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "server.sln")))
            directory = directory.Parent;
        if (directory == null)
            throw new DirectoryNotFoundException("Repository root not found.");

        network.common.data.helpers.GameDataHelper.SetBasePath(Path.Combine(directory.FullName, "network"));
        network.common.data.helpers.GameDataHelper.Initialize();
    }

    [Fact]
    public void Trail_InterpolatesAndExtrapolatesWithoutSharingMatches()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(947401);
        var second = store.GetOrCreate(947402);
        var service = new PlayerOrbTrailService();
        var firstPlayer = new Player(new PlayerInfo { PlayerId = 11 });
        var secondPlayer = new Player(new PlayerInfo { PlayerId = 11 });
        first.RegisterPlayer(firstPlayer);
        second.RegisterPlayer(secondPlayer);
        var anchor = new Vector3f(0, 0, 0);
        using (MatchRuntimeStore.Enter(first))
        {
            firstPlayer.Orbs.OrbTrail.AddRange([new Vector3f(2, 0, 0), new Vector3f(4, 0, 0)]);
            var middle = service.GetPositionAtDistance(first, firstPlayer, 3, anchor);
            var beyond = service.GetPositionAtDistance(first, firstPlayer, 6, anchor);
            Assert.Equal(3f, middle.X);
            Assert.Equal(0f, middle.Y);
            Assert.Equal(6f, beyond.X);
            Assert.Equal(0f, beyond.Y);
        }
        using (MatchRuntimeStore.Enter(second))
        {
            var fallback = service.GetPositionAtDistance(second, secondPlayer, 5, anchor);
            Assert.Equal(0f, fallback.X);
            Assert.Equal(-1f, fallback.Y);
            second.TryMarkEnded();
        }
        using (MatchRuntimeStore.Enter(first)) first.TryMarkEnded();
    }

    [Fact]
    public void Destroy_RemovesOnlySuffixAndRejectsInvalidOrdinal()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(947403);
        var service = new PlayerOrbTrailService();
        var player = new Player(new PlayerInfo { PlayerId = 11 });
        match.RegisterPlayer(player);
        using (MatchRuntimeStore.Enter(match))
        {
            var inventory = TestGameSessionServices.Orbs(match, 11);
            inventory.AddOrb(107000010);
            inventory.AddOrb(107000020);
            inventory.AddOrb(107000030);
            var original = inventory.GetAllOrbs().OrderBy(item => item.ItemUid).ToArray();
            Assert.Empty(service.DestroyOrbsFromOrdinal(match, player, -1, DateTime.UtcNow));
            Assert.Empty(service.DestroyOrbsFromOrdinal(match, player, 3, DateTime.UtcNow));
            var removed = service.DestroyOrbsFromOrdinal(match, player, 1, DateTime.UtcNow);
            Assert.Equal(original.Skip(1).Select(item => item.ItemUid), removed.Select(item => item.ItemUid));
            Assert.Equal(original[0].ItemUid, Assert.Single(inventory.GetAllOrbs()).ItemUid);
            match.TryMarkEnded();
        }
    }

    [Fact]
    public void OperationsRequireMatchLock()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(947406);
        var player = new Player(new PlayerInfo { PlayerId = 11 });
        match.RegisterPlayer(player);
        var service = new PlayerOrbTrailService();
        var anchor = new Vector3f();

        Assert.Throws<InvalidOperationException>(() => PlayerOrbTrailService.GetOrbTiersInOrder(match, player));
        Assert.Throws<InvalidOperationException>(() => service.GetOrbPosition(match, player, 0, anchor, []));
        Assert.Throws<InvalidOperationException>(() => service.GetPositionAtDistance(match, player, 1, anchor));
        Assert.Throws<InvalidOperationException>(() => service.DestroyOrbsFromOrdinal(match, player, 0, DateTime.UtcNow));
    }
}
