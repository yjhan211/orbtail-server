using game_server.matches;
using game_server.logging;
using game_server.matches.results;
using game_server.players;
using Microsoft.Extensions.Logging.Abstractions;
using network.common.data.models;
using network.common;
using network.common.data;
using network.common.data.helpers;

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
        var logs = new GameEventLogManager(id => store.GetOrNull(id)?.EventLog);
        var service = new PlayerMovementService(
            new MovementValidationService(NullLogger<MovementValidationService>.Instance), logs, NullLogger.Instance);
        var player = new MatchPlayer { Profile = new PlayerInfo { PlayerId = playerId } };
        match.RegisterParticipant(player);
        var spawn = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, MatchSpawnData.GetPhaseRoomCandidates()[0]);
        using (match.Enter())
        {
            service.InitializeSpawn(player, spawn);
            player.State = PlayerState.SLEEP;
            var result = service.Apply(match, player, new C_TO_G_MOVE
            {
                Position = player.LastValidatedPosition!,
                Velocity = new Vector3f(),
                Rotation = 45f
            }, 0.05f);
            Assert.NotNull(result);
            Assert.Null(player.Session);
            Assert.False(player.IsSleeping);
            Assert.Equal(45f, player.LastValidatedRotation);
            var snapshot = service.CaptureGameObjectInfo(match, player, player.State);
            Assert.Equal(45f, snapshot.Rotation);
        }
    }
    [Fact]
    public void HealthChange_RecordsRecoveryWithoutConnection_AndKeepsPlayersSeparate()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(948010);
        var logs = new GameEventLogManager(id => store.GetOrNull(id)?.EventLog);
        var service = new PlayerHealthChangeService(logs,
            TestGameSessionServices.CreateEliminationService(store, logs, new MatchSummaryFileStore(), NullLogger.Instance),
            NullLogger.Instance);
        var player = new MatchPlayer { Profile = new PlayerInfo { PlayerId = 1 }, Health = 50 };
        var other = new MatchPlayer { Profile = new PlayerInfo { PlayerId = 2 }, Health = 40 };
        match.RegisterParticipant(player);
        match.RegisterParticipant(other);

        using (match.Enter())
        {
            service.Handle(match, player, player.Recover(10));
            service.Handle(match, other, other.ApplyDamage(5));

            Assert.Null(player.Session);
            Assert.Null(other.Session);
            Assert.Equal(60, player.Health);
            Assert.Equal(35, other.Health);
            Assert.Equal(10, logs.GetResultStats(match.MatchingId, player.PlayerId).TotalRecovery);
            Assert.Equal(0, logs.GetResultStats(match.MatchingId, other.PlayerId).TotalRecovery);
        }
    }
}
