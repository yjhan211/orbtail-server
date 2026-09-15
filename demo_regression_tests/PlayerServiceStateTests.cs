using game_server.matches;
using game_server.players;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class PlayerServiceStateTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void Movement_AppliesWithoutConnection_ForHumanAndBotIds(long playerId)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "server.sln")))
            root = root.Parent;
        GameDataHelper.SetBasePath(Path.Combine(root!.FullName, "network"));
        GameDataHelper.Initialize();
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(948011);
        var service = new PlayerMovementService(NullLogger<PlayerMovementService>.Instance);
        var player = new Player { Profile = new PlayerInfo { PlayerId = playerId } };
        match.RegisterParticipant(player);
        var spawn = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, MatchSpawnData.GetPhaseRoomCandidates()[0]);
        using (match.Enter())
        {
            player.InitializeSpawn(spawn);
            player.State = PlayerState.SLEEP;
            bool correction = service.ProcessMovement(match, player, new C_TO_G_MOVE
            {
                Position = player.Position!,
                Velocity = new Vector3f(),
                Rotation = 45f
            }, 0.05f);
            Assert.False(correction);
            Assert.Null(player.Session);
            Assert.False(player.IsSleeping);
            Assert.Equal(45f, player.Rotation);
            var snapshot = player.CreateGameObjectInfo();
            Assert.Equal(45f, snapshot.Rotation);
        }
    }
    [Fact]
    public void SettlementDamageDefersEliminationAndRequiresMatchLock()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(948012);
        var health = TestGameSessionServices.CreateHealthService(store);
        var player = new Player { Profile = new PlayerInfo { PlayerId = 1 }, Health = 5 };
        Assert.Throws<InvalidOperationException>(() => health.ApplyDamage(match, player, 5, handleElimination: false));
        Assert.Equal(5, player.Health);

        using (match.Enter())
        {
            match.RegisterParticipant(player);
            health.ApplyDamage(match, player, 5, handleElimination: false);
            Assert.Equal(0, player.Health);
            Assert.False(player.IsEliminated);
            Assert.False(match.IsEnded);
        }
    }

    [Fact]
    public void HealthChange_RecordsRecoveryWithoutConnection_AndKeepsPlayersSeparate()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(948010);
        var service = new PlayerHealthService(TestGameSessionServices.CreateEliminationService(store, NullLogger.Instance), NullLogger<PlayerHealthService>.Instance);
        var player = new Player { Profile = new PlayerInfo { PlayerId = 1 }, Health = 50 };
        var other = new Player { Profile = new PlayerInfo { PlayerId = 2 }, Health = 40 };
        match.RegisterParticipant(player);
        match.RegisterParticipant(other);

        using (match.Enter())
        {
            service.Recover(match, player, 10);
            service.ApplyDamage(match, other, 5);

            Assert.Null(player.Session);
            Assert.Null(other.Session);
            Assert.Equal(60, player.Health);
            Assert.Equal(35, other.Health);
            Assert.Equal(10, player.RecoveryTotal);
            Assert.Equal(0, other.RecoveryTotal);
        }
    }
}
