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
            Alive = true, Aggro = false,
            WaveSlowUntilUtc = slowed ? now.AddSeconds(1) : now
        };
        var request = new MonsterBehaviorService().CreateMovementRequest(runtime, monster, [], now);
        float expected = Config.SWARM_MONSTER_MOVE_SPEED;
        if (slowed) expected *= OrbData.WaveSlowMoveSpeedMultiplier;
        if (offsetSeconds >= 0) expected *= (float)Config.SWARM_MONSTER_ESCALATION_STAGE2_MOVE_SPEED_MULTIPLIER;
        Assert.Equal(expected, request.Speed);
        Assert.False(request.HoldPosition);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DestinationArrivalPreservesAssignedAnchorAreaAndAggro(bool aggro)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987643);
        using var scope = runtime.Enter();
        var now = DateTime.UtcNow;
        runtime.StartGameplay(now.AddSeconds(-1));
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9);
        var position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, cell);
        var destination = new Vector3f(position.X + 0.01f, position.Y, 0);
        var monster = new Monster
        {
            MonsterId = 1, Position = position, Area = AreaType.S2Corridor9,
            HomeArea = AreaType.S2Library1, AnchorX = position.X - 1, AnchorY = position.Y,
            Alive = true, Health = 10, Aggro = aggro
        };
        monster.Movement.Waypoints.Add(destination);
        monster.Movement.LastProcessedAtUtc = now.AddMilliseconds(-50);
        runtime.Monsters.Entities[1] = monster;

        new MatchMoveService(null!, new MonsterBehaviorService()).ProcessTick(runtime, now);

        Assert.True(monster.Movement.ReachedDestination);
        Assert.Equal(destination, monster.Position);
        Assert.Equal(position.X - 1, monster.AnchorX);
        Assert.Equal(position.Y, monster.AnchorY);
        Assert.Equal(AreaType.S2Library1, monster.HomeArea);
        Assert.Equal(aggro, monster.Aggro);
    }
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
        Assert.False(monster.Movement.FollowPath);
        Assert.Equal(MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, target), monster.Movement.DestinationCell);
        var after = monster.Position;
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
        var target = new Vector3f(position.X + 0.1f, position.Y, 0f);
        var monster = new Monster
        {
            Position = position, Area = AreaType.S2Corridor9, HomeArea = AreaType.S2Corridor9,
            Alive = true, Aggro = true
        };
        monster.Movement.Waypoints.Add(target);
        var behavior = new MonsterBehaviorService();
        var now = DateTime.UtcNow.AddHours(1);

        var request = MovementPreparationTestSteps.Monster(behavior, runtime, monster, [], now);
        Assert.True(monster.Alive);
        Assert.Single(monster.Movement.Waypoints);
        MovementPreparationTestSteps.Advance(runtime, monster.Info.ObjectInfo, monster.Movement, request, 1f, ignoreClosedDoors: true);
        Assert.Equal(target, monster.Position);
        Assert.Empty(monster.Movement.Waypoints);
        Assert.True(monster.Alive);
        Assert.Equal(0f, monster.AnchorX);
        Assert.Equal(AreaType.S2Corridor9, monster.HomeArea);
    }
    [Fact]
    public void ResetIntentPreservesRouteButClearsOneTickCommands()
    {
        var state = new MovementState { DestinationCell = new Cell(0, 0) };
        state.Waypoints.Add(new Vector3f(1, 2, 0));
        state.ResetIntent();
        Assert.Single(state.Waypoints);
        Assert.False(state.FollowPath);
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
        movement.Waypoints.Add(position);

        MatchMoveService.PrepareMovement(runtime, monster.Info.ObjectInfo, movement, new MovementRequest(cell, 1f), now, ignoreClosedDoors: true);
        Assert.Equal(now.AddSeconds(10), movement.NextPathPlanAtUtc);
        Assert.Same(position, monster.Position);

        MatchMoveService.PrepareMovement(runtime, monster.Info.ObjectInfo, movement, new MovementRequest(cell, 1f), now.AddSeconds(10), true);
        Assert.True(movement.NextPathPlanAtUtc > now.AddSeconds(10));
        Assert.NotEmpty(movement.Waypoints);
        Assert.Same(position, monster.Position);
        movement.ResetIntent();
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
            Assert.Equal(MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, monster.Movement.DestinationCell!), monster.Movement.Waypoints[^1]);
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
