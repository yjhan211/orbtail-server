using game_server.matches;
using game_server.matches.monsters;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class MonsterMovementIntentTests
{
    [Fact]
    public void PlanningDoesNotMoveAndExecutionConsumesTheIntent()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987641);
        using var scope = runtime.Enter();
        var position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP,
            GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9));
        var monster = new Monster
        {
            Position = position, Area = AreaType.S2Corridor9, HomeArea = AreaType.S2Corridor9,
            Alive = true, Aggro = true, Health = 100, NextChasePlanAtUtc = DateTime.MaxValue
        };
        var target = new Vector3f(position.X + 0.1f, position.Y, 0f);
        var behavior = new MonsterBehaviorService();
        var movement = new MatchMovementService(null!, behavior, new MatchMonsterSpawnService(behavior));
        var now = DateTime.UtcNow;

        behavior.PlanMovement(runtime, monster, [new PlayerPositionSnapshot(1, monster.Area, target)], now, false);
        Assert.Same(position, monster.Position);
        Assert.Same(target, monster.Movement.DirectTarget);
        Assert.True(monster.Movement.Speed > 0);

        MatchMovementService.Move(runtime, monster.Info.ObjectInfo, monster.Movement, 0.05f, ignoreClosedDoors: true);
        Assert.True(monster.Position.X > position.X);
        Assert.Equal(0f, monster.Movement.Speed);
        Assert.Null(monster.Movement.DirectTarget);
        var after = monster.Position;
        MatchMovementService.Move(runtime, monster.Info.ObjectInfo, monster.Movement, 0.05f, ignoreClosedDoors: true);
        Assert.Equal(after, monster.Position);
    }

    [Fact]
    public void DestinationPathHasNoMarchTimeoutAndCompletesAtItsEndpoint()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987642);
        using var scope = runtime.Enter();
        var position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP,
            GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9));
        var target = new Vector3f(position.X + 0.1f, position.Y, 0f);
        var monster = new Monster
        {
            Position = position, Area = AreaType.S2Corridor9, HomeArea = AreaType.S2Corridor9,
            Alive = true, Aggro = true
        };
        monster.Movement.Waypoints.Add(target);
        var behavior = new MonsterBehaviorService();
        var now = DateTime.UtcNow.AddHours(1);

        behavior.PlanMovement(runtime, monster, [], now, true);
        Assert.Equal(0f, monster.Movement.Speed);
        Assert.Same(position, monster.Position);
        Assert.Single(monster.Movement.Waypoints);

        behavior.PlanMovement(runtime, monster, [], now, false);
        behavior.CompleteMovement(runtime, monster, false);
        Assert.True(monster.Alive);
        Assert.Single(monster.Movement.Waypoints);
        MatchMovementService.Move(runtime, monster.Info.ObjectInfo, monster.Movement, 1f, ignoreClosedDoors: true);
        behavior.CompleteMovement(runtime, monster, false);
        Assert.Equal(target, monster.Position);
        Assert.Empty(monster.Movement.Waypoints);
        Assert.True(monster.Alive);
        Assert.Equal(target.X, monster.AnchorX);
    }
    [Fact]
    public void ResetIntentPreservesRouteButClearsOneTickCommands()
    {
        var state = new MovementState { Speed = 3f, DirectTarget = new Vector3f(), PositionCorrection = new Vector3f() };
        state.Waypoints.Add(new Vector3f(1, 2, 0));
        state.ResetIntent();
        Assert.Single(state.Waypoints);
        Assert.Equal(0f, state.Speed);
        Assert.Null(state.DirectTarget);
        Assert.Null(state.PositionCorrection);
        state.Clear();
        Assert.Empty(state.Waypoints);
    }
}
