using network.common.data.models;
using game_server.players;
using game_server.field;
using game_server.matches;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.helpers;

namespace demo_regression_tests;

public sealed class MatchInteractionServiceTests
{
    public MatchInteractionServiceTests()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "server.sln")))
            directory = directory.Parent;
        if (directory == null) throw new DirectoryNotFoundException("Repository root not found.");
        GameDataHelper.SetBasePath(Path.Combine(directory.FullName, "network"));
        GameDataHelper.Initialize();
    }

    [Fact]
    public void FinishIsSingleUseAndCancellationInvalidatesPending()
    {
        var state = new MatchPlayer { Profile = new PlayerInfo { PlayerId = 1 } };
        state.BeginInteraction(10);
        Assert.True(state.TryFinishInteraction(10));
        Assert.False(state.TryFinishInteraction(10));
        state.BeginInteraction(11);
        state.ClearPendingInteractions();
        Assert.False(state.TryFinishInteraction(11));
        Assert.Equal(0, state.PendingInteractionCount);
    }

    [Fact]
    public void FirstDoorSurvivesHitButLaterDoorDoesNot()
    {
        var state = new MatchPlayer { Profile = new PlayerInfo { PlayerId = 1 } };
        state.BeginDoor(10, 0);
        Assert.Null(state.InterruptDoor());
        Assert.True(state.TryFinishDoor(10, 3000, TimeSpan.FromSeconds(3), out _));
        state.CompleteDoor();
        state.BeginDoor(11, 3000);
        Assert.Equal(11, state.InterruptDoor());
        Assert.False(state.TryFinishInteraction(11));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(12)]
    public void DoorFinishRequiresServerElapsedTimeAndIsSingleUse(int seconds)
    {
        var state = new MatchPlayer { Profile = new PlayerInfo { PlayerId = 1 } };
        var duration = TimeSpan.FromSeconds(seconds);
        state.BeginDoor(10, 1000);
        Assert.False(state.TryFinishDoor(10, 1000, duration, out var error));
        Assert.Equal(ErrorCode.DOOR_OPEN_TOO_EARLY, error);
        Assert.False(state.TryFinishDoor(10, 1000 + seconds * 1000 - 1, duration, out error));
        Assert.Equal(ErrorCode.DOOR_OPEN_TOO_EARLY, error);
        Assert.False(state.TryFinishDoor(11, 1000 + seconds * 1000, duration, out error));
        Assert.Equal(ErrorCode.INVALID_GAME_STATE, error);
        Assert.True(state.TryFinishDoor(10, 1000 + seconds * 1000, duration, out error));
        Assert.Equal(ErrorCode.SUCCESS, error);
        Assert.False(state.TryFinishDoor(10, 1000 + seconds * 1000, duration, out error));
        Assert.Equal(ErrorCode.INVALID_GAME_STATE, error);
    }

    [Fact]
    public void DoorRestartResetsTimeAndCancelInvalidatesFinish()
    {
        var state = new MatchPlayer { Profile = new PlayerInfo { PlayerId = 1 } };
        var duration = TimeSpan.FromSeconds(3);
        state.BeginDoor(10, 0);
        state.BeginDoor(10, 2000);
        Assert.False(state.TryFinishDoor(10, 3000, duration, out var error));
        Assert.Equal(ErrorCode.DOOR_OPEN_TOO_EARLY, error);
        state.BeginDoor(11, 3000);
        Assert.False(state.TryFinishDoor(10, 6000, duration, out error));
        Assert.Equal(ErrorCode.INVALID_GAME_STATE, error);
        state.ClearPendingInteractions();
        Assert.False(state.TryFinishDoor(11, 6000, duration, out error));
        Assert.Equal(ErrorCode.INVALID_GAME_STATE, error);
    }

    [Fact]
    public void CancelPendingInteractionsInvalidatesDoorFinish()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(984403);
        var state = new MatchPlayer { Profile = new PlayerInfo { PlayerId = 1 } };
        using (MatchRuntimeStore.Enter(runtime))
        {
            state.BeginInteraction(10);
            state.BeginDoor(11, 0);

            int[] canceled = MatchInteractionService.CancelPendingInteractions(runtime, state);

            Assert.Equal(new[] { 10, 11 }, canceled.OrderBy(id => id));
            Assert.Equal(0, state.PendingInteractionCount);
            Assert.False(state.TryFinishInteraction(10));
            Assert.False(state.TryFinishDoor(11, 6000, TimeSpan.FromSeconds(3), out _));

            Assert.Empty(MatchInteractionService.CancelPendingInteractions(runtime, state));
            runtime.TryMarkEnded();
        }
    }

    [Fact]
    public void CancelPendingInteractionsRequiresMatchLock()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(984404);
        var state = new MatchPlayer { Profile = new PlayerInfo { PlayerId = 1 } };
        state.BeginInteraction(10);

        Assert.Throws<InvalidOperationException>(() =>
            MatchInteractionService.CancelPendingInteractions(runtime, state));
        Assert.Equal(1, state.PendingInteractionCount);

        using (MatchRuntimeStore.Enter(runtime))
        {
            MatchInteractionService.CancelPendingInteractions(runtime, state);
            runtime.TryMarkEnded();
        }
    }
    [Fact]
    public void UnknownInteractionAndDoorAreRejected()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(984401);
        Assert.Throws<InvalidOperationException>(() =>
            MatchInteractionService.CheckDoorGauge(runtime, AreaType.None, int.MaxValue));
        using (MatchRuntimeStore.Enter(runtime))
        {

            Assert.Equal(ErrorCode.INVALID_GAME_STATE, MatchInteractionService.CheckDoorGauge(runtime, AreaType.None, int.MaxValue));
            runtime.TryMarkEnded();
        }
    }

}
