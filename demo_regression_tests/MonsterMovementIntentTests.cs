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
            Alive = true, Aggro = true, Health = 100
        };
        monster.Movement.NextPathPlanAtUtc = DateTime.MaxValue;
        var target = new Vector3f(position.X + 0.1f, position.Y, 0f);
        var behavior = new MonsterBehaviorService();
        var movement = new MatchMovementService(null!, behavior, new MatchMonsterSpawnService(behavior));
        var now = DateTime.UtcNow;

        behavior.PlanMovement(runtime, monster, [new PlayerPositionSnapshot(1, monster.Area, target)], now, false);
        Assert.Same(position, monster.Position);
        Assert.Same(target, monster.Movement.Destination);
        Assert.True(monster.Movement.Speed > 0);

        MatchMovementService.Move(runtime, monster.Info.ObjectInfo, monster.Movement, 0.05f, ignoreClosedDoors: true);
        Assert.True(monster.Position.X > position.X);
        Assert.Equal(0f, monster.Movement.Speed);
        Assert.Same(target, monster.Movement.Destination);
        Assert.False(monster.Movement.MoveToDestination);
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
        var state = new MovementState { Speed = 3f, Destination = new Vector3f(), MoveToDestination = true, PositionCorrection = new Vector3f() };
        state.Waypoints.Add(new Vector3f(1, 2, 0));
        state.ResetIntent();
        Assert.Single(state.Waypoints);
        Assert.Equal(0f, state.Speed);
        Assert.NotNull(state.Destination);
        Assert.False(state.MoveToDestination);
        Assert.Null(state.PositionCorrection);
        state.Clear();
        Assert.Empty(state.Waypoints);
    }
    [Fact]
    public void TargetPathPlanningRespectsThrottleAndDoesNotMoveActor()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987643);
        using var scope = runtime.Enter();
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        var position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, cell);
        var monster = new Monster { Position = position, Area = AreaType.S2Corridor9 };
        var movement = monster.Movement;
        var now = DateTime.UtcNow;
        movement.Destination = position;
        movement.PathRequest = MovementPathRequest.Detour;
        movement.NextPathPlanAtUtc = now.AddSeconds(10);

        MatchMovementService.PlanTargetPath(runtime, monster.Info.ObjectInfo, movement, now);
        Assert.Equal(now.AddSeconds(10), movement.NextPathPlanAtUtc);
        Assert.Same(position, monster.Position);

        MatchMovementService.PlanTargetPath(runtime, monster.Info.ObjectInfo, movement, now.AddSeconds(10));
        Assert.True(movement.NextPathPlanAtUtc > now.AddSeconds(10));
        Assert.Empty(movement.Waypoints);
        Assert.Same(position, monster.Position);
        movement.ResetIntent();
        Assert.Equal(MovementPathRequest.None, movement.PathRequest);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CrossAreaGoalIsPlannedByMovementTickAndFailedRouteFallsBack(bool reachable)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987644);
        using var scope = runtime.Enter();
        var now = DateTime.UtcNow;
        runtime.StartGameplay(now.AddSeconds(-1));
        runtime.Monsters.Initialize(now.AddSeconds(-0.05));
        var position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP,
            GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9));
        var destination = reachable
            ? MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP,
                GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Library1))
            : new Vector3f(10000f, 10000f, 0f);
        var monster = new Monster
        {
            MonsterId = 1, Position = position, Area = AreaType.S2Corridor9,
            HomeArea = AreaType.S2Corridor9, AnchorX = position.X, AnchorY = position.Y,
            Alive = true, Aggro = true, Health = 100, ChaseTargetPlayerId = 77, SpawnedAtUtc = now
        };
        runtime.Monsters.Entities.Add(monster.MonsterId, monster);
        runtime.RegisterParticipant(new game_server.players.Player
        {
            Profile = new PlayerInfo { PlayerId = 77 }, Health = 100,
            Position = destination, CurrentArea = AreaType.S2Library1
        });
        var behavior = new MonsterBehaviorService();
        behavior.PlanMovement(runtime, monster,
            [new PlayerPositionSnapshot(77, AreaType.S2Library1, destination)], now, false);
        Assert.Equal(MovementPathRequest.WorldPath, monster.Movement.PathRequest);
        Assert.Same(destination, monster.Movement.Destination);
        Assert.Empty(monster.Movement.Waypoints);
        Assert.Same(position, monster.Position);

        var movement = new MatchMovementService(null!, behavior, new MatchMonsterSpawnService(behavior));
        movement.ProcessTick(runtime, now);

        if (reachable)
        {
            Assert.NotEmpty(monster.Movement.Waypoints);
            Assert.Equal(destination, monster.Movement.Waypoints[^1]);
            Assert.NotEqual(position, monster.Position);
            Assert.Equal(77, monster.ChaseTargetPlayerId);
        }
        else
        {
            Assert.Empty(monster.Movement.Waypoints);
            Assert.Equal(0, monster.ChaseTargetPlayerId);
            Assert.NotEqual(destination, monster.Movement.Destination);
        }
        Assert.Equal(MovementPathRequest.None, monster.Movement.PathRequest);
        Assert.False(monster.Movement.MoveToDestination);
    }
}
