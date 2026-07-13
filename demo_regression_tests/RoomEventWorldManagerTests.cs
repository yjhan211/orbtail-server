using game_server.services;
using network.common;
using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

public sealed class RoomEventWorldManagerTests
{
    private static readonly DateTimeOffset InitialTime =
        new(2026, 7, 10, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ActivationUsesServerConfigAndRejectsDuplicateInstance()
    {
        await using var fixture = CreateFixture();

        var first = await fixture.Manager.EnqueueActivateAsync(CreateActivation());
        var duplicate = await fixture.Manager.EnqueueActivateAsync(CreateActivation());

        Assert.True(first.Success);
        Assert.Equal(1, first.State!.EventInstanceId);
        Assert.Equal(RoomEventWorldStatus.Active, first.State.Status);
        Assert.Equal(1, first.State.StateVersion);
        Assert.Equal(InitialTime.UtcDateTime.AddSeconds(120), first.State.ExpiresAtUtc);
        Assert.Equal("record", first.State.RequiredResponseTag);
        Assert.False(duplicate.Success);
        Assert.Equal(RoomEventCommandError.AlreadyActive, duplicate.Error);
        Assert.Equal(first.State, duplicate.State);
    }

    [Fact]
    public async Task ConcurrentSameVersionContributionsConsumeExactlyOneItem()
    {
        await using var fixture = CreateFixture();
        fixture.AddPlayer(100, 301000001, 1);
        fixture.AddPlayer(200, 301000028, 1);
        var activation = await fixture.Manager.EnqueueActivateAsync(CreateActivation());
        using var barrier = new Barrier(2);

        Task<RoomEventCommandResult> first = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await fixture.Manager.EnqueueInterventionAsync(CreateIntervention(
                actorPlayerId: 100,
                activation.State!.EventInstanceId,
                expectedVersion: 1,
                choiceId: 18800301,
                selectedItemId: 301000001));
        });
        Task<RoomEventCommandResult> second = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await fixture.Manager.EnqueueInterventionAsync(CreateIntervention(
                actorPlayerId: 200,
                activation.State!.EventInstanceId,
                expectedVersion: 1,
                choiceId: 18800301,
                selectedItemId: 301000028));
        });

        var results = await Task.WhenAll(first, second);
        var success = Assert.Single(results, result => result.Success);
        var stale = Assert.Single(results, result => !result.Success);
        Assert.Equal(RoomEventCommandError.StaleVersion, stale.Error);
        Assert.Equal(2, success.State!.StateVersion);
        Assert.Equal(1, success.State.CurrentContribution);
        int remaining = fixture.ItemCount(100, 301000001) + fixture.ItemCount(200, 301000028);
        Assert.Equal(1, remaining);
    }

    [Fact]
    public async Task PlayerCanContributeRepeatedlyUntilPersonalCap()
    {
        await using var fixture = CreateFixture();
        fixture.AddPlayer(100, 301000131, 1);
        fixture.AddPlayer(100, 301000001, 3);
        var activation = await fixture.Manager.EnqueueActivateAsync(CreateActivation());

        var crafted = await fixture.Manager.EnqueueInterventionAsync(CreateIntervention(
            100, activation.State!.EventInstanceId, 1, 18800305, 301000131));
        var materialOne = await fixture.Manager.EnqueueInterventionAsync(CreateIntervention(
            100, activation.State.EventInstanceId, crafted.State!.StateVersion, 18800301, 301000001));
        var materialTwo = await fixture.Manager.EnqueueInterventionAsync(CreateIntervention(
            100, activation.State.EventInstanceId, materialOne.State!.StateVersion, 18800301, 301000001));
        var overCap = await fixture.Manager.EnqueueInterventionAsync(CreateIntervention(
            100, activation.State.EventInstanceId, materialTwo.State!.StateVersion, 18800301, 301000001));

        Assert.True(crafted.Success);
        Assert.True(materialOne.Success);
        Assert.True(materialTwo.Success);
        Assert.Equal(5, materialTwo.State!.ContributionByPlayer[100]);
        Assert.False(overCap.Success);
        Assert.Equal(RoomEventCommandError.ContributionLimit, overCap.Error);
        Assert.Equal(1, fixture.ItemCount(100, 301000001));
    }

    [Fact]
    public async Task ContainedRequiresTargetAndTwoDistinctItemIds()
    {
        await using var fixture = CreateFixture();
        fixture.AddPlayer(100, 301000131, 1);
        fixture.AddPlayer(200, 401000007, 1);
        fixture.AddPlayer(300, 301000001, 1);
        fixture.AddPlayer(300, 301000028, 1);
        var state = (await fixture.Manager.EnqueueActivateAsync(CreateActivation())).State!;

        state = (await fixture.Manager.EnqueueInterventionAsync(CreateIntervention(
            100, state.EventInstanceId, state.StateVersion, 18800305, 301000131))).State!;
        state = (await fixture.Manager.EnqueueInterventionAsync(CreateIntervention(
            200, state.EventInstanceId, state.StateVersion, 18800305, 401000007))).State!;
        state = (await fixture.Manager.EnqueueInterventionAsync(CreateIntervention(
            300, state.EventInstanceId, state.StateVersion, 18800301, 301000001))).State!;
        state = (await fixture.Manager.EnqueueInterventionAsync(CreateIntervention(
            300, state.EventInstanceId, state.StateVersion, 18800301, 301000028))).State!;

        Assert.Equal(10, state.CurrentContribution);
        Assert.True(state.DistinctContributedItemIds.Count >= 2);
        Assert.Equal(RoomEventWorldStatus.Contained, state.Status);
    }

    [Fact]
    public async Task SameItemIdCannotContributeMoreThanFivePower()
    {
        await using var fixture = CreateFixture();
        fixture.AddPlayer(100, 401000007, 1);
        fixture.AddPlayer(200, 401000007, 1);
        var state = (await fixture.Manager.EnqueueActivateAsync(CreateActivation())).State!;

        var first = await fixture.Manager.EnqueueInterventionAsync(CreateIntervention(
            100, state.EventInstanceId, state.StateVersion, 18800305, 401000007));
        var second = await fixture.Manager.EnqueueInterventionAsync(CreateIntervention(
            200, state.EventInstanceId, first.State!.StateVersion, 18800305, 401000007));

        Assert.True(first.Success);
        Assert.False(second.Success);
        Assert.Equal(RoomEventCommandError.ItemContributionLimit, second.Error);
        Assert.Equal(1, fixture.ItemCount(200, 401000007));
    }

    [Fact]
    public async Task ExpiredRequestFinalizesOverrunBeforeRejectingWithoutCost()
    {
        await using var fixture = CreateFixture();
        fixture.AddPlayer(100, 301000001, 1);
        var activation = await fixture.Manager.EnqueueActivateAsync(CreateActivation());
        fixture.Time.Advance(TimeSpan.FromSeconds(120));

        var result = await fixture.Manager.EnqueueInterventionAsync(CreateIntervention(
            100, activation.State!.EventInstanceId, 1, 18800301, 301000001));

        Assert.False(result.Success);
        Assert.Equal(RoomEventCommandError.Expired, result.Error);
        Assert.Equal(RoomEventWorldStatus.Overrun, result.State!.Status);
        Assert.Equal(2, result.State.StateVersion);
        Assert.Equal(1, fixture.ItemCount(100, 301000001));
    }

    [Fact]
    public async Task TimeExtensionAndManualResponseUseServerAuthoredCostsAndLimits()
    {
        await using var fixture = CreateFixture();
        fixture.Gateway.SetArea(100, AreaType.BroadcastRoom);
        fixture.Gateway.SetArea(200, AreaType.BroadcastRoom);
        fixture.Gateway.SetArea(300, AreaType.BroadcastRoom);
        var state = (await fixture.Manager.EnqueueActivateAsync(CreateActivation())).State!;

        var extension = await fixture.Manager.EnqueueInterventionAsync(CreateIntervention(
            100, state.EventInstanceId, state.StateVersion, 18800302, 0));
        var manualOne = await fixture.Manager.EnqueueInterventionAsync(CreateIntervention(
            100, state.EventInstanceId, extension.State!.StateVersion, 18800303, 0));
        var manualTwo = await fixture.Manager.EnqueueInterventionAsync(CreateIntervention(
            200, state.EventInstanceId, manualOne.State!.StateVersion, 18800303, 0));
        var manualLimit = await fixture.Manager.EnqueueInterventionAsync(CreateIntervention(
            300, state.EventInstanceId, manualTwo.State!.StateVersion, 18800303, 0));

        Assert.True(extension.Success);
        Assert.Equal(-15, extension.AppliedStaminaDelta);
        Assert.Equal(InitialTime.UtcDateTime.AddSeconds(135), extension.State!.ExpiresAtUtc);
        Assert.Equal(10, manualOne.AppliedMentalDelta);
        Assert.Equal(2, manualTwo.State!.ManualResponsesUsed);
        Assert.False(manualLimit.Success);
        Assert.Equal(RoomEventCommandError.ManualResponseLimit, manualLimit.Error);
    }

    [Fact]
    public async Task MatchingCleanupAndStateAreIsolated()
    {
        await using var fixture = CreateFixture();
        fixture.AddPlayer(100, 301000001, 1, matchingId: 10);
        fixture.AddPlayer(100, 301000001, 1, matchingId: 20);
        var first = await fixture.Manager.EnqueueActivateAsync(CreateActivation(matchingId: 10));
        var second = await fixture.Manager.EnqueueActivateAsync(CreateActivation(matchingId: 20));

        var changed = await fixture.Manager.EnqueueInterventionAsync(CreateIntervention(
            100, first.State!.EventInstanceId, 1, 18800301, 301000001, matchingId: 10));
        Assert.True(changed.Success);
        Assert.Equal(1, fixture.ItemCount(100, 301000001, matchingId: 20));

        await fixture.Manager.RemoveMatchingStateAsync(10);
        var closed = await fixture.Manager.EnqueueInterventionAsync(CreateIntervention(
            100, first.State.EventInstanceId, 2, 18800301, 301000001, matchingId: 10));
        var other = await fixture.Manager.EnqueueInterventionAsync(CreateIntervention(
            100, second.State!.EventInstanceId, 1, 18800301, 301000001, matchingId: 20));

        Assert.Equal(RoomEventCommandError.MatchingClosed, closed.Error);
        Assert.True(other.Success);
    }

    private static Fixture CreateFixture()
    {
        InitializeData();
        var inventory = new InGameInventoryManager();
        inventory.Initialize();
        var gateway = new FakeActorGateway();
        var time = new MutableTimeProvider(InitialTime);
        var manager = new RoomEventWorldManager(inventory, gateway, time);
        return new Fixture(manager, inventory, gateway, time);
    }

    private static void InitializeData()
    {
        string csvPath = Path.Combine(FindRepositoryRoot(), "network", "Common", "csv");
        GameRoomEventData.Initialize(
            CsvHelper.LoadCsv(Path.Combine(csvPath, "room_event_master.csv")),
            CsvHelper.LoadCsv(Path.Combine(csvPath, "room_event_choice.csv")));
        GameRoomEventResponseItemData.Initialize(
            CsvHelper.LoadCsv(Path.Combine(csvPath, "room_event_response_item.csv")));
    }

    private static RoomEventActivationCommand CreateActivation(long matchingId = 10)
    {
        return new RoomEventActivationCommand(
            matchingId,
            EventId: 188003,
            AreaType.BroadcastRoom,
            InteractId: 701000007,
            RequiredResponseTag: "record");
    }

    private static RoomEventInterventionCommand CreateIntervention(
        long actorPlayerId,
        long eventInstanceId,
        int expectedVersion,
        int choiceId,
        int selectedItemId,
        long matchingId = 10)
    {
        return new RoomEventInterventionCommand(
            matchingId,
            actorPlayerId,
            eventInstanceId,
            expectedVersion,
            choiceId,
            selectedItemId);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "network", "Common", "csv")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output path.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Fixture(
            RoomEventWorldManager manager,
            InGameInventoryManager inventory,
            FakeActorGateway gateway,
            MutableTimeProvider time)
        {
            Manager = manager;
            Inventory = inventory;
            Gateway = gateway;
            Time = time;
        }

        public RoomEventWorldManager Manager { get; }
        public InGameInventoryManager Inventory { get; }
        public FakeActorGateway Gateway { get; }
        public MutableTimeProvider Time { get; }

        public void AddPlayer(long playerId, int itemId, int count, long matchingId = 10)
        {
            Gateway.SetArea(playerId, AreaType.BroadcastRoom, matchingId);
            Inventory.AddItem(matchingId, playerId, itemId, count);
        }

        public int ItemCount(long playerId, int itemId, long matchingId = 10) =>
            Inventory.GetPlayerInventory(matchingId, playerId).GetItemCount(itemId);

        public ValueTask DisposeAsync() => Manager.DisposeAsync();
    }

    private sealed class FakeActorGateway : IRoomEventActorGateway
    {
        private readonly Dictionary<(long MatchingId, long PlayerId), AreaType> _areas = new();

        public void SetArea(long playerId, AreaType areaType, long matchingId = 10)
        {
            _areas[(matchingId, playerId)] = areaType;
        }

        public bool IsPlayerInArea(long matchingId, long playerId, AreaType areaType) =>
            _areas.GetValueOrDefault((matchingId, playerId)) == areaType;

        public bool TryApplyResourceDelta(
            long matchingId,
            long playerId,
            int staminaDelta,
            int mentalDelta) => true;
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public MutableTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration)
        {
            _now = _now.Add(duration);
        }
    }
}
