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
        var playerIntent = new MovementState { Speed = 1f, FollowPath = !directTarget };
        var monsterIntent = new MovementState { Speed = 1f, FollowPath = !directTarget };
        if (directTarget)
        {
            playerIntent.Destination = target;
            playerIntent.MoveToDestination = true;
            monsterIntent.Destination = target;
            monsterIntent.MoveToDestination = true;
        }
        else
        {
            playerIntent.Waypoints.Add(target);
            monsterIntent.Waypoints.Add(target);
        }

        MatchMovementService.Move(runtime, player.Profile.ObjectInfo, playerIntent, 0.05f);
        MatchMovementService.Move(runtime, monster.Info.ObjectInfo, monsterIntent, 0.05f);
        Assert.True(player.Position!.X > before.X);
        Assert.Equal(player.Position, monster.Position);
        Assert.Equal(player.Profile.ObjectInfo.Cell, monster.Info.ObjectInfo.Cell);
        Assert.Equal(player.Velocity, monster.Info.ObjectInfo.Velocity);
        Assert.Equal(player.Rotation, monster.Info.ObjectInfo.Rotation);
        Assert.Equal(0f, playerIntent.Speed);
        Assert.Equal(0f, monsterIntent.Speed);

        var stoppedAt = player.Position;
        var stopped = MatchMovementService.Move(runtime, player.Profile.ObjectInfo, playerIntent, 0.05f);
        Assert.True(stopped.Changed);
        Assert.Same(stoppedAt, player.Position);
        Assert.Equal(0f, player.Velocity.X);
        Assert.False(MatchMovementService.Move(runtime, player.Profile.ObjectInfo, playerIntent, 0.05f).Changed);
    }
}
