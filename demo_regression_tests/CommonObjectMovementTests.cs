using game_server.matches;
using game_server.matches.monsters;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class CommonObjectMovementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlayerAndMonsterUseIdenticalSpatialUpdates(bool directTarget)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987661);
        using var scope = runtime.Enter();
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        runtime.Bots.RegisterBots(runtime.MatchingId, [-1], new Dictionary<long, Cell> { [-1] = cell });
        var player = runtime.Bots.GetBot(-1)!.Player;
        var before = player.Position!;
        var monster = new Monster { Position = new Vector3f(before.X, before.Y, 0f) };
        var target = new Vector3f(before.X + 0.1f, before.Y, 0f);
        var playerIntent = new MovementState { FollowPath = !directTarget };
        var monsterIntent = new MovementState { FollowPath = !directTarget };
        if (directTarget)
        {
            playerIntent.DestinationCell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, target);
            playerIntent.FollowPath = true;
            playerIntent.Waypoints.Add(target);
            monsterIntent.DestinationCell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, target);
            monsterIntent.FollowPath = true;
            monsterIntent.Waypoints.Add(target);
        }
        else
        {
            playerIntent.Waypoints.Add(target);
            monsterIntent.Waypoints.Add(target);
        }

        MovementPreparationTestSteps.Advance(runtime, player.GameInfo.ObjectInfo, playerIntent, new MovementRequest(null, 1f), 0.05f);
        MovementPreparationTestSteps.Advance(runtime, monster.Info.ObjectInfo, monsterIntent, new MovementRequest(null, 1f), 0.05f);
        Assert.True(player.Position!.X > before.X);
        Assert.Equal(player.Position, monster.Position);
        Assert.Equal(player.GameInfo.ObjectInfo.Cell, monster.Info.ObjectInfo.Cell);
        Assert.Equal(player.Velocity, monster.Info.ObjectInfo.Velocity);
        Assert.Equal(player.Rotation, monster.Info.ObjectInfo.Rotation);
        Assert.False(playerIntent.FollowPath);
        Assert.False(monsterIntent.FollowPath);

        var stoppedAt = player.Position;
        var stopped = MovementPreparationTestSteps.Advance(runtime, player.GameInfo.ObjectInfo, playerIntent, new MovementRequest(null, 1f), 0.05f);
        Assert.True(stopped.Changed);
        Assert.Same(stoppedAt, player.Position);
        Assert.Equal(0f, player.Velocity.X);
        Assert.False(MovementPreparationTestSteps.Advance(runtime, player.GameInfo.ObjectInfo, playerIntent, new MovementRequest(null, 1f), 0.05f).Changed);
    }
}
