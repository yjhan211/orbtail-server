using game_server.matches;
using game_server.services;
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
        var state = new PlayerInteractionState();
        state.Begin(10);
        Assert.True(state.TryFinish(10));
        Assert.False(state.TryFinish(10));
        state.Begin(11);
        state.Clear();
        Assert.False(state.TryFinish(11));
        Assert.Equal(0, state.Count);
    }

    [Fact]
    public void FirstDoorSurvivesHitButLaterDoorDoesNot()
    {
        var state = new PlayerInteractionState();
        state.BeginDoor(10, 0);
        Assert.Null(state.InterruptDoor());
        Assert.True(state.TryFinishDoor(10, 3000, TimeSpan.FromSeconds(3), out _));
        state.CompleteDoor();
        state.BeginDoor(11, 3000);
        Assert.Equal(11, state.InterruptDoor());
        Assert.False(state.TryFinish(11));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(12)]
    public void DoorFinishRequiresServerElapsedTimeAndIsSingleUse(int seconds)
    {
        var state = new PlayerInteractionState();
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
        var state = new PlayerInteractionState();
        var duration = TimeSpan.FromSeconds(3);
        state.BeginDoor(10, 0);
        state.BeginDoor(10, 2000);
        Assert.False(state.TryFinishDoor(10, 3000, duration, out var error));
        Assert.Equal(ErrorCode.DOOR_OPEN_TOO_EARLY, error);
        state.BeginDoor(11, 3000);
        Assert.False(state.TryFinishDoor(10, 6000, duration, out error));
        Assert.Equal(ErrorCode.INVALID_GAME_STATE, error);
        state.Clear();
        Assert.False(state.TryFinishDoor(11, 6000, duration, out error));
        Assert.Equal(ErrorCode.INVALID_GAME_STATE, error);
    }

    [Fact]
    public void CancelPendingInteractionsInvalidatesDoorFinish()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(984403);
        var state = new PlayerInteractionState();
        using (MatchRuntimeStore.Enter(runtime))
        {
            state.Begin(10);
            state.BeginDoor(11, 0);

            int[] canceled = MatchInteractionService.CancelPendingInteractions(runtime, state);

            Assert.Equal(new[] { 10, 11 }, canceled.OrderBy(id => id));
            Assert.Equal(0, state.Count);
            Assert.False(state.TryFinish(10));
            Assert.False(state.TryFinishDoor(11, 6000, TimeSpan.FromSeconds(3), out _));

            Assert.Empty(MatchInteractionService.CancelPendingInteractions(runtime, state));
            runtime.TryMarkTerminal();
        }
    }

    [Fact]
    public void CancelPendingInteractionsRequiresMatchLock()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(984404);
        var state = new PlayerInteractionState();
        state.Begin(10);

        Assert.Throws<InvalidOperationException>(() =>
            MatchInteractionService.CancelPendingInteractions(runtime, state));
        Assert.Equal(1, state.Count);

        using (MatchRuntimeStore.Enter(runtime))
        {
            MatchInteractionService.CancelPendingInteractions(runtime, state);
            runtime.TryMarkTerminal();
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
            runtime.TryMarkTerminal();
        }
    }

}
