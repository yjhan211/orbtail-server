using game_server.matches;
using game_server.players;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class PlayerInteractionServiceTests
{
    private readonly PlayerInteractionService _interactions = new();

    [Fact]
    public void InteractionStateClearsPendingAndUsesNewStartTime()
    {
        var state = new PlayerInteractState();
        state.Begin(10, TestTime.Ms(1000));
        Assert.False(state.Cancel(11));
        Assert.False(state.TryComplete(10, TestTime.Ms(3999), TimeSpan.FromSeconds(3), out _));
        Assert.True(state.TryComplete(10, TestTime.Ms(4000), TimeSpan.FromSeconds(3), out var error));
        Assert.Equal(ErrorCode.SUCCESS, error);
        Assert.Null(state.PendingInteractId);
        state.Begin(20, TestTime.Ms(5000));
        Assert.Equal(20, state.Cancel());
        Assert.Null(state.PendingInteractId);
        state.Begin(30, TestTime.Ms(9000));
        Assert.False(state.TryComplete(30, TestTime.Ms(11999), TimeSpan.FromSeconds(3), out _));
        Assert.True(state.TryComplete(30, TestTime.Ms(12000), TimeSpan.FromSeconds(3), out _));
    }

    public PlayerInteractionServiceTests()
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
        var state = new Player(new PlayerInfo { PlayerId = 1 });
        state.Interactions.Begin(10, TestTime.Ms(0));
        Assert.True(state.Interactions.Cancel(10));
        Assert.False(state.Interactions.Cancel(10));
        state.Interactions.Begin(11, TestTime.Ms(0));
        state.Interactions.Cancel();
        Assert.False(state.Interactions.Cancel(11));
        Assert.Null(state.Interactions.PendingInteractId);
    }

    [Fact]
    public void AnyPendingDoorIsInterruptedByHit()
    {
        var state = new Player(new PlayerInfo { PlayerId = 1 });
        state.Interactions.Begin(10, TestTime.Ms(0));
        Assert.Equal(10, state.Interactions.Cancel());
        Assert.False(state.Interactions.TryComplete(10, TestTime.Ms(3000), TimeSpan.FromSeconds(3), out _));
        Assert.Null(state.Interactions.Cancel());
        state.Interactions.Begin(11, TestTime.Ms(3000));
        Assert.Equal(11, state.Interactions.Cancel());
        Assert.False(state.Interactions.Cancel(11));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(12)]
    public void DoorFinishRequiresServerElapsedTimeAndIsSingleUse(int seconds)
    {
        var state = new Player(new PlayerInfo { PlayerId = 1 });
        var duration = TimeSpan.FromSeconds(seconds);
        state.Interactions.Begin(10, TestTime.Ms(1000));
        Assert.False(state.Interactions.TryComplete(10, TestTime.Ms(1000), duration, out var error));
        Assert.Equal(ErrorCode.DOOR_OPEN_TOO_EARLY, error);
        Assert.False(state.Interactions.TryComplete(10, TestTime.Ms(1000 + seconds * 1000 - 1), duration, out error));
        Assert.Equal(ErrorCode.DOOR_OPEN_TOO_EARLY, error);
        Assert.False(state.Interactions.TryComplete(11, TestTime.Ms(1000 + seconds * 1000), duration, out error));
        Assert.Equal(ErrorCode.INVALID_GAME_STATE, error);
        Assert.True(state.Interactions.TryComplete(10, TestTime.Ms(1000 + seconds * 1000), duration, out error));
        Assert.Equal(ErrorCode.SUCCESS, error);
        Assert.False(state.Interactions.TryComplete(10, TestTime.Ms(1000 + seconds * 1000), duration, out error));
        Assert.Equal(ErrorCode.INVALID_GAME_STATE, error);
    }

    [Fact]
    public void DoorRestartResetsTimeAndCancelInvalidatesFinish()
    {
        var state = new Player(new PlayerInfo { PlayerId = 1 });
        var duration = TimeSpan.FromSeconds(3);
        state.Interactions.Begin(10, TestTime.Ms(0));
        state.Interactions.Begin(10, TestTime.Ms(2000));
        Assert.False(state.Interactions.TryComplete(10, TestTime.Ms(3000), duration, out var error));
        Assert.Equal(ErrorCode.DOOR_OPEN_TOO_EARLY, error);
        state.Interactions.Begin(11, TestTime.Ms(3000));
        Assert.Equal(11, state.Interactions.PendingInteractId);
        Assert.False(state.Interactions.TryComplete(10, TestTime.Ms(6000), duration, out error));
        Assert.Equal(ErrorCode.INVALID_GAME_STATE, error);
        state.Interactions.Cancel();
        Assert.False(state.Interactions.TryComplete(11, TestTime.Ms(6000), duration, out error));
        Assert.Equal(ErrorCode.INVALID_GAME_STATE, error);
    }

    [Fact]
    public void CancelPendingInteractionsInvalidatesDoorFinish()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(984403);
        var state = new Player(new PlayerInfo { PlayerId = 1 });
        using (MatchRuntimeStore.Enter(runtime))
        {
            state.Interactions.Begin(10, TestTime.Ms(0));
            state.Interactions.Begin(11, TestTime.Ms(0));

            int? canceled = _interactions.CancelPendingInteractions(runtime, state);

            Assert.Equal(11, canceled);
            Assert.Null(state.Interactions.PendingInteractId);
            Assert.False(state.Interactions.Cancel(10));
            Assert.False(state.Interactions.TryComplete(11, TestTime.Ms(6000), TimeSpan.FromSeconds(3), out _));

            Assert.Null(_interactions.CancelPendingInteractions(runtime, state));
            runtime.TryMarkEnded();
        }
    }

    [Fact]
    public void CancelPendingInteractionsRequiresMatchLock()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(984404);
        var state = new Player(new PlayerInfo { PlayerId = 1 });
        state.Interactions.Begin(10, TestTime.Ms(0));

        Assert.Throws<InvalidOperationException>(() =>
            _interactions.CancelPendingInteractions(runtime, state));
        Assert.Equal(10, state.Interactions.PendingInteractId);

        using (MatchRuntimeStore.Enter(runtime))
        {
            _interactions.CancelPendingInteractions(runtime, state);
            runtime.TryMarkEnded();
        }
    }
    [Fact]
    public void UnknownInteractionAndDoorAreRejected()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(984401);
        Assert.Throws<InvalidOperationException>(() =>
            _interactions.ValidateDoor(runtime, new Player(new PlayerInfo { PlayerId = 1 }), int.MaxValue, int.MaxValue));
        using (MatchRuntimeStore.Enter(runtime))
        {

            Assert.Equal(ErrorCode.INVALID_GAME_STATE, _interactions.ValidateDoor(runtime, new Player(new PlayerInfo { PlayerId = 1 }), int.MaxValue, int.MaxValue));
            runtime.TryMarkEnded();
        }
    }

    [Theory]
    [InlineData(101)]
    [InlineData(-101)]
    public void SharedDoorFlowChecksTimeAndInterruptsFirstDoor(long playerId)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "server.sln")))
            directory = directory.Parent;
        network.common.data.helpers.GameDataHelper.SetBasePath(Path.Combine(directory!.FullName, "network"));
        network.common.data.helpers.GameDataHelper.Initialize();
        var info = GameInteractableData.GetAll().First(item => item.DoorId > 0 && GameDoorData.Get(item.DoorId) != null);
        var door = GameDoorData.Get(info.DoorId)!;
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(984405);
        var player = new Player(new PlayerInfo { PlayerId = playerId }) {  Cell = new Cell(info.CellX, info.CellY) };
        long duration = (long)TimeSpan.FromSeconds(Config.GetSwarmDoorGaugeSeconds(door.DoorId)).TotalMilliseconds;
        using (match.Enter())
        {
            Assert.False(_interactions.TryFinishDoor(match, player, info.Id, door.DoorId, TestTime.Ms(duration), out _));
            Assert.Equal(ErrorCode.SUCCESS, _interactions.StartDoor(match, player, info.Id, door.DoorId, TestTime.Ms(0)));
            Assert.Equal(info.Id, player.Interactions.Cancel());
            Assert.False(_interactions.TryFinishDoor(match, player, info.Id, door.DoorId, TestTime.Ms(duration), out _));
            Assert.Equal(ErrorCode.SUCCESS, _interactions.StartDoor(match, player, info.Id, door.DoorId, TestTime.Ms(0)));
            Assert.False(_interactions.TryFinishDoor(match, player, info.Id, door.DoorId, TestTime.Ms(duration - 1), out var error));
            Assert.Equal(ErrorCode.DOOR_OPEN_TOO_EARLY, error);
            Assert.True(_interactions.TryFinishDoor(match, player, info.Id, door.DoorId, TestTime.Ms(duration), out error));
            Assert.True(match.Doors.IsDoorOpen(door.DoorId));
            Assert.False(_interactions.TryFinishDoor(match, player, info.Id, door.DoorId, TestTime.Ms(duration), out _));
            match.Doors.CloseDoorsForAreas([door.AreaType]);
            Assert.Equal(ErrorCode.SUCCESS, _interactions.StartDoor(match, player, info.Id, door.DoorId, TestTime.Ms(duration)));
            Assert.Equal(info.Id, player.Interactions.Cancel());
            Assert.False(_interactions.TryFinishDoor(match, player, info.Id, door.DoorId, TestTime.Ms(duration * 2), out _));
            Assert.False(match.Doors.IsDoorOpen(door.DoorId));
        }
    }

    [Fact]
    public void InteractableSnapshots_AreIndependentCopiesOfSharedDefinitions()
    {
        var definition = GameInteractableData.GetAll().First(item => item.DoorId > 0 && item.Actions.Count > 0);
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(948602);
        var player = TestGameSessionServices.GetOrRegisterPlayer(match, 11);
        player.InitializeSpawn(network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)((AreaType)definition.ZoneId)));
        Assert.Throws<InvalidOperationException>(() => _interactions.GetInteractionInfos(match));
        using var scope = match.Enter();
        var first = _interactions.GetInteractionInfos(match);
        var second = _interactions.GetInteractionInfos(match);
        Assert.NotEmpty(first);
        Assert.Contains(first, state => state.InteractId == definition.Id);
        Assert.Equal(first.Count, second.Count);
        int originalId = second[0].InteractId;
        first[0].InteractId = -1;
        Assert.Equal(originalId, second[0].InteractId);
        Assert.NotNull(GameInteractableData.Get(originalId));
    }
    [Fact]
    public void InteractableInfoContainsCompletionAndEmptyListRoundTrips()
    {
        var info = new InteractableInfo { InteractId = 17 };
        var json = MessagePack.MessagePackSerializer.ConvertToJson(
            MessagePack.MessagePackSerializer.Serialize(info));
        Assert.Contains("interactId", json);
        Assert.DoesNotContain("actions", json);
        Assert.Equal(2, typeof(InteractableInfo).GetProperties().Length);
        Assert.Contains("isCompleted", json);

        var packet = new G_TO_C_INTERACTABLE_INFO
        {
            AreaType = AreaType.S2Gym1,
            Objects = new List<InteractableInfo>()
        };
        var copy = MessagePack.MessagePackSerializer.Deserialize<G_TO_C_INTERACTABLE_INFO>(
            MessagePack.MessagePackSerializer.Serialize(packet));
        Assert.Equal(packet.AreaType, copy.AreaType);
        Assert.Empty(copy.Objects);
    }
    [Fact]
    public void InteractionInfosIncludeOpenAndClosedDoorsAcrossAllAreas()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(948603);
        using var scope = runtime.Enter();
        var definition = GameInteractableData.GetAll().First(item => item.DoorId > 0);
        runtime.Doors.OpenDoor(definition.DoorId);
        var infos = _interactions.GetInteractionInfos(runtime);
        Assert.Contains(infos, info => info.InteractId == definition.Id && info.IsCompleted);
        Assert.All(GameDoorData.GetAll(), door =>
            Assert.Contains(infos, info => GameInteractableData.Get(info.InteractId)?.DoorId == door.DoorId));
        runtime.Doors.Clear();
        var closed = _interactions.GetInteractionInfos(runtime);
        Assert.Equal(infos.Count, closed.Count);
        Assert.All(closed, info => Assert.False(info.IsCompleted));
    }
}
