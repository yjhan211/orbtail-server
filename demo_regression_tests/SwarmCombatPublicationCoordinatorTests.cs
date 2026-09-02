using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using game_server;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;
using network.contracts.scaling;
using network.hosting;
using network.infrastructure;
using network.interfaces;
using network.packets;

namespace demo_regression_tests;

public sealed class SwarmCombatPublicationCoordinatorTests
{
    [Fact]
    public void RealtimeTurn_IsExclusivePerMatch_ButIndependentAcrossMatches()
    {
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(61_001, 61_002);
        using SwarmCombatPublicationCoordinator.PublicationTurn first =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(61_001));

        Assert.Null(coordinator.TryBeginDueRealtimeTurn(61_001));
        using SwarmCombatPublicationCoordinator.PublicationTurn independent =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(61_002));

        first.Dispose();
        using SwarmCombatPublicationCoordinator.PublicationTurn next =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(61_001));
    }

    [Fact]
    public async Task RequiredTurn_WaitsAndPreventsRealtimeSteal()
    {
        const long matchingId = 61_003;
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(matchingId);
        SwarmCombatPublicationCoordinator.PublicationTurn first =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
        using var requiredStarted = new ManualResetEventSlim();

        Task<SwarmCombatPublicationCoordinator.PublicationTurn?> requiredTask = Task.Run(() =>
        {
            requiredStarted.Set();
            return coordinator.BeginRequiredTurn(matchingId);
        });

        Assert.True(requiredStarted.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(SpinWait.SpinUntil(
            () => coordinator.Inspect(matchingId)?.RequiredWaiterCount == 1,
            TimeSpan.FromSeconds(2)));
        Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));

        first.Dispose();
        SwarmCombatPublicationCoordinator.PublicationTurn required =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                await requiredTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
        required.Dispose();

        using SwarmCombatPublicationCoordinator.PublicationTurn nextRealtime =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
    }

    [Fact]
    public async Task RequiredTurn_DoesNotBlockAnotherMatch()
    {
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(61_004, 61_005);
        using SwarmCombatPublicationCoordinator.PublicationTurn blocked =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(61_004));

        Task<SwarmCombatPublicationCoordinator.PublicationTurn?> waiting =
            Task.Run(() => coordinator.BeginRequiredTurn(61_004));
        Assert.True(SpinWait.SpinUntil(
            () => coordinator.Inspect(61_004)?.RequiredWaiterCount == 1,
            TimeSpan.FromSeconds(2)));

        using SwarmCombatPublicationCoordinator.PublicationTurn independent =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.BeginRequiredTurn(61_005));

        blocked.Dispose();
        SwarmCombatPublicationCoordinator.PublicationTurn required =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                await waiting.WaitAsync(TimeSpan.FromSeconds(2)));
        required.Dispose();
    }

    [Fact]
    public async Task RealtimeDue_ConcurrentSameTimestampClaimsExactlyOneTurn()
    {
        const long matchingId = 61_031;
        ulong now = 1_000;
        var coordinator = new SwarmCombatPublicationCoordinator(
            TimeSpan.FromMilliseconds(50), () => now, 1_000);
        Assert.True(coordinator.RegisterMatching(matchingId));
        using var start = new Barrier(16);

        Task<SwarmCombatPublicationCoordinator.PublicationTurn?>[] attempts =
            Enumerable.Range(0, 16)
                .Select(_ => Task.Run(() =>
                {
                    start.SignalAndWait();
                    return coordinator.TryBeginDueRealtimeTurn(matchingId);
                }))
                .ToArray();

        SwarmCombatPublicationCoordinator.PublicationTurn?[] turns =
            await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(2));
        SwarmCombatPublicationCoordinator.PublicationTurn winner = Assert.Single(turns.OfType<SwarmCombatPublicationCoordinator.PublicationTurn>());
        winner.Dispose();
        Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
    }

    [Fact]
    public async Task RealtimeDue_LongOverrunAllowsOneCatchUpAndCoalescesStaleCallers()
    {
        const long matchingId = 61_039;
        ulong now = 0;
        var coordinator = new SwarmCombatPublicationCoordinator(
            TimeSpan.FromMilliseconds(50), () => now, 1_000);
        Assert.True(coordinator.RegisterMatching(matchingId));
        SwarmCombatPublicationCoordinator.PublicationTurn overrun =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));

        now = 1_000;
        overrun.Dispose();
        using var start = new Barrier(16);
        Task<SwarmCombatPublicationCoordinator.PublicationTurn?>[] attempts =
            Enumerable.Range(0, 16)
                .Select(_ => Task.Run(() =>
                {
                    start.SignalAndWait();
                    return coordinator.TryBeginDueRealtimeTurn(matchingId);
                }))
                .ToArray();

        SwarmCombatPublicationCoordinator.PublicationTurn?[] turns =
            await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(2));
        SwarmCombatPublicationCoordinator.PublicationTurn catchUp =
            Assert.Single(turns.OfType<SwarmCombatPublicationCoordinator.PublicationTurn>());
        catchUp.Dispose();
        Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
        now = 1_049;
        Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
        now = 1_050;
        Assert.NotNull(coordinator.TryBeginDueRealtimeTurn(matchingId));
    }

    [Fact]
    public void RealtimeDue_UsesStartBasedFortyNineFiftyBoundaryAndOverrunRebase()
    {
        const long matchingId = 61_032;
        ulong now = 0;
        var coordinator = new SwarmCombatPublicationCoordinator(
            TimeSpan.FromMilliseconds(50), () => now, 1_000);
        Assert.True(coordinator.RegisterMatching(matchingId));

        Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
            coordinator.TryBeginDueRealtimeTurn(matchingId)).Dispose();
        now = 49;
        Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
        now = 50;
        Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
            coordinator.TryBeginDueRealtimeTurn(matchingId)).Dispose();

        now = 1_000;
        Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
            coordinator.TryBeginDueRealtimeTurn(matchingId)).Dispose();
        Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
        now = 1_049;
        Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
        now = 1_050;
        Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
            coordinator.TryBeginDueRealtimeTurn(matchingId)).Dispose();
    }

    [Fact]
    public void RealtimeDue_IsIndependentPerMatch()
    {
        ulong now = 10;
        var coordinator = new SwarmCombatPublicationCoordinator(
            TimeSpan.FromMilliseconds(50), () => now, 1_000);
        Assert.True(coordinator.RegisterMatching(61_033));
        Assert.True(coordinator.RegisterMatching(61_034));

        Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
            coordinator.TryBeginDueRealtimeTurn(61_033)).Dispose();
        Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
            coordinator.TryBeginDueRealtimeTurn(61_034)).Dispose();
        now = 59;
        Assert.Null(coordinator.TryBeginDueRealtimeTurn(61_033));
        Assert.Null(coordinator.TryBeginDueRealtimeTurn(61_034));
        now = 60;
        Assert.NotNull(coordinator.TryBeginDueRealtimeTurn(61_033));
        Assert.NotNull(coordinator.TryBeginDueRealtimeTurn(61_034));
    }

    [Fact]
    public void RequiredTurn_RebasesRealtimeDueAtActualAcquisition()
    {
        const long matchingId = 61_035;
        ulong now = 25;
        var coordinator = new SwarmCombatPublicationCoordinator(
            TimeSpan.FromMilliseconds(50), () => now, 1_000);
        Assert.True(coordinator.RegisterMatching(matchingId));

        Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
            coordinator.BeginRequiredTurn(matchingId)).Dispose();
        now = 74;
        Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
        now = 75;
        Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
            coordinator.TryBeginDueRealtimeTurn(matchingId)).Dispose();
    }

    [Fact]
    public async Task RequiredTurn_WaitingTimeDoesNotStartRealtimeDueBeforeAcquisition()
    {
        const long matchingId = 61_040;
        ulong now = 0;
        var coordinator = new SwarmCombatPublicationCoordinator(
            TimeSpan.FromMilliseconds(50), () => now, 1_000);
        Assert.True(coordinator.RegisterMatching(matchingId));
        SwarmCombatPublicationCoordinator.PublicationTurn realtime =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));

        Task<SwarmCombatPublicationCoordinator.PublicationTurn?> requiredTask =
            Task.Run(() => coordinator.BeginRequiredTurn(matchingId));
        Assert.True(SpinWait.SpinUntil(
            () => coordinator.Inspect(matchingId)?.RequiredWaiterCount == 1,
            TimeSpan.FromSeconds(2)));

        now = 1_000;
        realtime.Dispose();
        SwarmCombatPublicationCoordinator.PublicationTurn required =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                await requiredTask.WaitAsync(TimeSpan.FromSeconds(2)));
        required.Dispose();

        now = 1_049;
        Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
        now = 1_050;
        Assert.NotNull(coordinator.TryBeginDueRealtimeTurn(matchingId));
    }

    [Fact]
    public async Task OrderedTurn_WaitsBehindActiveTurnAndPreventsRealtimeBargeIn()
    {
        const long matchingId = 61_043;
        ulong now = 0;
        var coordinator = new SwarmCombatPublicationCoordinator(
            TimeSpan.FromMilliseconds(50), () => now, 1_000);
        Assert.True(coordinator.RegisterMatching(matchingId));
        SwarmCombatPublicationCoordinator.PublicationTurn realtime =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));

        Task<SwarmCombatPublicationCoordinator.PublicationTurn?> orderedTask =
            Task.Run(() => coordinator.BeginOrderedTurn(matchingId));
        Assert.True(SpinWait.SpinUntil(
            () => coordinator.Inspect(matchingId)?.OrderedWaiterCount == 1,
            TimeSpan.FromSeconds(2)));
        Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));

        realtime.Dispose();
        SwarmCombatPublicationCoordinator.PublicationTurn ordered =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                await orderedTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
        ordered.Dispose();

        now = 49;
        Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
        now = 50;
        Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
            coordinator.TryBeginDueRealtimeTurn(matchingId)).Dispose();
    }

    [Fact]
    public void OrderedTurn_DoesNotReadClockOrRebaseRealtimeDue()
    {
        const long matchingId = 61_044;
        ulong now = 0;
        int clockReads = 0;
        var coordinator = new SwarmCombatPublicationCoordinator(
            TimeSpan.FromMilliseconds(50),
            () =>
            {
                clockReads++;
                return now;
            },
            1_000);
        Assert.True(coordinator.RegisterMatching(matchingId));
        Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
            coordinator.TryBeginDueRealtimeTurn(matchingId)).Dispose();
        Assert.Equal(1, clockReads);

        now = 25;
        Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
            coordinator.BeginOrderedTurn(matchingId)).Dispose();
        Assert.Equal(1, clockReads);

        now = 49;
        Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
        now = 50;
        Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
            coordinator.TryBeginDueRealtimeTurn(matchingId)).Dispose();
        Assert.Equal(3, clockReads);
    }

    [Fact]
    public async Task OrderedTurn_SharesBlockingLaneWithRequiredTurn()
    {
        const long matchingId = 61_049;
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(matchingId);
        SwarmCombatPublicationCoordinator.PublicationTurn required =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.BeginRequiredTurn(matchingId));

        Task<SwarmCombatPublicationCoordinator.PublicationTurn?> orderedTask =
            Task.Run(() => coordinator.BeginOrderedTurn(matchingId));
        Assert.True(SpinWait.SpinUntil(
            () => coordinator.Inspect(matchingId)?.OrderedWaiterCount == 1,
            TimeSpan.FromSeconds(2)));

        required.Dispose();
        using SwarmCombatPublicationCoordinator.PublicationTurn ordered =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                await orderedTask.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task RequiredTurn_IsFifoWithinRequiredLane()
    {
        const long matchingId = 61_051;
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(matchingId);
        SwarmCombatPublicationCoordinator.PublicationTurn? active =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
        SwarmCombatPublicationCoordinator.PublicationTurn? firstTurn = null;
        SwarmCombatPublicationCoordinator.PublicationTurn? secondTurn = null;

        try
        {
            Task<SwarmCombatPublicationCoordinator.PublicationTurn?> firstTask =
                Task.Run(() => coordinator.BeginRequiredTurn(matchingId));
            Assert.True(SpinWait.SpinUntil(
                () => coordinator.Inspect(matchingId)?.RequiredWaiterCount == 1,
                TimeSpan.FromSeconds(2)));

            Task<SwarmCombatPublicationCoordinator.PublicationTurn?> secondTask =
                Task.Run(() => coordinator.BeginRequiredTurn(matchingId));
            Assert.True(SpinWait.SpinUntil(
                () => coordinator.Inspect(matchingId)?.RequiredWaiterCount == 2,
                TimeSpan.FromSeconds(2)));

            active.Dispose();
            active = null;
            firstTurn = Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                await firstTask.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(secondTask.IsCompleted);

            firstTurn.Dispose();
            firstTurn = null;
            secondTurn = Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                await secondTask.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            active?.Dispose();
            firstTurn?.Dispose();
            secondTurn?.Dispose();
            coordinator.ClearMatching(matchingId);
        }
    }

    [Fact]
    public async Task OrderedTurn_IsFifoWithinOrderedLane()
    {
        const long matchingId = 61_052;
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(matchingId);
        SwarmCombatPublicationCoordinator.PublicationTurn? active =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
        SwarmCombatPublicationCoordinator.PublicationTurn? firstTurn = null;
        SwarmCombatPublicationCoordinator.PublicationTurn? secondTurn = null;

        try
        {
            Task<SwarmCombatPublicationCoordinator.PublicationTurn?> firstTask =
                Task.Run(() => coordinator.BeginOrderedTurn(matchingId));
            Assert.True(SpinWait.SpinUntil(
                () => coordinator.Inspect(matchingId)?.OrderedWaiterCount == 1,
                TimeSpan.FromSeconds(2)));

            Task<SwarmCombatPublicationCoordinator.PublicationTurn?> secondTask =
                Task.Run(() => coordinator.BeginOrderedTurn(matchingId));
            Assert.True(SpinWait.SpinUntil(
                () => coordinator.Inspect(matchingId)?.OrderedWaiterCount == 2,
                TimeSpan.FromSeconds(2)));

            active.Dispose();
            active = null;
            firstTurn = Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                await firstTask.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(secondTask.IsCompleted);

            firstTurn.Dispose();
            firstTurn = null;
            secondTurn = Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                await secondTask.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            active?.Dispose();
            firstTurn?.Dispose();
            secondTurn?.Dispose();
            coordinator.ClearMatching(matchingId);
        }
    }

    [Fact]
    public async Task RequiredTurn_QueuedLaterLeapfrogsOrderedLane()
    {
        const long matchingId = 61_053;
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(matchingId);
        SwarmCombatPublicationCoordinator.PublicationTurn? active =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
        SwarmCombatPublicationCoordinator.PublicationTurn? requiredTurn = null;
        SwarmCombatPublicationCoordinator.PublicationTurn? orderedTurn = null;

        try
        {
            Task<SwarmCombatPublicationCoordinator.PublicationTurn?> orderedTask =
                Task.Run(() => coordinator.BeginOrderedTurn(matchingId));
            Assert.True(SpinWait.SpinUntil(
                () => coordinator.Inspect(matchingId)?.OrderedWaiterCount == 1,
                TimeSpan.FromSeconds(2)));

            Task<SwarmCombatPublicationCoordinator.PublicationTurn?> requiredTask =
                Task.Run(() => coordinator.BeginRequiredTurn(matchingId));
            Assert.True(SpinWait.SpinUntil(
                () => coordinator.Inspect(matchingId)?.RequiredWaiterCount == 1,
                TimeSpan.FromSeconds(2)));

            active.Dispose();
            active = null;
            requiredTurn = Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                await requiredTask.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(orderedTask.IsCompleted);

            requiredTurn.Dispose();
            requiredTurn = null;
            orderedTurn = Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                await orderedTask.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            active?.Dispose();
            requiredTurn?.Dispose();
            orderedTurn?.Dispose();
            coordinator.ClearMatching(matchingId);
        }
    }

    [Fact]
    public void DueRealtimeObservation_ArmsDefaultGraceOnceWithoutExtendingVersionOrDeadline()
    {
        const long matchingId = 61_054;
        ulong now = 0;
        var coordinator = new SwarmCombatPublicationCoordinator(
            TimeSpan.FromMilliseconds(50), () => now, 1_000);
        Assert.True(coordinator.RegisterMatching(matchingId));
        SwarmCombatPublicationCoordinator.PublicationTurn? active = null;

        try
        {
            active = Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
            now = 50;
            Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
            SwarmCombatPublicationCoordinator.PublicationDiagnostics first =
                Assert.IsType<SwarmCombatPublicationCoordinator.PublicationDiagnostics>(
                    coordinator.Inspect(matchingId));
            Assert.True(first.HasPendingRealtimeDemand);
            Assert.Equal(1, first.RealtimeDemandVersion);
            Assert.Equal((ulong)150, first.RealtimeDemandExpiresAt);

            now = 75;
            Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
            now = 250;
            Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
            SwarmCombatPublicationCoordinator.PublicationDiagnostics repeated =
                Assert.IsType<SwarmCombatPublicationCoordinator.PublicationDiagnostics>(
                    coordinator.Inspect(matchingId));
            Assert.True(repeated.HasPendingRealtimeDemand);
            Assert.Equal(first.RealtimeDemandVersion, repeated.RealtimeDemandVersion);
            Assert.Equal(first.RealtimeDemandExpiresAt, repeated.RealtimeDemandExpiresAt);
        }
        finally
        {
            active?.Dispose();
            coordinator.ClearMatching(matchingId);
        }
    }

    [Fact]
    public void DefaultHandoffGrace_CeilsTwiceTheDurationInsteadOfDoublingIntervalTicks()
    {
        const long matchingId = 61_065;
        ulong now = 0;
        var coordinator = new SwarmCombatPublicationCoordinator(
            TimeSpan.FromMilliseconds(50), () => now, 1_001);
        Assert.True(coordinator.RegisterMatching(matchingId));
        SwarmCombatPublicationCoordinator.PublicationTurn? active = null;

        try
        {
            active = Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
            now = 51;
            Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));

            SwarmCombatPublicationCoordinator.PublicationDiagnostics pending =
                Assert.IsType<SwarmCombatPublicationCoordinator.PublicationDiagnostics>(
                    coordinator.Inspect(matchingId));
            Assert.True(pending.HasPendingRealtimeDemand);
            Assert.Equal((ulong)152, pending.RealtimeDemandExpiresAt);
            Assert.NotEqual((ulong)153, pending.RealtimeDemandExpiresAt);
        }
        finally
        {
            active?.Dispose();
            coordinator.ClearMatching(matchingId);
        }
    }

    [Fact]
    public async Task DueRealtimeTurn_ClaimsAheadOfQueuedOrderedTurnBeforeGraceExpires()
    {
        const long matchingId = 61_055;
        ulong now = 0;
        var coordinator = new SwarmCombatPublicationCoordinator(
            TimeSpan.FromMilliseconds(50),
            () => now,
            1_000,
            TimeSpan.FromSeconds(5));
        Assert.True(coordinator.RegisterMatching(matchingId));
        SwarmCombatPublicationCoordinator.PublicationTurn? active = null;
        SwarmCombatPublicationCoordinator.PublicationTurn? realtimeTurn = null;
        SwarmCombatPublicationCoordinator.PublicationTurn? orderedTurn = null;

        try
        {
            active = Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
            Task<SwarmCombatPublicationCoordinator.PublicationTurn?> orderedTask =
                Task.Run(() => coordinator.BeginOrderedTurn(matchingId));
            Assert.True(SpinWait.SpinUntil(
                () => coordinator.Inspect(matchingId)?.OrderedWaiterCount == 1,
                TimeSpan.FromSeconds(2)));

            now = 50;
            Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
            Assert.True(coordinator.Inspect(matchingId)?.HasPendingRealtimeDemand);
            active.Dispose();
            active = null;

            realtimeTurn = Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
            Assert.False(orderedTask.IsCompleted);
            realtimeTurn.Dispose();
            realtimeTurn = null;
            orderedTurn = Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                await orderedTask.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            active?.Dispose();
            realtimeTurn?.Dispose();
            orderedTurn?.Dispose();
            coordinator.ClearMatching(matchingId);
        }
    }

    [Fact]
    public async Task DueRealtimeTurn_MakesBoundedProgressAgainstContinuousOrderedProducers()
    {
        const long matchingId = 61_066;
        const int producerCount = 4;
        long timestamp = 0;
        int stopProducers = 0;
        int orderedAcquisitions = 0;
        TimeSpan handoffGrace = TimeSpan.FromSeconds(2);
        var coordinator = new SwarmCombatPublicationCoordinator(
            TimeSpan.FromMilliseconds(50),
            () => unchecked((ulong)Interlocked.Read(ref timestamp)),
            1_000,
            handoffGrace);
        Assert.True(coordinator.RegisterMatching(matchingId));
        Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
            coordinator.TryBeginDueRealtimeTurn(matchingId)).Dispose();
        using var holderActive = new ManualResetEventSlim();
        using var releaseHolder = new ManualResetEventSlim();
        Task[] producers = [];
        SwarmCombatPublicationCoordinator.PublicationTurn? realtimeTurn = null;

        try
        {
            Task holder = Task.Run(() =>
            {
                using (SwarmCombatPublicationCoordinator.PublicationTurn? first =
                       coordinator.BeginOrderedTurn(matchingId))
                {
                    if (first == null)
                        return;

                    Interlocked.Increment(ref orderedAcquisitions);
                    holderActive.Set();
                    releaseHolder.Wait();
                }

                while (Volatile.Read(ref stopProducers) == 0)
                {
                    using SwarmCombatPublicationCoordinator.PublicationTurn? ordered =
                        coordinator.BeginOrderedTurn(matchingId);
                    if (ordered == null)
                        break;
                    Interlocked.Increment(ref orderedAcquisitions);
                    Thread.Yield();
                }
            });
            Assert.True(holderActive.Wait(TimeSpan.FromSeconds(2)));

            Task[] followers = Enumerable.Range(0, producerCount - 1)
                .Select(_ => Task.Run(() =>
                {
                    while (Volatile.Read(ref stopProducers) == 0)
                    {
                        using SwarmCombatPublicationCoordinator.PublicationTurn? ordered =
                            coordinator.BeginOrderedTurn(matchingId);
                        if (ordered == null)
                            break;
                        Interlocked.Increment(ref orderedAcquisitions);
                        Thread.Yield();
                    }
                }))
                .ToArray();
            producers = [holder, .. followers];
            Assert.True(SpinWait.SpinUntil(
                () => coordinator.Inspect(matchingId)?.OrderedWaiterCount ==
                      producerCount - 1,
                TimeSpan.FromSeconds(2)));

            Interlocked.Exchange(ref timestamp, 50);
            Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
            Assert.True(coordinator.Inspect(matchingId)?.HasPendingRealtimeDemand);

            var elapsed = Stopwatch.StartNew();
            releaseHolder.Set();
            Assert.True(SpinWait.SpinUntil(
                () => coordinator.Inspect(matchingId)?.OrderedWaiterCount == producerCount,
                TimeSpan.FromSeconds(1)));
            while (realtimeTurn == null && elapsed.Elapsed < handoffGrace)
            {
                realtimeTurn = coordinator.TryBeginDueRealtimeTurn(matchingId);
                if (realtimeTurn == null)
                    await Task.Yield();
            }

            Assert.NotNull(realtimeTurn);
            Assert.True(elapsed.Elapsed < handoffGrace);
            Assert.True(Volatile.Read(ref orderedAcquisitions) >= 1);
            Assert.All(producers, producer => Assert.False(producer.IsCompleted));
        }
        finally
        {
            Volatile.Write(ref stopProducers, 1);
            releaseHolder.Set();
            realtimeTurn?.Dispose();
            coordinator.ClearMatching(matchingId);
            if (producers.Length > 0)
                await Task.WhenAll(producers).WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task DueRealtimeHandoff_GraceStartsWhenDemandIsArmed()
    {
        const long matchingId = 61_056;
        long timestamp = 0;
        TimeSpan handoffGrace = TimeSpan.FromMilliseconds(400);
        var coordinator = new SwarmCombatPublicationCoordinator(
            TimeSpan.FromMilliseconds(50),
            () => unchecked((ulong)Interlocked.Read(ref timestamp)),
            1_000,
            handoffGrace);
        Assert.True(coordinator.RegisterMatching(matchingId));
        SwarmCombatPublicationCoordinator.PublicationTurn? active =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
        SwarmCombatPublicationCoordinator.PublicationTurn? orderedTurn = null;

        try
        {
            Interlocked.Exchange(ref timestamp, 50);
            Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
            Assert.True(coordinator.Inspect(matchingId)?.HasPendingRealtimeDemand);
            await Task.Delay(handoffGrace + TimeSpan.FromMilliseconds(150));

            Task<SwarmCombatPublicationCoordinator.PublicationTurn?> orderedTask =
                Task.Run(() => coordinator.BeginOrderedTurn(matchingId));
            Assert.True(SpinWait.SpinUntil(
                () => coordinator.Inspect(matchingId)?.OrderedWaiterCount == 1,
                TimeSpan.FromSeconds(2)));

            var releaseElapsed = Stopwatch.StartNew();
            active.Dispose();
            active = null;
            orderedTurn = Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                await orderedTask.WaitAsync(TimeSpan.FromSeconds(2)));
            releaseElapsed.Stop();

            Assert.True(
                releaseElapsed.Elapsed < TimeSpan.FromMilliseconds(300),
                $"Expired handoff took {releaseElapsed.Elapsed.TotalMilliseconds:F1} ms " +
                $"after active retirement instead of falling back immediately.");
            Assert.False(coordinator.Inspect(matchingId)?.HasPendingRealtimeDemand);
        }
        finally
        {
            active?.Dispose();
            orderedTurn?.Dispose();
            coordinator.ClearMatching(matchingId);
        }
    }

    [Fact]
    public async Task ExpiredRealtimeHandoff_AtomicallyFallsBackToOrderedBeforeFreshRealtime()
    {
        const long matchingId = 61_057;
        ulong now = 0;
        var coordinator = new SwarmCombatPublicationCoordinator(
            TimeSpan.FromMilliseconds(50),
            () => now,
            1_000,
            TimeSpan.FromSeconds(5));
        Assert.True(coordinator.RegisterMatching(matchingId));
        SwarmCombatPublicationCoordinator.PublicationTurn? active = null;
        SwarmCombatPublicationCoordinator.PublicationTurn? orderedTurn = null;
        SwarmCombatPublicationCoordinator.PublicationTurn? freshRealtime = null;

        try
        {
            active = Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
            Task<SwarmCombatPublicationCoordinator.PublicationTurn?> orderedTask =
                Task.Run(() => coordinator.BeginOrderedTurn(matchingId));
            Assert.True(SpinWait.SpinUntil(
                () => coordinator.Inspect(matchingId)?.OrderedWaiterCount == 1,
                TimeSpan.FromSeconds(2)));

            now = 50;
            Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
            now = 5_050;
            active.Dispose();
            active = null;

            freshRealtime = coordinator.TryBeginDueRealtimeTurn(matchingId);
            Assert.Null(freshRealtime);
            orderedTurn = Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                await orderedTask.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            active?.Dispose();
            freshRealtime?.Dispose();
            orderedTurn?.Dispose();
            coordinator.ClearMatching(matchingId);
        }
    }

    [Fact]
    public async Task PendingRealtimeClockFailure_FailsOpenToOrderedWithoutRebasingDue()
    {
        const long matchingId = 61_058;
        ulong now = 0;
        int failClock = 0;
        var coordinator = new SwarmCombatPublicationCoordinator(
            TimeSpan.FromMilliseconds(50),
            () =>
            {
                if (Volatile.Read(ref failClock) != 0)
                    throw new InvalidOperationException("pending clock failed");
                return now;
            },
            1_000,
            TimeSpan.FromSeconds(5));
        Assert.True(coordinator.RegisterMatching(matchingId));
        SwarmCombatPublicationCoordinator.PublicationTurn? active = null;
        SwarmCombatPublicationCoordinator.PublicationTurn? orderedTurn = null;
        SwarmCombatPublicationCoordinator.PublicationTurn? realtimeTurn = null;

        try
        {
            active = Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
            now = 50;
            Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
            Task<SwarmCombatPublicationCoordinator.PublicationTurn?> orderedTask =
                Task.Run(() => coordinator.BeginOrderedTurn(matchingId));
            Assert.True(SpinWait.SpinUntil(
                () => coordinator.Inspect(matchingId)?.OrderedWaiterCount == 1,
                TimeSpan.FromSeconds(2)));

            Volatile.Write(ref failClock, 1);
            active.Dispose();
            active = null;
            orderedTurn = Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                await orderedTask.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(coordinator.Inspect(matchingId)?.HasPendingRealtimeDemand);

            Volatile.Write(ref failClock, 0);
            orderedTurn.Dispose();
            orderedTurn = null;
            now = 49;
            Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
            now = 50;
            realtimeTurn = Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
        }
        finally
        {
            Volatile.Write(ref failClock, 0);
            active?.Dispose();
            orderedTurn?.Dispose();
            realtimeTurn?.Dispose();
            coordinator.ClearMatching(matchingId);
        }
    }

    [Fact]
    public async Task RequiredClockFailure_RemovesFifoHeadAndLetsNextRequiredTurnProceed()
    {
        const long matchingId = 61_059;
        ulong now = 0;
        int failNextClock = 0;
        var coordinator = new SwarmCombatPublicationCoordinator(
            TimeSpan.FromMilliseconds(50),
            () =>
            {
                if (Interlocked.Exchange(ref failNextClock, 0) != 0)
                    throw new InvalidOperationException("required clock failed");
                return now;
            },
            1_000);
        Assert.True(coordinator.RegisterMatching(matchingId));
        SwarmCombatPublicationCoordinator.PublicationTurn? active =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
        SwarmCombatPublicationCoordinator.PublicationTurn? secondTurn = null;

        try
        {
            Task<SwarmCombatPublicationCoordinator.PublicationTurn?> firstTask =
                Task.Run(() => coordinator.BeginRequiredTurn(matchingId));
            Assert.True(SpinWait.SpinUntil(
                () => coordinator.Inspect(matchingId)?.RequiredWaiterCount == 1,
                TimeSpan.FromSeconds(2)));
            Task<SwarmCombatPublicationCoordinator.PublicationTurn?> secondTask =
                Task.Run(() => coordinator.BeginRequiredTurn(matchingId));
            Assert.True(SpinWait.SpinUntil(
                () => coordinator.Inspect(matchingId)?.RequiredWaiterCount == 2,
                TimeSpan.FromSeconds(2)));

            now = 100;
            Volatile.Write(ref failNextClock, 1);
            active.Dispose();
            active = null;

            InvalidOperationException failure =
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                {
                    await firstTask.WaitAsync(TimeSpan.FromSeconds(2));
                });
            Assert.Equal("required clock failed", failure.Message);
            secondTurn = Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                await secondTask.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(0, coordinator.Inspect(matchingId)?.RequiredWaiterCount);
        }
        finally
        {
            active?.Dispose();
            secondTurn?.Dispose();
            coordinator.ClearMatching(matchingId);
        }
    }

    [Fact]
    public async Task RequiredAcquisition_ClearsPendingRealtimeDemandAndRebasesDue()
    {
        const long matchingId = 61_060;
        ulong now = 0;
        var coordinator = new SwarmCombatPublicationCoordinator(
            TimeSpan.FromMilliseconds(50),
            () => now,
            1_000,
            TimeSpan.FromSeconds(5));
        Assert.True(coordinator.RegisterMatching(matchingId));
        SwarmCombatPublicationCoordinator.PublicationTurn? active = null;
        SwarmCombatPublicationCoordinator.PublicationTurn? requiredTurn = null;
        SwarmCombatPublicationCoordinator.PublicationTurn? realtimeTurn = null;

        try
        {
            active = Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
            now = 50;
            Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
            Assert.True(coordinator.Inspect(matchingId)?.HasPendingRealtimeDemand);

            Task<SwarmCombatPublicationCoordinator.PublicationTurn?> requiredTask =
                Task.Run(() => coordinator.BeginRequiredTurn(matchingId));
            Assert.True(SpinWait.SpinUntil(
                () => coordinator.Inspect(matchingId)?.RequiredWaiterCount == 1,
                TimeSpan.FromSeconds(2)));
            now = 70;
            active.Dispose();
            active = null;

            requiredTurn = Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                await requiredTask.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(coordinator.Inspect(matchingId)?.HasPendingRealtimeDemand);
            requiredTurn.Dispose();
            requiredTurn = null;

            now = 119;
            Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
            now = 120;
            realtimeTurn = Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
        }
        finally
        {
            active?.Dispose();
            requiredTurn?.Dispose();
            realtimeTurn?.Dispose();
            coordinator.ClearMatching(matchingId);
        }
    }

    [Fact]
    public void DueButUnobservedRealtime_DoesNotDelayOrderedOrReadClock()
    {
        const long matchingId = 61_061;
        ulong now = 0;
        int clockReads = 0;
        var coordinator = new SwarmCombatPublicationCoordinator(
            TimeSpan.FromMilliseconds(50),
            () =>
            {
                clockReads++;
                return now;
            },
            1_000);
        Assert.True(coordinator.RegisterMatching(matchingId));
        SwarmCombatPublicationCoordinator.PublicationTurn? realtimeTurn = null;
        SwarmCombatPublicationCoordinator.PublicationTurn? orderedTurn = null;

        try
        {
            realtimeTurn = Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
            realtimeTurn.Dispose();
            realtimeTurn = null;
            Assert.Equal(1, clockReads);

            now = 50;
            orderedTurn = Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.BeginOrderedTurn(matchingId));
            Assert.Equal(1, clockReads);
            Assert.False(coordinator.Inspect(matchingId)?.HasPendingRealtimeDemand);
            orderedTurn.Dispose();
            orderedTurn = null;

            realtimeTurn = Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
            Assert.Equal(2, clockReads);
        }
        finally
        {
            realtimeTurn?.Dispose();
            orderedTurn?.Dispose();
            coordinator.ClearMatching(matchingId);
        }
    }

    [Fact]
    public async Task ClearMatching_WakesDetachedRequiredAndOrderedQueues_AndReregistersFresh()
    {
        const long matchingId = 61_062;
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(matchingId);
        SwarmCombatPublicationCoordinator.PublicationTurn? stale =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
        SwarmCombatPublicationCoordinator.PublicationTurn? fresh = null;

        try
        {
            Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
            Assert.True(coordinator.Inspect(matchingId)?.HasPendingRealtimeDemand);
            Task<SwarmCombatPublicationCoordinator.PublicationTurn?> orderedTask =
                Task.Run(() => coordinator.BeginOrderedTurn(matchingId));
            Assert.True(SpinWait.SpinUntil(
                () => coordinator.Inspect(matchingId)?.OrderedWaiterCount == 1,
                TimeSpan.FromSeconds(2)));
            Task<SwarmCombatPublicationCoordinator.PublicationTurn?> requiredTask =
                Task.Run(() => coordinator.BeginRequiredTurn(matchingId));
            Assert.True(SpinWait.SpinUntil(
                () => coordinator.Inspect(matchingId)?.RequiredWaiterCount == 1,
                TimeSpan.FromSeconds(2)));

            coordinator.ClearMatching(matchingId);
            Assert.Null(await requiredTask.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Null(await orderedTask.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Null(coordinator.Inspect(matchingId));

            stale.Dispose();
            stale = null;
            Assert.True(coordinator.RegisterMatching(matchingId));
            fresh = Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
        }
        finally
        {
            stale?.Dispose();
            fresh?.Dispose();
            coordinator.ClearMatching(matchingId);
        }
    }

    [Fact]
    public async Task PendingRealtimeExpiry_HandlesUint64WrapAndDoesNotBlockOtherMatch()
    {
        const long matchingId = 61_063;
        const long otherMatchingId = 61_064;
        ulong now = ulong.MaxValue - 100;
        var coordinator = new SwarmCombatPublicationCoordinator(
            TimeSpan.FromMilliseconds(50),
            () => now,
            1_000,
            TimeSpan.FromSeconds(1));
        Assert.True(coordinator.RegisterMatching(matchingId));
        Assert.True(coordinator.RegisterMatching(otherMatchingId));
        SwarmCombatPublicationCoordinator.PublicationTurn? active = null;
        SwarmCombatPublicationCoordinator.PublicationTurn? orderedTurn = null;
        SwarmCombatPublicationCoordinator.PublicationTurn? independentTurn = null;

        try
        {
            active = Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
            now = ulong.MaxValue - 50;
            Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
            SwarmCombatPublicationCoordinator.PublicationDiagnostics pending =
                Assert.IsType<SwarmCombatPublicationCoordinator.PublicationDiagnostics>(
                    coordinator.Inspect(matchingId));
            Assert.True(pending.HasPendingRealtimeDemand);
            Assert.Equal((ulong)949, pending.RealtimeDemandExpiresAt);

            Task<SwarmCombatPublicationCoordinator.PublicationTurn?> orderedTask =
                Task.Run(() => coordinator.BeginOrderedTurn(matchingId));
            Assert.True(SpinWait.SpinUntil(
                () => coordinator.Inspect(matchingId)?.OrderedWaiterCount == 1,
                TimeSpan.FromSeconds(2)));
            independentTurn = Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.BeginOrderedTurn(otherMatchingId));

            now = 949;
            active.Dispose();
            active = null;
            Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
            orderedTurn = Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                await orderedTask.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            active?.Dispose();
            orderedTurn?.Dispose();
            independentTurn?.Dispose();
            coordinator.ClearMatching(matchingId);
            coordinator.ClearMatching(otherMatchingId);
        }
    }

    [Fact]
    public void OrderedCountdownLease_DefersTerminalUntilDispatchTurnAndLeaseRetire()
    {
        const long matchingId = 61_050;
        var events = new List<string>();
        SwarmCombatPublicationCoordinator coordinator = CreateUnregisteredCoordinator();
        var registry = new MatchRuntimeRegistry();
        registry.SetRuntimeInitializer(id => Assert.True(coordinator.RegisterMatching(id)));
        Assert.True(registry.TryExecute(matchingId, static () => { }));
        SwarmCombatPublicationCoordinator.PublicationTurn turn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.BeginOrderedTurn(matchingId));
        SwarmCombatPublicationCoordinator.PublicationPlan? plan = null;
        IDisposable operation = Assert.IsAssignableFrom<IDisposable>(
            registry.TryAcquireOperation(
                matchingId,
                () =>
                {
                    Assert.True(
                        coordinator.TryCommitPeriodicCountdownPublication(turn, 2));
                    using SwarmCombatPublicationCoordinator.CaptureScope capture =
                        coordinator.BeginCapture(turn);
                    coordinator.AppendDeferredStep(() => events.Add("countdown"));
                    plan = capture.Freeze();
                }));

        Assert.True(registry.TryFinalize(
            matchingId,
            static () => true,
            beforeFinalized: () => events.Add("terminal"),
            cleanup: () =>
            {
                events.Add("cleanup");
                coordinator.ClearMatching(matchingId);
            },
            afterFinalized: () => events.Add("lifecycle")));

        coordinator.DispatchAndRetire(
            turn,
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationPlan>(plan));
        turn.Dispose();
        Assert.Equal(["countdown"], events);
        Assert.NotNull(coordinator.Inspect(matchingId));

        operation.Dispose();
        Assert.Equal(["countdown", "terminal", "cleanup", "lifecycle"], events);
        Assert.Null(coordinator.Inspect(matchingId));
    }

    [Fact]
    public void PeriodicCountdownCommit_CapturesPacketForEveryFrozenRecipient()
    {
        const long matchingId = 61_045;
        const int remainingSeconds = 4;
        const long serverUnixMs = 1_777_000_123_456;
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(matchingId);
        Assert.True(coordinator.NeedsPeriodicCountdownPublication(matchingId, remainingSeconds));
        using SwarmCombatPublicationCoordinator.PublicationTurn turn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.BeginOrderedTurn(matchingId));
        Assert.True(coordinator.TryCommitPeriodicCountdownPublication(turn, remainingSeconds));
        Assert.False(coordinator.TryCommitPeriodicCountdownPublication(turn, remainingSeconds));
        var delivered = new List<(string Recipient, byte[] WireBytes)>();
        var first = new SwarmCombatPublicationCoordinator.PacketRecipient(
            packet => delivered.Add(("first", packet.ToBytes())));
        var second = new SwarmCombatPublicationCoordinator.PacketRecipient(
            packet => delivered.Add(("second", packet.ToBytes())));
        using SwarmCombatPublicationCoordinator.CaptureScope capture = coordinator.BeginCapture(turn);
        byte[] expectedBody = MessagePackSerializer.Serialize(new G_TO_C_MATCH_START_COUNTDOWN
        {
            MatchingId = matchingId,
            RemainingSeconds = remainingSeconds,
            ServerUnixMs = serverUnixMs
        });
        using (var packet = Packet.Create((int)Protocol.G_TO_C_MATCH_START_COUNTDOWN))
        {
            packet.SetBody(expectedBody);
            Assert.True(coordinator.TryCapturePacket(first, packet));
            Assert.True(coordinator.TryCapturePacket(second, packet));
        }

        SwarmCombatPublicationCoordinator.PublicationPlan plan = capture.Freeze();
        Assert.Equal(2, plan.Count);
        coordinator.DispatchAndRetire(turn, plan);

        Assert.Equal(["first", "second"], delivered.Select(entry => entry.Recipient));
        foreach ((string _, byte[] wireBytes) in delivered)
        {
            Assert.Equal(
                (int)Protocol.G_TO_C_MATCH_START_COUNTDOWN,
                BitConverter.ToInt32(wireBytes, Config.HEADER_SIZE));
            Assert.Equal(
                expectedBody,
                wireBytes[(Config.HEADER_SIZE + sizeof(int) + sizeof(long))..]);
        }

        Assert.False(
            coordinator.NeedsPeriodicCountdownPublication(matchingId, remainingSeconds));
        Assert.True(coordinator.NeedsPeriodicCountdownPublication(matchingId, remainingSeconds - 1));
    }

    [Fact]
    public void PeriodicCountdownCommit_NoRecipientsStillDedupesSecond()
    {
        const long matchingId = 61_046;
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(matchingId);
        using SwarmCombatPublicationCoordinator.PublicationTurn turn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.BeginOrderedTurn(matchingId));
        Assert.True(coordinator.TryCommitPeriodicCountdownPublication(turn, -1));
        using SwarmCombatPublicationCoordinator.CaptureScope capture = coordinator.BeginCapture(turn);
        SwarmCombatPublicationCoordinator.PublicationPlan plan = capture.Freeze();

        Assert.Equal(0, plan.Count);
        coordinator.DispatchAndRetire(turn, plan);
        Assert.False(coordinator.NeedsPeriodicCountdownPublication(matchingId, -1));
    }

    [Fact]
    public void PeriodicCountdownFailure_DoesNotRetryCommittedSecondAndRetiresTurn()
    {
        const long matchingId = 61_047;
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(matchingId);
        using SwarmCombatPublicationCoordinator.PublicationTurn turn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.BeginOrderedTurn(matchingId));
        Assert.True(coordinator.TryCommitPeriodicCountdownPublication(turn, 3));
        int laterRecipients = 0;
        using SwarmCombatPublicationCoordinator.CaptureScope capture = coordinator.BeginCapture(turn);
        using (var packet = Packet.Create((int)Protocol.G_TO_C_MATCH_START_COUNTDOWN))
        {
            packet.SetBody([1, 2, 3]);
            Assert.True(coordinator.TryCapturePacket(
                new SwarmCombatPublicationCoordinator.PacketRecipient(
                    static _ => throw new InvalidOperationException("countdown send failed")),
                packet));
            Assert.True(coordinator.TryCapturePacket(
                new SwarmCombatPublicationCoordinator.PacketRecipient(
                    _ => laterRecipients++),
                packet));
        }

        SwarmCombatPublicationCoordinator.PublicationPlan plan = capture.Freeze();
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => coordinator.DispatchAndRetire(turn, plan));

        Assert.Equal("countdown send failed", failure.Message);
        Assert.Equal(0, laterRecipients);
        Assert.False(coordinator.NeedsPeriodicCountdownPublication(matchingId, 3));
        Assert.False(coordinator.Inspect(matchingId)?.HasActiveTurn);
        using SwarmCombatPublicationCoordinator.PublicationTurn next =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.BeginOrderedTurn(matchingId));
        Assert.False(coordinator.TryCommitPeriodicCountdownPublication(next, 3));
    }

    [Fact]
    public void PeriodicCountdownCommit_ClearAndReregisterStartsFresh()
    {
        const long matchingId = 61_048;
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(matchingId);
        using (SwarmCombatPublicationCoordinator.PublicationTurn turn =
               Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                   coordinator.BeginOrderedTurn(matchingId)))
        {
            Assert.True(coordinator.TryCommitPeriodicCountdownPublication(turn, 5));
        }

        Assert.False(coordinator.NeedsPeriodicCountdownPublication(matchingId, 5));
        coordinator.ClearMatching(matchingId);
        Assert.False(coordinator.NeedsPeriodicCountdownPublication(matchingId, 5));
        Assert.True(coordinator.RegisterMatching(matchingId));
        Assert.True(coordinator.NeedsPeriodicCountdownPublication(matchingId, 5));
    }

    [Fact]
    public void RealtimeFailure_ConsumesDueAndRetireDoesNotReadClock()
    {
        const long matchingId = 61_036;
        ulong now = 100;
        int clockReads = 0;
        var coordinator = new SwarmCombatPublicationCoordinator(
            TimeSpan.FromMilliseconds(50),
            () =>
            {
                clockReads++;
                return now;
            },
            1_000);
        Assert.True(coordinator.RegisterMatching(matchingId));
        SwarmCombatPublicationCoordinator.PublicationTurn turn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
        using SwarmCombatPublicationCoordinator.CaptureScope capture = coordinator.BeginCapture(turn);
        coordinator.AppendDeferredStep(() => throw new InvalidOperationException("send failed"));
        SwarmCombatPublicationCoordinator.PublicationPlan plan = capture.Freeze();

        Assert.Throws<InvalidOperationException>(() => coordinator.DispatchAndRetire(turn, plan));
        Assert.Equal(1, clockReads);
        turn.Dispose();
        Assert.Equal(1, clockReads);
        now = 149;
        Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
        now = 150;
        Assert.NotNull(coordinator.TryBeginDueRealtimeTurn(matchingId));
    }

    [Fact]
    public void RealtimeDue_HandlesUint64WrapAndCeilsTimestampInterval()
    {
        const long matchingId = 61_037;
        ulong now = ulong.MaxValue;
        var coordinator = new SwarmCombatPublicationCoordinator(
            TimeSpan.FromMilliseconds(50), () => now, 1_001);
        Assert.True(coordinator.RegisterMatching(matchingId));
        Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
            coordinator.TryBeginDueRealtimeTurn(matchingId)).Dispose();

        now = 49;
        Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
        now = 50;
        Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
            coordinator.TryBeginDueRealtimeTurn(matchingId)).Dispose();
    }

    [Fact]
    public void Constructor_RejectsInvalidIntervalFrequencyAndHalfRange()
    {
        Func<ulong> clock = static () => 0;

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SwarmCombatPublicationCoordinator(TimeSpan.Zero, clock, 1_000));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SwarmCombatPublicationCoordinator(TimeSpan.FromMilliseconds(50), clock, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SwarmCombatPublicationCoordinator(TimeSpan.MaxValue, clock, ulong.MaxValue));
    }

    [Fact]
    public void ClearAndReregister_StartsWithFreshImmediateDueState()
    {
        const long matchingId = 61_038;
        ulong now = 0;
        var coordinator = new SwarmCombatPublicationCoordinator(
            TimeSpan.FromMilliseconds(50), () => now, 1_000);
        Assert.True(coordinator.RegisterMatching(matchingId));
        SwarmCombatPublicationCoordinator.PublicationTurn stale =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));

        coordinator.ClearMatching(matchingId);
        Assert.True(coordinator.RegisterMatching(matchingId));
        SwarmCombatPublicationCoordinator.PublicationTurn fresh =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));

        stale.Dispose();
        Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
        fresh.Dispose();
        Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
        now = 50;
        Assert.NotNull(coordinator.TryBeginDueRealtimeTurn(matchingId));
    }

    [Fact]
    public void Capture_DeepCopiesRecordedWireBytes_AndPreservesMixedStepOrder()
    {
        int bodyOffset = Config.HEADER_SIZE + sizeof(int) + sizeof(long);
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(61_006);
        using SwarmCombatPublicationCoordinator.PublicationTurn turn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(61_006));
        var events = new List<string>();
        var sent = new List<byte[]>();
        var recipient = new SwarmCombatPublicationCoordinator.PacketRecipient(packet =>
        {
            events.Add("packet");
            sent.Add(packet.ToBytes());
        });
        using SwarmCombatPublicationCoordinator.CaptureScope capture = coordinator.BeginCapture(turn);

        byte[] expectedFirst;
        using (var first = Packet.Create((int)Protocol.G_TO_C_HEART_BEAT, 7001))
        {
            first.SetBody([1, 2, 3]);
            Assert.True(coordinator.TryCapturePacket(recipient, first));
            expectedFirst = first.ToBytes();
            first.Buffer[bodyOffset] = 99;
        }

        coordinator.AppendDeferredStep(() => events.Add("deferred"));

        byte[] expectedSecond;
        using (var second = Packet.Create((int)Protocol.G_TO_C_MATCH_START_COUNTDOWN, 7002))
        {
            second.SetBody([4, 5]);
            Assert.True(coordinator.TryCapturePacket(recipient, second));
            expectedSecond = second.ToBytes();
            second.Buffer[bodyOffset] = 88;
        }

        SwarmCombatPublicationCoordinator.PublicationPlan plan = capture.Freeze();
        Assert.Equal(3, plan.Count);
        coordinator.DispatchAndRetire(turn, plan);

        Assert.Equal(["packet", "deferred", "packet"], events);
        Assert.Equal(2, sent.Count);
        Assert.Equal(expectedFirst, sent[0]);
        Assert.Equal(expectedSecond, sent[1]);
    }

    [Fact]
    public async Task FrozenCapture_RejectsLateInheritedExecutionContextWrites()
    {
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(61_007);
        using SwarmCombatPublicationCoordinator.PublicationTurn turn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(61_007));
        var recipient = new SwarmCombatPublicationCoordinator.PacketRecipient(static _ => { });
        using SwarmCombatPublicationCoordinator.CaptureScope capture = coordinator.BeginCapture(turn);
        using var inherited = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        Task<bool> lateCapture = Task.Run(() =>
        {
            using var packet = Packet.Create((int)Protocol.G_TO_C_HEART_BEAT);
            inherited.Set();
            release.Wait();
            return coordinator.TryCapturePacket(recipient, packet);
        });

        Assert.True(inherited.Wait(TimeSpan.FromSeconds(2)));
        SwarmCombatPublicationCoordinator.PublicationPlan plan = capture.Freeze();
        release.Set();
        Assert.False(await lateCapture.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, plan.Count);
        coordinator.DispatchAndRetire(turn, plan);
    }

    [Fact]
    public void Capture_RejectsForeignTurnAndNestedScope()
    {
        SwarmCombatPublicationCoordinator owner = CreateCoordinator(61_008);
        SwarmCombatPublicationCoordinator foreign = CreateUnregisteredCoordinator();
        using SwarmCombatPublicationCoordinator.PublicationTurn turn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                owner.TryBeginDueRealtimeTurn(61_008));

        Assert.Throws<InvalidOperationException>(() => foreign.BeginCapture(turn));
        using SwarmCombatPublicationCoordinator.CaptureScope capture = owner.BeginCapture(turn);
        Assert.Throws<InvalidOperationException>(() => owner.BeginCapture(turn));

        SwarmCombatPublicationCoordinator.PublicationPlan plan = capture.Freeze();
        owner.DispatchAndRetire(turn, plan);
    }

    [Fact]
    public void DefaultFailure_PreservesPreparedState_AbortsRemainingPlan_AndRetiresTurn()
    {
        const long matchingId = 61_009;
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(matchingId);
        using SwarmCombatPublicationCoordinator.PublicationTurn turn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
        var events = new List<string>();
        int authoritativeState = 0;
        using SwarmCombatPublicationCoordinator.CaptureScope capture = coordinator.BeginCapture(turn);
        authoritativeState = 1;
        coordinator.AppendDeferredStep(() => events.Add("before"));
        coordinator.AppendDeferredStep(() => throw new InvalidOperationException("send failed"));
        coordinator.AppendDeferredStep(() => events.Add("after"));
        SwarmCombatPublicationCoordinator.PublicationPlan plan = capture.Freeze();

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => coordinator.DispatchAndRetire(turn, plan));

        Assert.Equal("send failed", failure.Message);
        Assert.Equal(1, authoritativeState);
        Assert.Equal(["before"], events);
        Assert.False(coordinator.Inspect(matchingId)?.HasActiveTurn);
        using SwarmCombatPublicationCoordinator.PublicationTurn next =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
    }

    [Fact]
    public void BestEffortFailure_PreservesPreparedState_SkipsGroup_AndContinuesPlan()
    {
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(61_010);
        using SwarmCombatPublicationCoordinator.PublicationTurn turn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(61_010));
        var events = new List<string>();
        var failures = new List<string>();
        int authoritativeBotState = 0;
        using SwarmCombatPublicationCoordinator.CaptureScope capture = coordinator.BeginCapture(turn);
        coordinator.AppendDeferredStep(() => events.Add("before"));
        using (coordinator.BeginBestEffortGroup(failure => failures.Add(failure.Message)))
        {
            authoritativeBotState = 1;
            coordinator.AppendDeferredStep(() => events.Add("group-start"));
            coordinator.AppendDeferredStep(() => throw new InvalidOperationException("bot send failed"));
            coordinator.AppendDeferredStep(() => events.Add("group-skipped"));
        }
        coordinator.AppendDeferredStep(() => events.Add("after"));
        SwarmCombatPublicationCoordinator.PublicationPlan plan = capture.Freeze();

        coordinator.DispatchAndRetire(turn, plan);

        Assert.Equal(1, authoritativeBotState);
        Assert.Equal(["before", "group-start", "after"], events);
        Assert.Equal(["bot send failed"], failures);
    }

    [Fact]
    public void OptionalBestEffortGroup_OnlyOpensDuringActiveCapture()
    {
        const long matchingId = 61_027;
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(matchingId);
        Assert.Null(coordinator.TryBeginBestEffortGroup(static _ => { }));

        using SwarmCombatPublicationCoordinator.PublicationTurn turn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
        using SwarmCombatPublicationCoordinator.CaptureScope capture =
            coordinator.BeginCapture(turn);
        using IDisposable group = Assert.IsAssignableFrom<IDisposable>(
            coordinator.TryBeginBestEffortGroup(static _ => { }));
        group.Dispose();

        SwarmCombatPublicationCoordinator.PublicationPlan plan = capture.Freeze();
        coordinator.DispatchAndRetire(turn, plan);
        Assert.Null(coordinator.TryBeginBestEffortGroup(static _ => { }));
    }

    [Fact]
    public void PreparationFailure_DispatchesCapturedPrefixBeforeOriginalRethrow()
    {
        const long matchingId = 61_028;
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(matchingId);
        using SwarmCombatPublicationCoordinator.PublicationTurn turn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
        var events = new List<string>();
        var expected = new InvalidOperationException("prepare failed");
        ExceptionDispatchInfo? preparationFailure = null;
        using SwarmCombatPublicationCoordinator.CaptureScope capture =
            coordinator.BeginCapture(turn);

        try
        {
            coordinator.AppendDeferredStep(() => events.Add("captured-prefix"));
            throw expected;
        }
        catch (Exception ex)
        {
            preparationFailure = ExceptionDispatchInfo.Capture(ex);
        }

        SwarmCombatPublicationCoordinator.PublicationPlan plan = capture.Freeze();
        coordinator.DispatchAndRetire(turn, plan);
        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(
            () => preparationFailure!.Throw());

        Assert.Same(expected, actual);
        Assert.Equal(["captured-prefix"], events);
    }

    [Fact]
    public void OrbVisualDeferredSteps_FailedKeyIsCommittedAndUnvisitedKeysRetry()
    {
        const long matchingId = 61_029;
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(matchingId);
        var committed = new HashSet<int>();
        var dispatched = new List<int>();
        int[] candidates = [1, 2, 3];

        using (SwarmCombatPublicationCoordinator.PublicationTurn firstTurn =
               Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                   coordinator.TryBeginDueRealtimeTurn(matchingId)))
        {
            using SwarmCombatPublicationCoordinator.CaptureScope firstCapture =
                coordinator.BeginCapture(firstTurn);
            foreach (int candidate in candidates)
            {
                int frozenCandidate = candidate;
                coordinator.AppendDeferredStep(() =>
                {
                    committed.Add(frozenCandidate);
                    dispatched.Add(frozenCandidate);
                    if (frozenCandidate == 1)
                        throw new InvalidOperationException("visual send failed");
                });
            }

            SwarmCombatPublicationCoordinator.PublicationPlan firstPlan = firstCapture.Freeze();
            Assert.Throws<InvalidOperationException>(
                () => coordinator.DispatchAndRetire(firstTurn, firstPlan));
        }

        Assert.Equal([1], committed.OrderBy(value => value).ToArray());
        Assert.Equal([1], dispatched);
        int[] retryCandidates =
            candidates.Where(candidate => !committed.Contains(candidate)).ToArray();

        using SwarmCombatPublicationCoordinator.PublicationTurn retryTurn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
        using SwarmCombatPublicationCoordinator.CaptureScope retryCapture =
            coordinator.BeginCapture(retryTurn);
        foreach (int candidate in retryCandidates)
        {
            int frozenCandidate = candidate;
            coordinator.AppendDeferredStep(() =>
            {
                committed.Add(frozenCandidate);
                dispatched.Add(frozenCandidate);
            });
        }

        SwarmCombatPublicationCoordinator.PublicationPlan retryPlan = retryCapture.Freeze();
        coordinator.DispatchAndRetire(retryTurn, retryPlan);

        Assert.Equal([1, 2, 3], committed.OrderBy(value => value).ToArray());
        Assert.Equal([1, 2, 3], dispatched);
    }

    [Fact]
    public async Task DisposeDuringDispatch_DoesNotReleaseSameMatchTurn()
    {
        const long matchingId = 61_011;
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(matchingId);
        SwarmCombatPublicationCoordinator.PublicationTurn turn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
        using var dispatchEntered = new ManualResetEventSlim();
        using var releaseDispatch = new ManualResetEventSlim();
        using SwarmCombatPublicationCoordinator.CaptureScope capture = coordinator.BeginCapture(turn);
        coordinator.AppendDeferredStep(() =>
        {
            dispatchEntered.Set();
            releaseDispatch.Wait();
        });
        SwarmCombatPublicationCoordinator.PublicationPlan plan = capture.Freeze();

        Task dispatch = Task.Run(() => coordinator.DispatchAndRetire(turn, plan));
        Assert.True(dispatchEntered.Wait(TimeSpan.FromSeconds(2)));
        try
        {
            turn.Dispose();
            Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
        }
        finally
        {
            releaseDispatch.Set();
        }

        await dispatch.WaitAsync(TimeSpan.FromSeconds(2));
        using SwarmCombatPublicationCoordinator.PublicationTurn next =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
    }

    [Fact]
    public void RetiredPlan_IsRejectedWithoutReplay()
    {
        const long matchingId = 61_012;
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(matchingId);
        SwarmCombatPublicationCoordinator.PublicationTurn turn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
        var events = new List<string>();
        using SwarmCombatPublicationCoordinator.CaptureScope capture = coordinator.BeginCapture(turn);
        coordinator.AppendDeferredStep(() => events.Add("replayed"));
        SwarmCombatPublicationCoordinator.PublicationPlan plan = capture.Freeze();

        turn.Dispose();

        Assert.Throws<InvalidOperationException>(() => coordinator.DispatchAndRetire(turn, plan));
        Assert.Empty(events);
        using SwarmCombatPublicationCoordinator.PublicationTurn next =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
    }

    [Fact]
    public void Replay_IsNotCapturedByAnotherAmbientMatchCapture()
    {
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(61_013, 61_014);
        var directlySent = new List<byte[]>();
        bool? bestEffortGroupOpenedDuringReplay = null;
        SwarmCombatPublicationCoordinator.PacketRecipient? recipient = null;
        recipient = new SwarmCombatPublicationCoordinator.PacketRecipient(packet =>
        {
            IDisposable? replayGroup =
                coordinator.TryBeginBestEffortGroup(static _ => { });
            bestEffortGroupOpenedDuringReplay = replayGroup != null;
            replayGroup?.Dispose();
            if (!coordinator.TryCapturePacket(recipient!, packet))
                directlySent.Add(packet.ToBytes());
        });

        using SwarmCombatPublicationCoordinator.PublicationTurn firstTurn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(61_013));
        using SwarmCombatPublicationCoordinator.CaptureScope firstCapture =
            coordinator.BeginCapture(firstTurn);
        byte[] expected;
        using (var packet = Packet.Create((int)Protocol.G_TO_C_HEART_BEAT, 7013))
        {
            packet.SetBody([7, 1, 3]);
            Assert.True(coordinator.TryCapturePacket(recipient, packet));
            expected = packet.ToBytes();
        }
        SwarmCombatPublicationCoordinator.PublicationPlan firstPlan = firstCapture.Freeze();

        using SwarmCombatPublicationCoordinator.PublicationTurn secondTurn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(61_014));
        using SwarmCombatPublicationCoordinator.CaptureScope secondCapture =
            coordinator.BeginCapture(secondTurn);

        coordinator.DispatchAndRetire(firstTurn, firstPlan);

        Assert.Single(directlySent);
        Assert.Equal(expected, directlySent[0]);
        Assert.False(bestEffortGroupOpenedDuringReplay);
        SwarmCombatPublicationCoordinator.PublicationPlan secondPlan = secondCapture.Freeze();
        Assert.Equal(0, secondPlan.Count);
        coordinator.DispatchAndRetire(secondTurn, secondPlan);
    }

    [Fact]
    public async Task ClearMatching_RemovesEntryAndWakesRequiredWaiter()
    {
        const long matchingId = 61_015;
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(matchingId);
        SwarmCombatPublicationCoordinator.PublicationTurn active =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
        Task<SwarmCombatPublicationCoordinator.PublicationTurn?> waiting =
            Task.Run(() => coordinator.BeginRequiredTurn(matchingId));
        Assert.True(SpinWait.SpinUntil(
            () => coordinator.Inspect(matchingId)?.RequiredWaiterCount == 1,
            TimeSpan.FromSeconds(2)));

        coordinator.ClearMatching(matchingId);

        Assert.Null(await waiting.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Null(coordinator.Inspect(matchingId));
        Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
        Assert.Null(coordinator.BeginRequiredTurn(matchingId));

        active.Dispose();
        Assert.Null(coordinator.Inspect(matchingId));
        Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
    }

    [Fact]
    public void Begin_FailsClosedUntilMatchRegistration()
    {
        const long matchingId = 61_016;
        SwarmCombatPublicationCoordinator coordinator = CreateUnregisteredCoordinator();

        Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
        Assert.Null(coordinator.BeginRequiredTurn(matchingId));
        Assert.True(coordinator.RegisterMatching(matchingId));
        Assert.False(coordinator.RegisterMatching(matchingId));

        using SwarmCombatPublicationCoordinator.PublicationTurn turn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
    }

    [Fact]
    public void CaptureDelegate_WithoutFrameReturnsFalseWithoutMutatingPacket()
    {
        SwarmCombatPublicationCoordinator coordinator = CreateUnregisteredCoordinator();
        int directSendCount = 0;
        Action<IPacket> sendDirect = _ => directSendCount++;
        using var packet = Packet.Create((int)Protocol.G_TO_C_HEART_BEAT, 7017);
        packet.SetBody([1, 7]);
        byte[] before = packet.ToBytes();

        Assert.False(coordinator.TryCapturePacket(sendDirect, packet));

        Assert.Equal(before, packet.ToBytes());
        Assert.Equal(0, directSendCount);
    }

    [Fact]
    public void CaptureDelegate_ActiveFrameDefersOneExactWireReplay()
    {
        const long matchingId = 61_026;
        int bodyOffset = Config.HEADER_SIZE + sizeof(int) + sizeof(long);
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(matchingId);
        using SwarmCombatPublicationCoordinator.PublicationTurn turn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
        var sent = new List<byte[]>();
        Action<IPacket> sendDirect = packet => sent.Add(packet.ToBytes());
        using SwarmCombatPublicationCoordinator.CaptureScope capture = coordinator.BeginCapture(turn);
        byte[] expected;
        using (var packet = Packet.Create((int)Protocol.G_TO_C_HEART_BEAT, 7026))
        {
            packet.SetBody([2, 6]);
            Assert.True(coordinator.TryCapturePacket(sendDirect, packet));
            expected = packet.ToBytes();
            packet.Buffer[bodyOffset] = 99;
        }

        Assert.Empty(sent);
        SwarmCombatPublicationCoordinator.PublicationPlan plan = capture.Freeze();
        coordinator.DispatchAndRetire(turn, plan);

        Assert.Single(sent);
        Assert.Equal(expected, sent[0]);
    }

    [Fact]
    public void MatchRuntimeInitializer_RunsOnceForEveryLazyCreationPath()
    {
        var initialized = new Dictionary<long, int>();
        var registry = new MatchRuntimeRegistry();
        registry.SetRuntimeInitializer(matchingId =>
            initialized[matchingId] = initialized.GetValueOrDefault(matchingId) + 1);

        Assert.True(registry.TryExecute(61_018, static () => { }));
        Assert.True(registry.TryBindOwnerFence(61_018, ownerFence: 18));
        using IDisposable executionFirst = Assert.IsAssignableFrom<IDisposable>(
            registry.TryAcquireOperation(61_018, static () => { }));

        Assert.True(registry.TryBindOwnerFence(61_019, ownerFence: 19));
        Assert.True(registry.TryExecute(61_019, static () => { }));

        using IDisposable leaseFirst = Assert.IsAssignableFrom<IDisposable>(
            registry.TryAcquireOperation(61_020, static () => { }));
        Assert.True(registry.TryExecute(61_020, static () => { }));

        Assert.True(registry.TryFinalize(
            61_021,
            static () => true,
            static () => { }));

        Assert.Equal(4, initialized.Count);
        Assert.All(initialized.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public void MatchRuntimeInitializer_MustBeConfiguredBeforeRuntimeUse()
    {
        var registry = new MatchRuntimeRegistry();
        Assert.True(registry.TryExecute(61_022, static () => { }));

        Assert.Throws<InvalidOperationException>(
            () => registry.SetRuntimeInitializer(static _ => { }));
    }

    [Fact]
    public void MatchRuntimeInitializer_FailureIsRetriedBeforeActionRuns()
    {
        const long matchingId = 61_024;
        int initializationAttempts = 0;
        int actionCount = 0;
        var registry = new MatchRuntimeRegistry();
        registry.SetRuntimeInitializer(_ =>
        {
            initializationAttempts++;
            if (initializationAttempts == 1)
                throw new InvalidOperationException("registration failed");
        });

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => registry.TryExecute(matchingId, () => actionCount++));
        Assert.Equal("registration failed", failure.Message);
        Assert.Equal(0, actionCount);

        Assert.True(registry.TryExecute(matchingId, () => actionCount++));
        Assert.Equal(2, initializationAttempts);
        Assert.Equal(1, actionCount);
    }

    [Fact]
    public void MatchRuntimeInitializer_CannotBeConfiguredAfterRemovedRuntimeWasUsed()
    {
        const long matchingId = 61_025;
        var registry = new MatchRuntimeRegistry();
        Assert.True(registry.TryExecute(matchingId, static () => { }));
        Assert.True(registry.TryFinalize(
            matchingId,
            static () => true,
            static () => { }));
        Assert.Equal(0, registry.ActiveCount);

        Assert.Throws<InvalidOperationException>(
            () => registry.SetRuntimeInitializer(static _ => { }));
    }

    [Fact]
    public async Task MatchRuntimeInitializer_SetupRaceIsAtomicWithFirstRuntimeUse()
    {
        for (int iteration = 0; iteration < 32; iteration++)
        {
            var registry = new MatchRuntimeRegistry();
            var events = new ConcurrentQueue<string>();
            using var start = new Barrier(2);
            Exception? setupFailure = null;
            bool executed = false;

            Task setup = Task.Run(() =>
            {
                start.SignalAndWait();
                try
                {
                    registry.SetRuntimeInitializer(_ => events.Enqueue("initializer"));
                }
                catch (Exception ex)
                {
                    setupFailure = ex;
                }
            });
            Task use = Task.Run(() =>
            {
                start.SignalAndWait();
                executed = registry.TryExecute(
                    62_000 + iteration,
                    () => events.Enqueue("action"));
            });

            await Task.WhenAll(setup, use).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(executed);
            if (setupFailure == null)
                Assert.Equal(["initializer", "action"], events);
            else
            {
                Assert.IsType<InvalidOperationException>(setupFailure);
                Assert.Equal(["action"], events);
            }
        }
    }

    [Fact]
    public void RuntimeLease_DefersCombatCoordinatorCleanupUntilTurnCanRetire()
    {
        const long matchingId = 61_023;
        SwarmCombatPublicationCoordinator coordinator = CreateUnregisteredCoordinator();
        var registry = new MatchRuntimeRegistry();
        registry.SetRuntimeInitializer(id => Assert.True(coordinator.RegisterMatching(id)));
        IDisposable operation = Assert.IsAssignableFrom<IDisposable>(
            registry.TryAcquireOperation(matchingId, static () => { }));
        SwarmCombatPublicationCoordinator.PublicationTurn turn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));

        Assert.True(registry.TryFinalize(
            matchingId,
            static () => true,
            () => coordinator.ClearMatching(matchingId)));
        Assert.NotNull(coordinator.Inspect(matchingId));

        turn.Dispose();
        operation.Dispose();

        Assert.Null(coordinator.Inspect(matchingId));
        Assert.Null(coordinator.TryBeginDueRealtimeTurn(matchingId));
    }

    [Fact]
    public void RuntimeLease_PublishesTerminalOnlyAfterCombatRetiresAndLeaseReleases()
    {
        const long matchingId = 61_030;
        var events = new List<string>();
        SwarmCombatPublicationCoordinator coordinator = CreateUnregisteredCoordinator();
        var registry = new MatchRuntimeRegistry();
        registry.SetRuntimeInitializer(id => Assert.True(coordinator.RegisterMatching(id)));
        Assert.True(registry.TryExecute(matchingId, static () => { }));
        SwarmCombatPublicationCoordinator.PublicationTurn turn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginDueRealtimeTurn(matchingId));
        SwarmCombatPublicationCoordinator.PublicationPlan? plan = null;
        IDisposable operation = Assert.IsAssignableFrom<IDisposable>(
            registry.TryAcquireOperation(
                matchingId,
                () =>
                {
                    using SwarmCombatPublicationCoordinator.CaptureScope capture =
                        coordinator.BeginCapture(turn);
                    coordinator.AppendDeferredStep(() => events.Add("combat"));
                    plan = capture.Freeze();
                }));

        Assert.True(registry.TryFinalize(
            matchingId,
            static () => true,
            beforeFinalized: () => events.Add("terminal"),
            cleanup: () =>
            {
                events.Add("cleanup");
                coordinator.ClearMatching(matchingId);
            },
            afterFinalized: () => events.Add("lifecycle")));

        coordinator.DispatchAndRetire(
            turn,
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationPlan>(plan));
        Assert.Equal(["combat"], events);
        Assert.NotNull(coordinator.Inspect(matchingId));

        turn.Dispose();
        Assert.Equal(["combat"], events);
        operation.Dispose();

        Assert.Equal(["combat", "terminal", "cleanup", "lifecycle"], events);
        Assert.Null(coordinator.Inspect(matchingId));
    }

    [Fact]
    public void RealtimeTick_ContinuesToHigherMatchWhenLowerMatchClockFails()
    {
        const long lowerMatchingId = 61_041;
        const long higherMatchingId = 61_042;
        int timestampCalls = 0;
        var coordinator = new SwarmCombatPublicationCoordinator(
            TimeSpan.FromMilliseconds(50),
            () =>
            {
                int call = Interlocked.Increment(ref timestampCalls);
                if (call == 1)
                    throw new InvalidOperationException("lower match clock failed");
                return 1_000;
            },
            1_000);
        Assert.True(coordinator.RegisterMatching(lowerMatchingId));
        Assert.True(coordinator.RegisterMatching(higherMatchingId));
        GameServer server = CreateRealtimeTickTestServer();
        typeof(GameServer)
            .GetField(
                "_swarmCombatPublicationCoordinator",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(server, coordinator);
        BotPlayerManager botPlayerManager = Assert.IsType<BotPlayerManager>(
            typeof(GameServer)
                .GetField("_botPlayerManager", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(server));
        botPlayerManager.RegisterBots(lowerMatchingId, MapId.School2, []);
        botPlayerManager.RegisterBots(higherMatchingId, MapId.School2, []);

        typeof(GameServer)
            .GetMethod(
                "ProcessProximityAutoCombatTick",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(server, new object?[] { null });

        Assert.Equal(2, Volatile.Read(ref timestampCalls));
        Assert.False(Assert.IsType<SwarmCombatPublicationCoordinator.PublicationDiagnostics>(
            coordinator.Inspect(higherMatchingId)).HasActiveTurn);
    }

    [Fact]
    public void IntegrationSeam_WiresLifecycleSendFallbackAndActivatedEntrypoints()
    {
        string repositoryRoot = FindRepositoryRoot();
        string server = ReadNormalizedSource(repositoryRoot, "game_server", "GameServer.cs");
        string session = ReadNormalizedSource(
            repositoryRoot, "game_server", "Network", "GameClientSession.cs");
        string registry = ReadNormalizedSource(
            repositoryRoot, "game_server", "Services", "MatchRuntimeRegistry.cs");

        Assert.Contains(
            "private readonly SwarmCombatPublicationCoordinator _swarmCombatPublicationCoordinator =\n" +
            "        new(TimeSpan.FromMilliseconds(ProximityAutoCombatTickIntervalMs));",
            server);
        string start = ReadMethodSlice(
            server,
            "public async Task StartAsync(CancellationToken cancellationToken)",
            "public async Task StopAsync(CancellationToken cancellationToken)");
        AssertInOrder(
            start,
            "InitializeServices();",
            "StartTcpServer();",
            "StartResourceTickTimer();",
            "StartProximityAutoCombatTimer();");
        string stop = ReadMethodSlice(
            server,
            "public async Task StopAsync(CancellationToken cancellationToken)",
            "private async Task RunShutdownStageAsync(");
        AssertInOrder(
            stop,
            "_proximityAutoCombatTimer",
            "_proximityAutoCombatTimer = null;",
            ".Select(timer => timer!.DisposeAsync().AsTask())",
            "WaitForPendingMatchOwnerLossesAsync()");
        AssertInOrder(
            stop,
            "_botMovementTimer",
            "_botMovementTimer = null;",
            ".Select(timer => timer!.DisposeAsync().AsTask())");
        string initialization = ReadMethodSlice(
            server,
            "private void InitializeServices()",
            "private void StartTcpServer()");
        AssertInOrder(
            initialization,
            "_matchRuntimeRegistry.SetRuntimeInitializer(",
            "RegisterMatchRuntimeComponents",
            "_matchRuntimeCleanupCoordinator = new MatchRuntimeCleanupCoordinator(",
            "\"combat publication\"",
            "_swarmCombatPublicationCoordinator.ClearMatching",
            "\"session runtime\"");
        int cleanupPlanStart = initialization.IndexOf(
            "_matchRuntimeCleanupCoordinator = new MatchRuntimeCleanupCoordinator(",
            StringComparison.Ordinal);
        int firstCleanupStep = initialization.IndexOf(
            "new MatchRuntimeCleanupStep(",
            cleanupPlanStart,
            StringComparison.Ordinal);
        int secondCleanupStep = initialization.IndexOf(
            "new MatchRuntimeCleanupStep(",
            firstCleanupStep + 1,
            StringComparison.Ordinal);
        int combatStepName = initialization.IndexOf(
            "\"combat publication\"",
            firstCleanupStep,
            StringComparison.Ordinal);
        int combatClear = initialization.IndexOf(
            "_swarmCombatPublicationCoordinator.ClearMatching",
            firstCleanupStep,
            StringComparison.Ordinal);
        Assert.True(
            cleanupPlanStart >= 0 &&
            firstCleanupStep > cleanupPlanStart &&
            combatStepName > firstCleanupStep &&
            combatClear > combatStepName &&
            secondCleanupStep > combatClear,
            "Combat publication cleanup must be the first component cleanup step.");
        Assert.Equal(
            1,
            CountOccurrences(
                initialization,
                "_swarmCombatPublicationCoordinator.ClearMatching"));
        Assert.Contains("_swarmCombatPublicationCoordinator.TryCapturePacket,", server);
        string registration = ReadMethodSlice(
            server,
            "private void RegisterMatchRuntimeComponents(long matchingId)",
            "private bool IsSwarmFrontOrbDamaged(");
        AssertInOrder(
            registration,
            "_swarmBotTickCoordinator.RegisterMatching(matchingId)",
            "_swarmCombatPublicationCoordinator.RegisterMatching(matchingId)",
            "throw new InvalidOperationException(");
        Assert.Contains("internal void SetRuntimeInitializer(Action<long> runtimeInitializer)", registry);
        Assert.Equal(4, CountOccurrences(registry, "GetOrCreateRuntime(matchingId)"));
        Assert.Equal(4, CountOccurrences(registry, "runtime.EnsureInitialized("));

        Assert.Contains(
            "private readonly Func<Action<IPacket>, IPacket, bool>? _tryCaptureCombatPublication;",
            session);
        Assert.Contains(
            "Func<Action<IPacket>, IPacket, bool> tryCaptureCombatPublication,",
            session);
        string sendOverride = ReadMethodSlice(
            session,
            "public override void Send(IPacket packet)",
            "private void SendCombatPublicationDirect(IPacket packet)");
        AssertInOrder(
            sendOverride,
            "Func<Action<IPacket>, IPacket, bool>? tryCapture = _tryCaptureCombatPublication;",
            "Action<IPacket>? sendDirect = _sendCombatPublicationDirect;",
            "tryCapture != null && sendDirect != null && tryCapture(sendDirect, packet)",
            "return;",
            "base.Send(packet);");
        Assert.Contains(
            "private void SendCombatPublicationDirect(IPacket packet) => base.Send(packet);",
            session);

        string proximity = ReadNormalizedSource(
            repositoryRoot, "game_server", "GameServer.ProximityAutoCombat.cs");
        string settlement = ReadNormalizedSource(
            repositoryRoot, "game_server", "GameServer.MatchSettlement.cs");
        string arena = ReadNormalizedSource(
            repositoryRoot, "game_server", "GameServer.SwarmArena.cs");
        string realtimeTick = ReadMethodSlice(
            proximity,
            "private void ProcessProximityAutoCombatTick(object? state)",
            "private void ProcessProximityAutoCombatForMatching(");
        string combatPublicationAdapter = ReadMethodSlice(
            proximity,
            "private void PrepareAndDispatchCombatPublication(",
            "private void PublishOrderedSessionPublication(");
        string sessionPublicationAdapter = ReadMethodSlice(
            proximity,
            "private void PublishOrderedSessionPublication(",
            "private void PrepareAndDispatchMatchPublication(");
        string publicationHelper = ReadMethodSlice(
            proximity,
            "private void PrepareAndDispatchMatchPublication(",
            "private static void AddInventoryCombatActors(");
        string resourceTick = ReadMethodSlice(
            settlement,
            "private void ProcessResourceTickForMatching(",
            "private void CleanupMatchSettlementState(");
        string botElimination = ReadMethodSlice(
            server,
            "private void ProcessBotElimination(",
            "private void DropBotInventoryAtCurrentPosition(");

        AssertInOrder(
            realtimeTick,
            "List<GameClientSession> activeSessions;",
            "try",
            "_sessionRegistry.SnapshotWhere(",
            "activeMatchingIds = GetActiveMatchingIds();",
            "catch (Exception ex)",
            "Proximity auto combat snapshot failed",
            "return;",
            "foreach (long matchingId in activeMatchingIds)");
        AssertInOrder(
            realtimeTick,
            "foreach (long matchingId in activeMatchingIds)",
            "try",
            "_swarmCombatPublicationCoordinator.TryBeginDueRealtimeTurn(matchingId)",
            "if (publicationTurn == null)",
            "PrepareAndDispatchCombatPublication(",
            "() => ProcessProximityAutoCombatForMatching(matchingId, activeSessions)",
            "catch (Exception ex)",
            "MatchingId={MatchingId}");
        Assert.DoesNotContain("_proximityAutoCombatProcessing", proximity);
        Assert.DoesNotContain("Interlocked.Exchange(", realtimeTick);
        Assert.DoesNotContain("Volatile.Write(", realtimeTick);
        AssertInOrder(
            resourceTick,
            "_swarmCombatPublicationCoordinator.BeginRequiredTurn(matchingId)",
            "if (publicationTurn == null)",
            "PrepareAndDispatchCombatPublication(",
            "ProcessProximityAutoCombatForMatching(matchingId, activeSessions);",
            "target.Session.ModifyStats(",
            "var eliminatedTargets = targets",
            "foreach (var candidate in survivorsToEliminate.AsEnumerable().Reverse())",
            "target.Session.EliminateForSettlement(",
            "ProcessBotElimination(",
            "_matchRosterManager.CheckGameOver(matchingId)",
            "resultHost.TryEndMatch(winnerId.Value, resolution.DecisiveCriterion);");
        Assert.DoesNotContain("_matchRuntimeRegistry.TryExecute(", realtimeTick);
        Assert.DoesNotContain("_matchRuntimeRegistry.TryExecute(", resourceTick);

        AssertInOrder(
            combatPublicationAdapter,
            "_matchRuntimeRegistry.TryAcquireOperation(",
            "return runtimeOperation != null;",
            "prepare,",
            "() => runtimeOperation?.Dispose()");
        AssertInOrder(
            sessionPublicationAdapter,
            "_swarmCombatPublicationCoordinator.BeginOrderedTurn(matchingId)",
            "_matchRuntimeRegistry.TryExecute(",
            "captureActivePreparation",
            "captureFinalizingRejection();",
            "releaseOwnedRuntimeOperation: null");
        Assert.DoesNotContain("TryAcquireOperation", sessionPublicationAdapter);
        AssertInOrder(
            publicationHelper,
            "SwarmCombatPublicationCoordinator.CaptureScope? capture = null;",
            "_swarmCombatPublicationCoordinator.BeginCapture(publicationTurn)",
            "preparation();",
            "preparationFailure = ExceptionDispatchInfo.Capture(ex);",
            "publicationPlan = capture.Freeze();",
            "bool prepared = tryPrepare(",
            "_swarmCombatPublicationCoordinator.DispatchAndRetire(",
            "publicationTurn.Dispose();",
            "releaseOwnedRuntimeOperation?.Invoke();",
            "pendingFailure?.Throw();");
        string capturePreparation = ReadMethodSlice(
            publicationHelper,
            "void CapturePreparation(Action preparation)",
            "try\n        {\n            bool prepared = tryPrepare(");
        AssertInOrder(
            capturePreparation,
            "CaptureScope? capture = null;",
            "capture = _swarmCombatPublicationCoordinator.BeginCapture(publicationTurn);",
            "preparation();",
            "preparationFailure = ExceptionDispatchInfo.Capture(ex);",
            "publicationPlan = capture.Freeze();",
            "if (preparationFailure != null && pendingFailure == null)",
            "pendingFailure = preparationFailure;",
            "RecordFailure(ex);",
            "finally",
            "capture?.Dispose();");
        Assert.DoesNotContain("throw;", capturePreparation);
        Assert.DoesNotContain("throw ", capturePreparation);
        AssertInOrder(
            publicationHelper,
            "publicationTurn.Dispose();",
            "releaseOwnedRuntimeOperation?.Invoke();",
            "if (preparationFailure != null &&",
            "ReferenceEquals(pendingFailure, preparationFailure)",
            "preparationFailure!.Throw();",
            "throw new AggregateException(",
            "pendingFailure?.Throw();");
        AssertInOrder(
            botElimination,
            "try",
            "_swarmCombatPublicationCoordinator.TryBeginBestEffortGroup(",
            "_matchRosterManager.TryEliminatePlayer(",
            "catch (Exception ex)",
            "finally",
            "publicationGroup?.Dispose();");
        Assert.Contains("AppendOrbVisualStatePublicationSteps(", arena);
        Assert.Contains(
            "_swarmCombatPublicationCoordinator.AppendDeferredStep(",
            proximity);
        Assert.Equal(1, CountOccurrences(proximity, ".TryBeginDueRealtimeTurn("));
        Assert.Equal(1, CountOccurrences(settlement, ".BeginRequiredTurn("));
        Assert.Equal(1, CountOccurrences(proximity, ".BeginCapture("));
        Assert.Equal(1, CountOccurrences(proximity, ".DispatchAndRetire("));
    }

    private static SwarmCombatPublicationCoordinator CreateCoordinator(params long[] matchingIds)
    {
        SwarmCombatPublicationCoordinator coordinator = CreateUnregisteredCoordinator();
        foreach (long matchingId in matchingIds)
            Assert.True(coordinator.RegisterMatching(matchingId));
        return coordinator;
    }

    private static SwarmCombatPublicationCoordinator CreateUnregisteredCoordinator()
    {
        long timestamp = -1;
        return new SwarmCombatPublicationCoordinator(
            TimeSpan.FromMilliseconds(1),
            () => unchecked((ulong)Interlocked.Increment(ref timestamp)),
            1_000);
    }

    private static GameServer CreateRealtimeTickTestServer()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        return new GameServer(
            configuration,
            NullLogger<GameServer>.Instance,
            null!,
            null!,
            null!,
            null!,
            new ServerConfig
            {
                ServerType = "GameServer",
                ServerId = 1,
                GameServerNum = 1
            },
            null!,
            new GameServerScalingOptions { Enabled = false },
            null!,
            null!,
            new ServerReadinessState());
    }

    private static void AssertInOrder(string source, params string[] markers)
    {
        int previousIndex = -1;
        foreach (string marker in markers)
        {
            int index = source.IndexOf(marker, previousIndex + 1, StringComparison.Ordinal);
            Assert.True(index > previousIndex, $"Expected '{marker}' after index {previousIndex}.");
            previousIndex = index;
        }
    }

    private static int CountOccurrences(string source, string marker)
    {
        int count = 0;
        int start = 0;
        while ((start = source.IndexOf(marker, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += marker.Length;
        }

        return count;
    }

    private static string ReadMethodSlice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{startMarker}'.");
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find '{endMarker}' after '{startMarker}'.");
        return source[start..end];
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

    private static string ReadNormalizedSource(string repositoryRoot, params string[] pathParts)
    {
        string[] fullPathParts = [repositoryRoot, .. pathParts];
        return File.ReadAllText(Path.Combine(fullPathParts)).Replace("\r\n", "\n");
    }
}
