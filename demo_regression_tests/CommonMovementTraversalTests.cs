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
        var player = new Player { Profile = new PlayerInfo { PlayerId = 1 } };
        player.InitializeSpawn(startCell);
        runtime.RegisterParticipant(player);
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
