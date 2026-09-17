using game_server.matches;
using game_server.matches.monsters;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.models;

namespace server_tests;

public sealed class MonsterMovementIntentTests
{
    [Theory]
    [InlineData(99, 2)]
    [InlineData(1, 2)]
    [InlineData(0, 2)]
    public void NearestOtherAreaPlayerOverridesPreviousTarget(long previousId, long expectedId)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var monster = new Monster { Alive = true, Position = TestMapPosition.In(AreaType.S2Gym1), ChaseTargetPlayerId = previousId };
        var destination = TestMapPosition.In(AreaType.S2Library1);
        var origin = monster.Position;
        float direction = Math.Sign(destination.X - origin.X);
        var far = new game_server.players.Player(new PlayerInfo { PlayerId = 1 })
        {
            Position = TestMapPosition.In(AreaType.S2Library1, direction * 0.2f)
        };
        var near = new game_server.players.Player(new PlayerInfo { PlayerId = 2 })
        {
            Position = destination
        };
        var eliminated = new game_server.players.Player(new PlayerInfo { PlayerId = 3 })
        {
            Position = origin,
            Status = PlayerMatchStatus.ELIMINATED
        };
        var target = MonsterBehaviorService.SelectChaseTarget(monster, [far, near, eliminated]);
        Assert.Equal(expectedId, target?.PlayerId ?? 0);
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
        var monster = new Monster
        {
            Alive = true,
            Position = TestMapPosition.In(AreaType.S2Corridor9), ChaseTargetPlayerId = 1
        };
        var previous = new game_server.players.Player(new PlayerInfo { PlayerId = 1 })
        {

            Position = TestMapPosition.In(sameArea ? AreaType.S2Corridor9 : AreaType.S2Library1, 0.3f + previousOffset * 0.01f)
        };
        var nearest = new game_server.players.Player(new PlayerInfo { PlayerId = 2 })
        {
            Position = TestMapPosition.In(AreaType.S2Corridor9, 0.1f + offset * 0.01f)
        };
        var target = MonsterBehaviorService.SelectChaseTarget(monster, [previous, nearest]);
        Assert.Equal(expectedId, target?.PlayerId ?? 0);
    }

    [Fact]
    public void ChaseContinuesAcrossAreasWhenNoLocalPlayerExists()
    {
        var monster = new Monster { Alive = true, Position = TestMapPosition.In(AreaType.S2Corridor9), ChaseTargetPlayerId = 7 };
        var player = new game_server.players.Player(new PlayerInfo { PlayerId = 7 })
        {
            Position = TestMapPosition.In(AreaType.S2Library1)
        };
        Assert.Same(player, MonsterBehaviorService.SelectChaseTarget(monster, [player]));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void RequestedSpeedAppliesEscalationAtBoundary(int offsetSeconds)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987645);
        using var scope = runtime.Enter();
        var started = DateTime.UtcNow;
        runtime.Monsters.Initialize(started);
        var now = started.AddSeconds(Config.SWARM_MONSTER_ESCALATION_STAGE2_AT_SECONDS + offsetSeconds);
        var monster = new Monster { Alive = true };
        var target = new game_server.players.Player(new PlayerInfo { PlayerId = 1 })
        {

            Position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP,
                GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9))
        };
        var request = new MonsterBehaviorService().CreateMovementRequest(runtime, monster, [target], now);
        float expected = Config.SWARM_MONSTER_MOVE_SPEED;
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
            MonsterId = 1, Position = position,
            Alive = true, Health = 10
        };
        monster.Movement.Waypoints.Add(MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, destination));
        monster.Movement.LastProcessedAtUtc = now.AddMilliseconds(-50);
        runtime.Monsters.Entities[1] = monster;

        new MatchMoveService(null!, new MonsterBehaviorService()).ProcessTick(runtime, now);

        Assert.Empty(monster.Movement.Waypoints);
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
            Position = position,
            Alive = true, Health = 100
        };
        var target = new Vector3f(position.X + 0.6f, position.Y, 0f);
        var behavior = new MonsterBehaviorService();
        var movement = new MatchMoveService(null!, behavior);
        var now = DateTime.UtcNow;

        var request = MovementPreparationTestSteps.Monster(behavior, runtime, monster, [new game_server.players.Player(new PlayerInfo { PlayerId = 1 }) { Position = target }], now);
        Assert.Same(position, monster.Position);
        Assert.Equal(MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, target), request.DestinationCell);
        Assert.True(request.Speed > 0);

        MovementPreparationTestSteps.Advance(runtime, monster.Info.ObjectInfo, monster.Movement, request, 0.05f);
        Assert.True(monster.Position.X > position.X);
        Assert.Equal(MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, target), request.DestinationCell);
        var after = monster.Position;
        request = request with { Speed = 0f };
        MovementPreparationTestSteps.Advance(runtime, monster.Info.ObjectInfo, monster.Movement, request, 0.05f);
        Assert.Equal(after, monster.Position);
    }

    [Fact]
    public void SameDestinationCellStopsWithoutMovingToCenterAndClearsPath()
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
            Position = position,
            Alive = true
        };
        monster.Movement.Waypoints.Add(MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, target));
        var behavior = new MonsterBehaviorService();
        var now = DateTime.UtcNow.AddHours(1);

        var request = MovementPreparationTestSteps.Monster(behavior, runtime, monster, [new game_server.players.Player(new PlayerInfo { PlayerId = 1 }) { Position = target }], now);
        Assert.True(monster.Alive);
        Assert.Empty(monster.Movement.Waypoints);
        Assert.Equal(0f, request.Speed);
        Assert.Equal(monster.Info.ObjectInfo.Cell, request.DestinationCell);
        MovementPreparationTestSteps.Advance(runtime, monster.Info.ObjectInfo, monster.Movement, request, 1f);
        Assert.Equal(position, monster.Position);
        Assert.Empty(monster.Movement.Waypoints);
        Assert.True(monster.Alive);
    }
    [Fact]
    public void ClearRemovesRouteAndResetsIndex()
    {
        var state = new MovementState();
        state.Waypoints.Add(MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, new Vector3f(1, 2, 0)));
        Assert.Single(state.Waypoints);
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
        var monster = new Monster { Position = position };
        var movement = monster.Movement;
        var now = DateTime.UtcNow;
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
            MonsterId = 1, Position = position,
            Alive = true, Health = 100, ChaseTargetPlayerId = 77
        };
        monster.Movement.LastProcessedAtUtc = now.AddSeconds(-0.05);
        runtime.Monsters.Entities.Add(monster.MonsterId, monster);
        runtime.RegisterPlayer(new game_server.players.Player(new PlayerInfo { PlayerId = 77 })
        {
            Health = 100,
            Position = destination
        });
        var behavior = new MonsterBehaviorService();
        var request = behavior.CreateMovementRequest(runtime, monster,
            [new game_server.players.Player(new PlayerInfo { PlayerId = 77 }) { Position = destination }], now);
        if (reachable)
            Assert.Equal(MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, destination), request.DestinationCell);
        else
            Assert.Null(request.DestinationCell); // 구역 밖 좌표는 추적 대상에서 제외한다.
        Assert.Empty(monster.Movement.Waypoints);
        Assert.Same(position, monster.Position);

        var movement = new MatchMoveService(null!, behavior);
        movement.ProcessTick(runtime, now);

        if (reachable)
        {
            Assert.NotEmpty(monster.Movement.Waypoints);
            Assert.Equal(request.DestinationCell, monster.Movement.Waypoints[^1]);
            Assert.NotEqual(position, monster.Position);
            Assert.Equal(77, monster.ChaseTargetPlayerId);
        }
        else
        {
            Assert.Empty(monster.Movement.Waypoints);
            Assert.Equal(0, monster.ChaseTargetPlayerId);
            Assert.Equal(position, monster.Position);
        }
    }
}
