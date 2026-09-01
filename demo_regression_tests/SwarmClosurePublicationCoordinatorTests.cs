using System.Collections.Concurrent;
using System.Collections.Immutable;
using game_server;
using game_server.services;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class SwarmClosurePublicationCoordinatorTests
{
    [Fact]
    public void ImmutableOutboundPlan_FreezesInventoryAndRecipientOrder()
    {
        var mutableItem = new InGameItemInfo
        {
            ItemUid = 7001,
            ItemId = 107000010,
            Count = 0,
            GiftState = GiftState.Received
        };
        ImmutableArray<int> recipients = [2, 0, 1];
        SwarmInGameItemSnapshot itemSnapshot = SwarmInGameItemSnapshot.Capture(mutableItem);
        var plan = new SwarmClosurePublicationPlan(
            51_001,
            [new SwarmInventoryUpdateOutbound(itemSnapshot, recipients)]);

        mutableItem.ItemUid = 9999;
        mutableItem.ItemId = 1;
        mutableItem.Count = 5;
        mutableItem.GiftState = GiftState.None;

        SwarmInventoryUpdateOutbound outbound =
            Assert.IsType<SwarmInventoryUpdateOutbound>(Assert.Single(plan.Outbound));
        Assert.Equal([2, 0, 1], outbound.RecipientOrdinals.ToArray());
        Assert.Equal(7001, outbound.Item.ItemUid);
        Assert.Equal(107000010, outbound.Item.ItemId);
        Assert.Equal(0, outbound.Item.Count);
        Assert.Equal(GiftState.Received, outbound.Item.GiftState);
        InGameItemInfo restored = outbound.Item.ToModel();
        Assert.Equal(7001, restored.ItemUid);
        Assert.Equal(GiftState.Received, restored.GiftState);
    }

    [Fact]
    public void ImmutableOutboundPlan_PreservesClosureWireOrderAndScalarPayloads()
    {
        ImmutableArray<int> allRecipients = [0, 1];
        var plan = new SwarmClosurePublicationPlan(
            51_010,
            [
                new SwarmFieldStateOutbound(1_001, allRecipients),
                new SwarmClosureWarningOutbound(AreaType.S2Classroom1, 15, 2_002, allRecipients),
                new SwarmAreaClosedOutbound(AreaType.S2Classroom1, allRecipients),
                new SwarmDoorStateOutbound(3003, allRecipients),
                new SwarmInventoryUpdateOutbound(
                    new SwarmInGameItemSnapshot(4004, 107000010, 0, GiftState.None),
                    [1]),
                new SwarmRingVfxOutbound(5005, 1.5f, 2.5f, 3.5f, 1, 5005, 2, [1, 0])
            ]);

        Assert.Collection(
            plan.Outbound,
            field => Assert.Equal(1_001, Assert.IsType<SwarmFieldStateOutbound>(field).StartedAtUnixMs),
            warning =>
            {
                SwarmClosureWarningOutbound value = Assert.IsType<SwarmClosureWarningOutbound>(warning);
                Assert.Equal(AreaType.S2Classroom1, value.Area);
                Assert.Equal(15, value.SecondsRemaining);
                Assert.Equal(2_002, value.ClosureAtUnixMs);
            },
            closed => Assert.Equal(
                AreaType.S2Classroom1,
                Assert.IsType<SwarmAreaClosedOutbound>(closed).Area),
            door => Assert.Equal(3003, Assert.IsType<SwarmDoorStateOutbound>(door).DoorId),
            inventory => Assert.Equal(
                4004,
                Assert.IsType<SwarmInventoryUpdateOutbound>(inventory).Item.ItemUid),
            ring =>
            {
                SwarmRingVfxOutbound value = Assert.IsType<SwarmRingVfxOutbound>(ring);
                Assert.Equal(5005, value.OwnerPlayerId);
                Assert.Equal(1.5f, value.CenterX);
                Assert.Equal(2.5f, value.CenterY);
                Assert.Equal(3.5f, value.Radius);
                Assert.Equal(1, value.Kind);
                Assert.Equal(5005, value.VictimPlayerId);
                Assert.Equal(2, value.FromOrdinal);
                Assert.Equal([1, 0], value.RecipientOrdinals.ToArray());
            });
    }

    [Fact]
    public async Task PublicationTickets_PreserveCommitOrderAndTerminalWaitsForLeases()
    {
        const long matchingId = 51_002;
        var coordinator = new SwarmClosurePublicationCoordinator();
        var registry = new MatchRuntimeRegistry();
        var events = new ConcurrentQueue<string>();
        using var secondAttempted = new ManualResetEventSlim();
        using var firstDispatchStarted = new ManualResetEventSlim();
        using var releaseFirstDispatch = new ManualResetEventSlim();
        SwarmClosurePublicationTicket firstTicket = default;
        SwarmClosurePublicationTicket secondTicket = default;

        IDisposable? firstOperation = registry.TryAcquireOperation(
            matchingId,
            () =>
            {
                events.Enqueue("prepare-warning");
                firstTicket = coordinator.ReservePublication(matchingId);
            });
        IDisposable? secondOperation = registry.TryAcquireOperation(
            matchingId,
            () =>
            {
                events.Enqueue("prepare-closed");
                secondTicket = coordinator.ReservePublication(matchingId);
            });
        Assert.NotNull(firstOperation);
        Assert.NotNull(secondOperation);

        Task secondDispatch = Task.Run(() =>
        {
            events.Enqueue("attempt-closed");
            secondAttempted.Set();
            GameServer.DispatchWithMatchRuntimeLease(
                secondOperation!,
                () => coordinator.DispatchInOrder(
                    secondTicket,
                    () => events.Enqueue("closed-packet")));
        });
        Assert.True(secondAttempted.Wait(TimeSpan.FromSeconds(5)));
        await Task.Delay(50);
        Assert.False(secondDispatch.IsCompleted);

        Task firstDispatch = Task.Run(() => GameServer.DispatchWithMatchRuntimeLease(
            firstOperation!,
            () => coordinator.DispatchInOrder(
                firstTicket,
                () =>
                {
                    events.Enqueue("warning-start");
                    firstDispatchStarted.Set();
                    Assert.True(releaseFirstDispatch.Wait(TimeSpan.FromSeconds(5)));
                    events.Enqueue("warning-packet");
                })));
        Assert.True(firstDispatchStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(registry.TryExecute(
            matchingId,
            () => events.Enqueue("same-match-action")));
        Assert.True(registry.TryFinalize(
            matchingId,
            static () => true,
            beforeFinalized: () => events.Enqueue("terminal-before"),
            cleanup: () =>
            {
                events.Enqueue("component-cleanup");
                coordinator.ClearMatching(matchingId);
            },
            afterFinalized: () => events.Enqueue("terminal-after")));
        Assert.DoesNotContain("terminal-before", events);

        releaseFirstDispatch.Set();
        await Task.WhenAll(firstDispatch, secondDispatch).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            [
                "prepare-warning",
                "prepare-closed",
                "attempt-closed",
                "warning-start",
                "same-match-action",
                "warning-packet",
                "closed-packet",
                "terminal-before",
                "component-cleanup",
                "terminal-after"
            ],
            events.ToArray());
    }

    [Fact]
    public async Task PublicationTickets_FirstFailureStillAdvancesWaitingClosedTurn()
    {
        const long matchingId = 51_003;
        var coordinator = new SwarmClosurePublicationCoordinator();
        SwarmClosurePublicationTicket warning = coordinator.ReservePublication(matchingId);
        SwarmClosurePublicationTicket closed = coordinator.ReservePublication(matchingId);
        using var closedAttempted = new ManualResetEventSlim();
        var events = new ConcurrentQueue<string>();

        Task closedDispatch = Task.Run(() =>
        {
            closedAttempted.Set();
            coordinator.DispatchInOrder(closed, () => events.Enqueue("closed-packet"));
        });
        Assert.True(closedAttempted.Wait(TimeSpan.FromSeconds(5)));
        await Task.Delay(50);
        Assert.False(closedDispatch.IsCompleted);

        var failure = new InvalidOperationException("warning send failed");
        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() =>
            coordinator.DispatchInOrder(warning, () => throw failure));
        Assert.Same(failure, thrown);
        await closedDispatch.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["closed-packet"], events.ToArray());
        coordinator.ClearMatching(matchingId);
    }

    [Fact]
    public async Task ProjectionFailure_StopsRemainingPacketsWithoutRollingBackCommittedStateOrNextTurn()
    {
        const long matchingId = 51_011;
        var coordinator = new SwarmClosurePublicationCoordinator();
        SwarmClosurePublicationTicket first = coordinator.ReservePublication(matchingId);
        SwarmClosurePublicationTicket second = coordinator.ReservePublication(matchingId);
        var authoritative = new List<string>
        {
            "closure-state",
            "door-state",
            "orb-state",
            "closure-log",
            "orb-log"
        };
        var packets = new ConcurrentQueue<string>();
        using var secondAttempted = new ManualResetEventSlim();

        Task nextPlan = Task.Run(() =>
        {
            secondAttempted.Set();
            coordinator.DispatchInOrder(second, () => packets.Enqueue("next-plan"));
        });
        Assert.True(secondAttempted.Wait(TimeSpan.FromSeconds(5)));
        await Task.Delay(50);
        Assert.False(nextPlan.IsCompleted);

        Assert.Throws<InvalidOperationException>(() => coordinator.DispatchInOrder(
            first,
            () =>
            {
                packets.Enqueue("warning");
                throw new InvalidOperationException("first send failed");
#pragma warning disable CS0162
                packets.Enqueue("closed");
#pragma warning restore CS0162
            }));
        await nextPlan.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            ["closure-state", "door-state", "orb-state", "closure-log", "orb-log"],
            authoritative);
        Assert.Equal(["warning", "next-plan"], packets.ToArray());
        coordinator.ClearMatching(matchingId);
    }

    [Fact]
    public async Task PublicationTicket_CopiedToConcurrentCallers_DispatchesAtMostOnce()
    {
        const long matchingId = 51_004;
        var coordinator = new SwarmClosurePublicationCoordinator();
        SwarmClosurePublicationTicket ticket = coordinator.ReservePublication(matchingId);
        using var firstStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var duplicateAttempted = new ManualResetEventSlim();
        int dispatchCount = 0;

        Task first = Task.Run(() => coordinator.DispatchInOrder(
            ticket,
            () =>
            {
                Interlocked.Increment(ref dispatchCount);
                firstStarted.Set();
                Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(5)));
            }));
        Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(5)));

        Task duplicate = Task.Run(() =>
        {
            duplicateAttempted.Set();
            coordinator.DispatchInOrder(
                ticket,
                () => Interlocked.Increment(ref dispatchCount));
        });
        Assert.True(duplicateAttempted.Wait(TimeSpan.FromSeconds(5)));
        await Task.Delay(50);
        Assert.False(duplicate.IsCompleted);

        releaseFirst.Set();
        await Task.WhenAll(first, duplicate).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, dispatchCount);
        coordinator.ClearMatching(matchingId);
    }

    [Fact]
    public async Task PublicationTickets_DifferentMatchesDispatchIndependently()
    {
        var coordinator = new SwarmClosurePublicationCoordinator();
        SwarmClosurePublicationTicket blocked = coordinator.ReservePublication(51_005);
        SwarmClosurePublicationTicket independent = coordinator.ReservePublication(51_006);
        using var blockedStarted = new ManualResetEventSlim();
        using var releaseBlocked = new ManualResetEventSlim();

        Task blockedDispatch = Task.Run(() => coordinator.DispatchInOrder(
            blocked,
            () =>
            {
                blockedStarted.Set();
                Assert.True(releaseBlocked.Wait(TimeSpan.FromSeconds(5)));
            }));
        Assert.True(blockedStarted.Wait(TimeSpan.FromSeconds(5)));

        Task independentDispatch = Task.Run(() => coordinator.DispatchInOrder(
            independent,
            static () => { }));
        await independentDispatch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(blockedDispatch.IsCompleted);

        releaseBlocked.Set();
        await blockedDispatch.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.ClearMatching(51_005);
        coordinator.ClearMatching(51_006);
    }
}
