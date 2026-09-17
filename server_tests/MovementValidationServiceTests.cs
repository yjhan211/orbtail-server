using game_server.players;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;

namespace server_tests;

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
        var resultCell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, result.Position);
        Assert.Equal(cell.X, resultCell.X);
        Assert.Equal(cell.Y, resultCell.Y);
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
        var keptCell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, result.Position);
        Assert.Equal(cell.X, keptCell.X);
        Assert.Equal(cell.Y, keptCell.Y);
        Assert.Equal(0f, result.Velocity.Magnitude());
    }

    [Fact]
    public void BroadcastVelocityCannotExceedAuthoritativeSpeed()
    {
        var cell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, MatchSpawnData.GetPhaseRoomCandidates()[0]);
        var position = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, cell);
        var velocity = new Vector3f(30f, 40f, 0f);

        var result = ProcessMovement(position, cell, position, velocity, 0.05f);

        // 위치가 정상이면 주장한 속도가 과해도 보정하지 않는다. 속도는 실제 이동량에서 다시 구하므로 상한을 넘지 못한다.
        Assert.False(result.RequiresCorrection);
        Assert.InRange(result.Velocity.Magnitude(), 0f, PlayerMovementService.MaximumSpeedUnitsPerSecond + 0.001f);
        Assert.True((result.Position - position).Magnitude() <= PlayerMovementService.MaximumSpeedUnitsPerSecond * 0.05f + 0.001f);
        Assert.Equal(30f, velocity.X);
        Assert.Equal(40f, velocity.Y);
    }

    // 검증 결과를 단언하기 쉽게 묶는 테스트용 값이다.
    private readonly record struct ValidatedMovement(Vector3f Position, Vector3f Velocity, bool RequiresCorrection);

    private ValidatedMovement ProcessMovement(Vector3f position, Cell cell, Vector3f input, Vector3f velocity, float deltaTime)
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(948012);
        var player = new Player(new PlayerInfo { PlayerId = 1 });
        using (match.Enter())
        {
            player.InitializeSpawn(cell);
            player.Position = position; // 셀은 위치에서 파생된다
            match.RegisterPlayer(player);
            bool correction = _service.ProcessMovement(match, player, new C_TO_G_MOVE
            {
                Position = input,
                Velocity = velocity
            }, deltaTime);
            return new ValidatedMovement(player.Position!, player.Velocity, correction);
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
