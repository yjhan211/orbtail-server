using game_server.players;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class PlayerMovementCompletionTests
{
    [Theory]
    [InlineData(0f, PlayerState.EXPLORE_1)]
    [InlineData(1f, PlayerState.IDLE)]
    public void CompletionOnlyCancelsExplorationWhileMoving(float speed, PlayerState expected)
    {
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987640);
        using var scope = runtime.Enter();
        var player = new Player(new PlayerInfo { PlayerId = 1 });
        player.State = PlayerState.EXPLORE_1;
        player.GameInfo.ObjectInfo.Position = new Vector3f(10, 10, 0);
        player.GameInfo.ObjectInfo.Velocity = new Vector3f(speed, 0, 0);

        PlayerMovementService.CancelDoorOpeningIfMoved(runtime, player);

        Assert.Equal(expected, player.State);
    }

    [Fact]
    public void CompletionRequiresMatchLock()
    {
        var runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(987641);
        var player = new Player(new PlayerInfo { PlayerId = 1 });
        Assert.Throws<InvalidOperationException>(() => PlayerMovementService.CancelDoorOpeningIfMoved(runtime, player));
    }
}
