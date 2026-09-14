using game_server.matches;
using game_server.players;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class DoorInteractionCellTests
{
    [Fact]
    public void RegisteredDoorCellsAreReachableFromTheirInteractionArea()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var failures = new List<string>();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(982014);
        using var scope = runtime.Enter();
        foreach (var info in GameInteractableData.GetAll())
        {
            if (info.DoorId <= 0 || GameDoorData.Get(info.DoorId) == null) continue;
            var cell = new Cell(info.CellX, info.CellY);
            var area = (AreaType)info.ZoneId;
            var actualArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, cell);
            if (!GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, cell) || actualArea != area)
            {
                failures.Add($"{info.Id}: door={info.DoorId}, cell=({cell.X},{cell.Y}), configured={area}, actual={actualArea}, walkable={GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, cell)}");
                continue;
            }
            var start = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, area);
            var path = MapPathfinder.FindPath(Config.SWARM_MATCH_MAP, area, start, area, cell);
            if (path == null || path.Count == 0)
            {
                failures.Add($"{info.Id}: no path from area spawn");
                continue;
            }
            var movement = new MovementState();
            foreach (var step in path)
                movement.Waypoints.Add(MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, step.Cell));
            var reached = MatchMovementService.AdvanceRoute(runtime, movement,
                MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, start), 10000f);
            var reachedCell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, reached);
            if (reachedCell.X != cell.X || reachedCell.Y != cell.Y)
                failures.Add($"{info.Id}: closed door prevents arrival");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
    [Theory]
    [InlineData(101)]
    [InlineData(-101)]
    public void StartAndFinishRequireTheRegisteredCell(long playerId)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(982015);
        using var scope = runtime.Enter();
        var info = GameInteractableData.Get(702000113)!;
        var player = new Player { Profile = new PlayerInfo { PlayerId = playerId }, CurrentArea = (AreaType)info.ZoneId,
            Cell = new Cell(info.CellX + 1, info.CellY) };
        var service = new PlayerInteractionService();
        Assert.Equal(ErrorCode.DOOR_TOO_FAR, service.StartDoor(runtime, player, info.Id, info.DoorId, 0));
        Assert.Null(player.PendingDoorInteractionId);
        player.Cell = new Cell(info.CellX, info.CellY);
        Assert.Equal(ErrorCode.SUCCESS, service.StartDoor(runtime, player, info.Id, info.DoorId, 0));
        player.Cell = new Cell(info.CellX, info.CellY + 1);
        Assert.False(service.TryFinishDoor(runtime, player, info.Id, info.DoorId, 60000, out var error));
        Assert.Equal(ErrorCode.DOOR_TOO_FAR, error);
        Assert.False(runtime.Doors.IsDoorOpen(info.DoorId));
        Assert.Null(player.PendingDoorInteractionId);
    }
}
