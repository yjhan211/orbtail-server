using game_server.matches;
using game_server.matches.monsters;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class MonsterMovementIntentTests
{
    [Theory]
    [InlineData(99, 2)]
    [InlineData(1, 2)]
    [InlineData(0, 2)]
    public void NearestOtherAreaPlayerOverridesPreviousTarget(long previousId, long expectedId)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var monster = new Monster { Alive = true, Area = AreaType.S2Corridor9, Position = new Vector3f(), ChaseTargetPlayerId = previousId };
        var far = new game_server.players.Player
        {
            Profile = new PlayerInfo { PlayerId = 1 }, CurrentArea = AreaType.S2Library1, Position = new Vector3f(20, 0, 0)
        };
        var near = new game_server.players.Player
        {
            Profile = new PlayerInfo { PlayerId = 2 }, CurrentArea = AreaType.S2Library1, Position = new Vector3f(10, 0, 0)
        };
        var eliminated = new game_server.players.Player
        {
            Profile = new PlayerInfo { PlayerId = 3 }, CurrentArea = AreaType.S2Library1, Position = new Vector3f(1, 0, 0),
            Status = PlayerMatchStatus.ELIMINATED
        };
        bool found = new MonsterBehaviorService().TrySelectChaseTarget(monster, [far, near, eliminated], out var target);
        Assert.Equal(expectedId != 0, found);
        if (found)
        {
            Assert.Equal(expectedId, target.PlayerId);
            Assert.Equal(expectedId, monster.ChaseTargetPlayerId);
        }
    }

    [Theory]
    [InlineData(0, 0, true, 2)]
    [InlineData(1, 0, true, 2)]
    [InlineData(-1, 10, true, 2)]
    [InlineData(1, 10, true, 2)]
    [InlineData(1, 10, false, 2)]
    public void SameAreaNearestPlayerOverridesPreviousTarget(int offset, int previousOffset, bool sameArea, long expectedId)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        float radius = Config.SWARM_MONSTER_AGGRO_RADIUS;
        var monster = new Monster
        {
            Alive = true, Area = AreaType.S2Corridor9,
            Position = new Vector3f(), ChaseTargetPlayerId = 1
        };
        var previous = new game_server.players.Player
        {
            Profile = new PlayerInfo { PlayerId = 1 },
            CurrentArea = sameArea ? AreaType.S2Corridor9 : AreaType.S2Library1,
            Position = new Vector3f(radius + previousOffset + 20, 0, 0)
        };
        var nearest = new game_server.players.Player
        {
            Profile = new PlayerInfo { PlayerId = 2 }, CurrentArea = AreaType.S2Corridor9,
            Position = new Vector3f(radius + offset, 0, 0)
        };
        bool found = new MonsterBehaviorService().TrySelectChaseTarget(monster, [previous, nearest], out var target);
        Assert.Equal(expectedId != 0, found);
        if (found)
        {
            Assert.Equal(expectedId, target.PlayerId);
            Assert.Equal(expectedId, monster.ChaseTargetPlayerId);
        }
    }

    [Fact]
    public void ChaseContinuesAcrossAreasWhenNoLocalPlayerExists()
    {
        var monster = new Monster { Alive = true, Area = AreaType.S2Corridor9, Position = new Vector3f(), ChaseTargetPlayerId = 7 };
        var player = new game_server.players.Player
        {
            Profile = new PlayerInfo { PlayerId = 7 }, CurrentArea = AreaType.S2Library1, Position = new Vector3f()
        };
        Assert.True(new MonsterBehaviorService().TrySelectChaseTarget(monster, [player], out var target));
        Assert.Same(player, target);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void RequestedSpeedAppliesEscalationAtBoundaryAndPreservesSlow(int offsetSeconds, bool slowed)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987645);
        using var scope = runtime.Enter();
        var started = DateTime.UtcNow;
        runtime.Monsters.Initialize(started);
        var now = started.AddSeconds(Config.SWARM_MONSTER_ESCALATION_STAGE2_AT_SECONDS + offsetSeconds);
        var monster = new Monster
        {
            Alive = true,
            WaveSlowUntilUtc = slowed ? now.AddSeconds(1) : now
        };
        var target = new game_server.players.Player
        {
            Profile = new PlayerInfo { PlayerId = 1 },
            CurrentArea = AreaType.S2Corridor9,
            Position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP,
                GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9))
        };
        var request = new MonsterBehaviorService().CreateMovementRequest(runtime, monster, [target], now);
        float expected = Config.SWARM_MONSTER_MOVE_SPEED;
        if (slowed) expected *= OrbData.WaveSlowMoveSpeedMultiplier;
        if (offsetSeconds >= 0) expected *= (float)Config.SWARM_MONSTER_ESCALATION_STAGE2_MOVE_SPEED_MULTIPLIER;
        Assert.Equal(expected, request.Speed);
        Assert.NotNull(request.DestinationCell);
    }

    [Fact]
    public void NoTargetStopsMovement()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987643);
        using var scope = runtime.Enter();
        var now = DateTime.UtcNow;
        runtime.StartGameplay(now.AddSeconds(-1));
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        var position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, cell);
        var destination = position;
        position = new Vector3f(destination.X - 0.01f, destination.Y, 0);
        var monster = new Monster
        {
            MonsterId = 1, Position = position, Area = AreaType.S2Corridor9,
            Alive = true, Health = 10
        };
        monster.Movement.Waypoints.Add(MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, destination));
        monster.Movement.LastProcessedAtUtc = now.AddMilliseconds(-50);
        runtime.Monsters.Entities[1] = monster;

        new MatchMoveService(null!, new MonsterBehaviorService()).ProcessTick(runtime, now);

        Assert.NotEmpty(monster.Movement.Waypoints);
        Assert.Equal(position, monster.Position);
        Assert.Equal(new Vector3f(), monster.Info.ObjectInfo.Velocity);
    }
    [Fact]
    public void PlanningDoesNotMoveAndHoldRequestStopsExecution()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987641);
        using var scope = runtime.Enter();
        var position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP,
            GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9));
        var monster = new Monster
        {
            Position = position, Area = AreaType.S2Corridor9,
            Alive = true, Health = 100
        };
        monster.Movement.NextPathPlanAtUtc = DateTime.MaxValue;
        var target = new Vector3f(position.X + 0.6f, position.Y, 0f);
        var behavior = new MonsterBehaviorService();
        var movement = new MatchMoveService(null!, behavior);
        var now = DateTime.UtcNow;

        var request = MovementPreparationTestSteps.Monster(behavior, runtime, monster, [new game_server.players.Player { Profile = new PlayerInfo { PlayerId = 1 }, CurrentArea = monster.Area, Position = target }], now);
        Assert.Same(position, monster.Position);
        Assert.Equal(MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, target), monster.Movement.DestinationCell);
        Assert.True(request.Speed > 0);

        MovementPreparationTestSteps.Advance(runtime, monster.Info.ObjectInfo, monster.Movement, request, 0.05f, ignoreClosedDoors: true);
        Assert.True(monster.Position.X > position.X);
        Assert.Equal(MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, target), monster.Movement.DestinationCell);
        var after = monster.Position;
        request = request with { Speed = 0f };
        MovementPreparationTestSteps.Advance(runtime, monster.Info.ObjectInfo, monster.Movement, request, 0.05f, ignoreClosedDoors: true);
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
        var target = position;
        position = new Vector3f(target.X - 0.1f, target.Y, 0f);
        var monster = new Monster
        {
            Position = position, Area = AreaType.S2Corridor9,
            Alive = true
        };
        monster.Movement.Waypoints.Add(MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, target));
        var behavior = new MonsterBehaviorService();
        var now = DateTime.UtcNow.AddHours(1);

        var request = MovementPreparationTestSteps.Monster(behavior, runtime, monster, [new game_server.players.Player { Profile = new PlayerInfo { PlayerId = 1 }, CurrentArea = monster.Area, Position = target }], now);
        Assert.True(monster.Alive);
        Assert.Single(monster.Movement.Waypoints);
        MovementPreparationTestSteps.Advance(runtime, monster.Info.ObjectInfo, monster.Movement, request, 1f, ignoreClosedDoors: true);
        Assert.Equal(target, monster.Position);
        Assert.Empty(monster.Movement.Waypoints);
        Assert.True(monster.Alive);
    }
    [Fact]
    public void ClearRemovesRouteAndPreservesDestination()
    {
        var state = new MovementState { DestinationCell = new Cell(0, 0) };
        state.Waypoints.Add(MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, new Vector3f(1, 2, 0)));
        Assert.Single(state.Waypoints);
        Assert.NotNull(state.DestinationCell);
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
        movement.DestinationCell = cell;
        movement.NextPathPlanAtUtc = now.AddSeconds(10);
        movement.Waypoints.Add(MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, position));

        MatchMoveService.PrepareMovement(runtime, monster.Info.ObjectInfo, movement, new MovementRequest(cell, 1f), now, ignoreClosedDoors: true);
        Assert.Equal(now.AddSeconds(10), movement.NextPathPlanAtUtc);
        Assert.Same(position, monster.Position);

        MatchMoveService.PrepareMovement(runtime, monster.Info.ObjectInfo, movement, new MovementRequest(cell, 1f), now.AddSeconds(10), true);
        Assert.True(movement.NextPathPlanAtUtc > now.AddSeconds(10));
        Assert.NotEmpty(movement.Waypoints);
        Assert.Same(position, monster.Position);
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
            Alive = true, Health = 100, ChaseTargetPlayerId = 77
        };
        monster.Movement.LastProcessedAtUtc = now.AddSeconds(-0.05);
        runtime.Monsters.Entities.Add(monster.MonsterId, monster);
        runtime.RegisterParticipant(new game_server.players.Player
        {
            Profile = new PlayerInfo { PlayerId = 77 }, Health = 100,
            Position = destination, CurrentArea = AreaType.S2Library1
        });
        var behavior = new MonsterBehaviorService();
        var request = behavior.CreateMovementRequest(runtime, monster,
            [new game_server.players.Player { Profile = new PlayerInfo { PlayerId = 77 }, CurrentArea = AreaType.S2Library1, Position = destination }], now);
        Assert.Equal(MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, destination), request.DestinationCell);
        Assert.Empty(monster.Movement.Waypoints);
        Assert.Same(position, monster.Position);

        var movement = new MatchMoveService(null!, behavior);
        movement.ProcessTick(runtime, now);

        if (reachable)
        {
            Assert.NotEmpty(monster.Movement.Waypoints);
            Assert.Equal(monster.Movement.DestinationCell, monster.Movement.Waypoints[^1]);
            Assert.NotEqual(position, monster.Position);
            Assert.Equal(77, monster.ChaseTargetPlayerId);
        }
        else
        {
            Assert.Empty(monster.Movement.Waypoints);
            Assert.Equal(77, monster.ChaseTargetPlayerId);
            Assert.Equal(position, monster.Position);
        }
    }
}
