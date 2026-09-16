using game_server.matches;
using game_server.players;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class CommonMovementTraversalTests
{
    public CommonMovementTraversalTests()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, -1)]
    [InlineData(-1, 1)]
    [InlineData(-1, -1)]
    public void DiagonalStepsExposeBothCornersAndPreserveSingleCornerPassage(int x, int y)
    {
        var from = new Cell(0, 0);
        var to = new Cell(x, y);
        var step = Assert.Single(MapTraversal.GetSteps(from, to));
        Assert.Equal(from, step.From);
        Assert.Equal(to, step.To);
        Assert.Equal(new Cell(x, 0), step.Horizontal);
        Assert.Equal(new Cell(0, y), step.Vertical);
        Assert.True(MapTraversal.IsTraversable(from, to, cell => !cell.Equals(step.Horizontal)));
        Assert.False(MapTraversal.IsTraversable(from, to,
            cell => !cell.Equals(step.Horizontal) && !cell.Equals(step.Vertical)));
        Assert.False(MapTraversal.IsTraversable(from, to, cell => !cell.Equals(to)));
        Assert.Empty(MapTraversal.GetSteps(from, from));
    }
    [Theory]
    [InlineData(0.4f)]
    [InlineData(-0.4f)]
    [InlineData(0f)]
    public void CellValidatedRouteMovesWithoutCenterDetour(float offsetX)
    {
        var map = Config.SWARM_MATCH_MAP;
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987638);
        using var scope = runtime.Enter();
        foreach (var door in GameDoorData.GetAll())
        {
            for (int x = (int)door.PositionX - 5; x <= door.PositionX + 5; x++)
            {
                for (int y = (int)door.PositionY - 5; y <= door.PositionY + 5; y++)
                {
                    var from = new Cell(x, y);
                    var to = new Cell(x + 1, y + 1);
                    var horizontal = new Cell(x + 1, y);
                    if (!GameMapData.IsMoveablePosition(map, from) ||
                        GameMapData.IsMoveablePosition(map, horizontal) ||
                        !MatchMoveService.CanTraverse(runtime, from, to, true))
                    {
                        continue;
                    }
                    var area = GameMapData.GetCurrentArea(map, from);
                    if (area == AreaType.None || GameMapData.GetCurrentArea(map, to) != area)
                    {
                        continue;
                    }
                    var planned = MapPathfinder.FindPath(map, area, from, area, to);
                    if (planned == null || planned.Count != 1 || !planned[0].Cell.Equals(to))
                    {
                        continue;
                    }
                    var center = MapCoordinateConverter.CellToWorld(map, from);
                    // 셀 중심에서 X축 방향으로 치우친 위치. 셀 좌표는 여전히 from이다.
                    var start = new Vector3f(center.X + offsetX, center.Y, 0f);
                    var end = MapCoordinateConverter.CellToWorld(map, to);
                    Assert.Equal(from, MapCoordinateConverter.WorldToCell(map, start));
                    Assert.False(MatchMoveService.CanTraverse(runtime, from, horizontal, true));
                    var state = new MovementState();
                    var info = new GameObjectInfo { MapId = network.common.Config.SWARM_MATCH_MAP, Cell = from,  Position = start };
                    var now = DateTime.UtcNow;
                    var request = new MovementRequest(to, 1f);
                    MatchMoveService.PrepareMovement(runtime, info, state, request, now, true);
                    Assert.Equal(to, state.Waypoints[0]);
                    Assert.Single(state.Waypoints);
                    Assert.Equal(start, info.Position);
                    var current = start;
                    for (int tick = 0; tick < 200 && state.WaypointIndex < state.Waypoints.Count; tick++)
                    {
                        info.Position = current;
                        info.Cell = MapCoordinateConverter.WorldToCell(map, current);
                        info.Cell = network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(GameMapData.GetCurrentArea(map, info.Cell)));
                        MatchMoveService.PrepareMovement(runtime, info, state, request, now.AddSeconds(tick * 0.01), true);
                        var next = MatchMoveService.MoveAlongPath(runtime, state, current, 0.01f, now, true);
                        Assert.NotEqual(current, next);
                        current = next;
                    }
                    Assert.Equal(end, current);
                    return;
                }
            }
        }
        Assert.Fail("Test map must contain a diagonal route beside a blocked corner.");
    }

    [Fact]
    public void WorldAndCellChecksAgreeAroundEveryDoor()
    {
        var map = Config.SWARM_MATCH_MAP;
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987639);
        using var scope = runtime.Enter();
        int accepted = 0;
        int rejected = 0;
        foreach (var door in GameDoorData.GetAll())
        {
            for (int x = (int)door.PositionX - 3; x <= door.PositionX + 3; x++)
            {
                for (int y = (int)door.PositionY - 3; y <= door.PositionY + 3; y++)
                {
                    var from = new Cell(x, y);
                    if (!GameMapData.IsMoveablePosition(map, from)) continue;
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        for (int dy = -2; dy <= 2; dy++)
                        {
                            var to = new Cell(x + dx, y + dy);
                            bool expected = GameMapData.IsMoveablePosition(map, to) &&
                                MapTraversal.IsTraversable(from, to, cell => GameMapData.IsMoveablePosition(map, cell));
                            var start = MapCoordinateConverter.CellToWorld(map, from);
                            var end = MapCoordinateConverter.CellToWorld(map, to);
                            Assert.Equal(expected, MapTraversal.IsTraversable(map, start, end));
                            Assert.Equal(expected, MatchMoveService.CanTraverse(runtime, from, to, true));
                            Assert.Equal(expected, MapPathfinder.IsSegmentWalkable(map, start, end));
                            if (expected) accepted++;
                            else rejected++;
                        }
                    }
                }
            }
        }
        Assert.True(accepted > 0);
        Assert.True(rejected > 0);
    }

    [Fact]
    public void ShortBlockedStepIsRejectedForPlayerBotAndMonster()
    {
        var map = Config.SWARM_MATCH_MAP;
        Cell? startCell = null;
        Cell? blockedCell = null;
        foreach (var door in GameDoorData.GetAll())
        {
            for (int x = (int)door.PositionX - 5; x <= door.PositionX + 5 && startCell == null; x++)
            {
                for (int y = (int)door.PositionY - 5; y <= door.PositionY + 5; y++)
                {
                    var from = new Cell(x, y);
                    var to = new Cell(x + 1, y);
                    if (GameMapData.IsMoveablePosition(map, from) && !GameMapData.IsMoveablePosition(map, to))
                    {
                        startCell = from;
                        blockedCell = to;
                        break;
                    }
                }
            }
            if (startCell != null) break;
        }
        Assert.NotNull(startCell);
        Assert.NotNull(blockedCell);
        var start = MapCoordinateConverter.CellToWorld(map, startCell);
        var end = MapCoordinateConverter.CellToWorld(map, blockedCell);
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987631);
        using var scope = runtime.Enter();
        var player = new Player(new PlayerInfo { PlayerId = 1 });
        player.InitializeSpawn(startCell);
        runtime.RegisterPlayer(player);
        var service = new PlayerMovementService(NullLogger<PlayerMovementService>.Instance);
        bool correction = service.ProcessMovement(runtime, player,
            new C_TO_G_MOVE { Position = end, Velocity = new Vector3f() }, 1f);
        Assert.True(correction);
        Assert.Equal(start, player.Position);
        Assert.False(MapPathfinder.IsSegmentWalkable(map, start, end));
        foreach (bool ignoreDoors in new[] { false, true })
        {
            var path = new MovementState();
            path.Waypoints.Add(MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, end));
            MatchMoveService.PrepareMovement(runtime, player.GameInfo.ObjectInfo, path,
                new MovementRequest(new Cell(-10000, -10000), 1f), DateTime.UtcNow, ignoreDoors);
            Assert.Empty(path.Waypoints);
            Assert.Equal(start, MatchMoveService.MoveAlongPath(runtime, path, start, 10f, DateTime.UtcNow, ignoreDoors));
            Assert.Equal(0, path.WaypointIndex);
        }
    }

    [Fact]
    public void SpawnRoomRoutesRemainTraversableWithSharedTerrainRules()
    {
        var map = Config.SWARM_MATCH_MAP;
        foreach (var fromArea in MatchSpawnData.GetPhaseRoomCandidates())
        {
            foreach (var toArea in MatchSpawnData.GetPhaseRoomCandidates())
            {
                if (fromArea == toArea) continue;
                var fromCell = GameMapData.GetAreaSpawnCell(map, fromArea);
                var toCell = GameMapData.GetAreaSpawnCell(map, toArea);
                var path = MapPathfinder.FindPath(map, fromArea, fromCell, toArea, toCell);
                Assert.NotNull(path);
                Assert.NotEmpty(path);
                var previous = MapCoordinateConverter.CellToWorld(map, fromCell);
                foreach (var step in path)
                {
                    var next = MapCoordinateConverter.CellToWorld(map, step.Cell);
                    Assert.True(MapTraversal.IsTraversable(map, previous, next),
                        $"Blocked route {fromArea} -> {toArea} at {step.Cell}");
                    previous = next;
                }
            }
        }
    }
    [Fact]
    public void DoorPolicyIsBidirectionalAndIsolatedPerMatch()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(987632);
        var second = store.GetOrCreate(987633);
        using var firstScope = first.Enter();
        using var secondScope = second.Enter();
        foreach (var door in GameDoorData.GetAll())
        {
            var cell = new Cell((int)door.PositionX, (int)door.PositionY);
            first.Doors.OpenDoor(door.DoorId);
            Assert.Null(first.Doors.GetBlockingDoor(door.AreaType, door.AreaTypeB, cell, cell));
            Assert.NotNull(second.Doors.GetBlockingDoor(door.AreaType, door.AreaTypeB, cell, cell));
            Assert.NotNull(second.Doors.GetBlockingDoor(door.AreaTypeB, door.AreaType, cell, cell));
            Assert.Null(second.Doors.GetBlockingDoor(door.AreaType, door.AreaTypeB, cell, cell, ignoreClosedDoors: true));
            Assert.Null(second.Doors.GetBlockingDoor(door.AreaType, door.AreaType, cell, cell));
        }
    }
}
