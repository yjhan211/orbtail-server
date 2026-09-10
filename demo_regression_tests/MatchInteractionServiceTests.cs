using game_server.field;
using game_server.matches;
using game_server.players;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.helpers;
using network.common.data.models;

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
        var state = new Player { Profile = new PlayerInfo { PlayerId = 1 } };
        state.BeginInteraction(10);
        Assert.True(state.TryFinishInteraction(10));
        Assert.False(state.TryFinishInteraction(10));
        state.BeginInteraction(11);
        state.ClearPendingInteractions();
        Assert.False(state.TryFinishInteraction(11));
        Assert.Empty(state.GetPendingInteractionIds());
    }

    [Fact]
    public void FirstDoorSurvivesHitButLaterDoorDoesNot()
    {
        var state = new Player { Profile = new PlayerInfo { PlayerId = 1 } };
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
        var state = new Player { Profile = new PlayerInfo { PlayerId = 1 } };
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
        var state = new Player { Profile = new PlayerInfo { PlayerId = 1 } };
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
        var state = new Player { Profile = new PlayerInfo { PlayerId = 1 } };
        using (MatchRuntimeStore.Enter(runtime))
        {
            state.BeginInteraction(10);
            state.BeginDoor(11, 0);

            int[] canceled = MatchInteractionService.CancelPendingInteractions(runtime, state);

            Assert.Equal(new[] { 10, 11 }, canceled.OrderBy(id => id));
            Assert.Empty(state.GetPendingInteractionIds());
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
        var state = new Player { Profile = new PlayerInfo { PlayerId = 1 } };
        state.BeginInteraction(10);

        Assert.Throws<InvalidOperationException>(() =>
            MatchInteractionService.CancelPendingInteractions(runtime, state));
        Assert.Single(state.GetPendingInteractionIds());

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

    [Theory]
    [InlineData(101)]
    [InlineData(-101)]
    public void SharedDoorFlowChecksTimeAndFirstDoorProtection(long playerId)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "server.sln")))
            directory = directory.Parent;
        network.common.data.helpers.GameDataHelper.SetBasePath(Path.Combine(directory!.FullName, "network"));
        network.common.data.helpers.GameDataHelper.Initialize();
        var door = network.common.data.GameDoorData.GetAll().First();
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(984405);
        var player = new Player { Profile = new PlayerInfo { PlayerId = playerId }, CurrentArea = door.AreaType };
        long duration = (long)TimeSpan.FromSeconds(Config.GetSwarmDoorGaugeSeconds(door.DoorId)).TotalMilliseconds;
        using (match.Enter())
        {
            Assert.False(MatchInteractionService.TryFinishDoor(match, player, 10, door.DoorId, duration, out _));
            Assert.Equal(ErrorCode.SUCCESS, MatchInteractionService.StartDoor(match, player, 10, door.DoorId, 0));
            Assert.Null(player.InterruptDoor());
            Assert.False(MatchInteractionService.TryFinishDoor(match, player, 10, door.DoorId, duration - 1, out var error));
            Assert.Equal(ErrorCode.DOOR_OPEN_TOO_EARLY, error);
            Assert.True(MatchInteractionService.TryFinishDoor(match, player, 10, door.DoorId, duration, out error));
            Assert.True(match.Doors.IsDoorOpen(door.DoorId));
            Assert.False(MatchInteractionService.TryFinishDoor(match, player, 10, door.DoorId, duration, out _));
            match.Doors.CloseDoorsForAreas([door.AreaType]);
            Assert.Equal(ErrorCode.SUCCESS, MatchInteractionService.StartDoor(match, player, 11, door.DoorId, duration));
            Assert.Equal(11, player.InterruptDoor());
            Assert.False(MatchInteractionService.TryFinishDoor(match, player, 11, door.DoorId, duration * 2, out _));
            Assert.False(match.Doors.IsDoorOpen(door.DoorId));
        }
    }
}
