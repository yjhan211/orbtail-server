using game_server.players;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class MovementValidationServiceTests
{
    private readonly PlayerMovementService _service = new(NullLogger<PlayerMovementService>.Instance);

    public MovementValidationServiceTests()
    {
        GameDataHelper.SetBasePath(Path.Combine(FindRoot(), "network"));
        GameDataHelper.Initialize();
    }

    [Fact]
    public void ValidationReturnsNewPositionWithoutChangingClientInput()
    {
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, MatchSpawnData.GetPhaseRoomCandidates()[0]);
        var position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, cell);
        var input = new Vector3f(position.X, position.Y, 5f);

        var result = ProcessMovement(position, cell, input, new Vector3f(0, 0, 0), 0.05f);

        Assert.False(result.RequiresCorrection);
        Assert.Equal(0f, result.Position.Z);
        Assert.Equal(5f, input.Z);
        Assert.Equal(cell.X, result.ValidCell!.X);
        Assert.Equal(cell.Y, result.ValidCell.Y);
    }

    [Fact]
    public void ExcessiveDistanceCannotExceedServerTimeBudget()
    {
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, MatchSpawnData.GetPhaseRoomCandidates()[0]);
        var position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, cell);
        var input = new Vector3f(position.X + 100f, position.Y, 0f);

        var result = ProcessMovement(position, cell, input, new Vector3f(0, 0, 0), 0.05f);

        Assert.True(result.RequiresCorrection);
        Assert.True((result.Position - position).Magnitude() <= PlayerMovementService.MaximumSpeedUnitsPerSecond * 0.05f + 0.001f);
        Assert.True(result.Velocity.Magnitude() <= PlayerMovementService.MaximumSpeedUnitsPerSecond + 0.001f);
    }

    [Fact]
    public void ZeroElapsedTime_DoesNotMoveOrPublishNonzeroVelocity()
    {
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, MatchSpawnData.GetPhaseRoomCandidates()[0]);
        var position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, cell);
        var input = new Vector3f(position.X + 1f, position.Y, 0f);
        var result = ProcessMovement(position, cell,
            input, new Vector3f(1f, 0f, 0f), 0f);
        Assert.Equal(position.X, result.Position.X);
        Assert.Equal(position.Y, result.Position.Y);
        Assert.Equal(0f, result.Velocity.Magnitude());
        Assert.True(result.RequiresCorrection);
    }

    [Fact]
    public void UnwalkableDestinationKeepsPreviouslyAcceptedCell()
    {
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, MatchSpawnData.GetPhaseRoomCandidates()[0]);
        var position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, cell);
        var input = new Vector3f(100000f, 100000f, 0f);

        var result = ProcessMovement(position, cell, input, new Vector3f(0, 0, 0), 100000f);

        Assert.True(result.RequiresCorrection);
        Assert.Same(position, result.Position);
        Assert.Same(cell, result.ValidCell);
        Assert.Equal(0f, result.Velocity.Magnitude());
    }

    [Fact]
    public void SessionCommitsCellOnlyAfterDoorRejectionPath()
    {
        string source = File.ReadAllText(Path.Combine(FindRoot(), "game_server", "Players", "PlayerMovementService.cs"));
        int check = source.IndexOf("match.Doors.GetBlockingDoor(", StringComparison.Ordinal);
        int reject = source.IndexOf("if (transitionDoor != null)", check, StringComparison.Ordinal);
        int stop = source.IndexOf("return true;", reject, StringComparison.Ordinal);
        int commit = source.IndexOf("player.ApplyValidatedMovement(validation.ValidCell, validation.Position, validation.Velocity, msg.Rotation);", StringComparison.Ordinal);
        Assert.True(check >= 0 && reject > check && stop > reject && commit > stop);
        Assert.Contains("WorldToCell(Config.SWARM_MATCH_MAP, player.Position)", source);
        Assert.DoesNotContain("player.Session", source);
        Assert.DoesNotContain("PacketMaker", source);
    }

    [Fact]
    public void BroadcastVelocityCannotExceedAuthoritativeSpeed()
    {
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, MatchSpawnData.GetPhaseRoomCandidates()[0]);
        var position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, cell);
        var velocity = new Vector3f(30f, 40f, 0f);

        var result = ProcessMovement(position, cell, position, velocity, 0.05f);

        Assert.True(result.RequiresCorrection);
        Assert.InRange(result.Velocity.Magnitude(), 0f, PlayerMovementService.MaximumSpeedUnitsPerSecond + 0.001f);
        Assert.True((result.Position - position).Magnitude() <= PlayerMovementService.MaximumSpeedUnitsPerSecond * 0.05f + 0.001f);
        Assert.Equal(30f, velocity.X);
        Assert.Equal(40f, velocity.Y);
    }

    private PlayerMovementService.ValidatedMovement ProcessMovement(Vector3f position, Cell cell, Vector3f input, Vector3f velocity, float deltaTime)
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(948012);
        var player = new Player(new PlayerInfo { PlayerId = 1 });
        using (match.Enter())
        {
            player.InitializeSpawn(cell);
            player.Position = position;
            player.Cell = cell;
            match.RegisterParticipant(player);
            bool correction = _service.ProcessMovement(match, player, new C_TO_G_MOVE
            {
                Position = input,
                Velocity = velocity
            }, deltaTime);
            return new PlayerMovementService.ValidatedMovement(player.Position!, player.Velocity, player.Cell,
                correction);
        }
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "server.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
