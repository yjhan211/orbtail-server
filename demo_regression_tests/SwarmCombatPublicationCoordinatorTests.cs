using game_server.services;
using network.common;
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
                coordinator.TryBeginRealtimeTurn(61_001));

        Assert.Null(coordinator.TryBeginRealtimeTurn(61_001));
        using SwarmCombatPublicationCoordinator.PublicationTurn independent =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginRealtimeTurn(61_002));

        first.Dispose();
        using SwarmCombatPublicationCoordinator.PublicationTurn next =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginRealtimeTurn(61_001));
    }

    [Fact]
    public async Task RequiredTurn_WaitsAndPreventsRealtimeSteal()
    {
        const long matchingId = 61_003;
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(matchingId);
        SwarmCombatPublicationCoordinator.PublicationTurn first =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginRealtimeTurn(matchingId));
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
        Assert.Null(coordinator.TryBeginRealtimeTurn(matchingId));

        first.Dispose();
        SwarmCombatPublicationCoordinator.PublicationTurn required =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                await requiredTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Null(coordinator.TryBeginRealtimeTurn(matchingId));
        required.Dispose();

        using SwarmCombatPublicationCoordinator.PublicationTurn nextRealtime =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginRealtimeTurn(matchingId));
    }

    [Fact]
    public async Task RequiredTurn_DoesNotBlockAnotherMatch()
    {
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(61_004, 61_005);
        using SwarmCombatPublicationCoordinator.PublicationTurn blocked =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginRealtimeTurn(61_004));

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
    public void Capture_DeepCopiesRecordedWireBytes_AndPreservesMixedStepOrder()
    {
        int bodyOffset = Config.HEADER_SIZE + sizeof(int) + sizeof(long);
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(61_006);
        using SwarmCombatPublicationCoordinator.PublicationTurn turn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginRealtimeTurn(61_006));
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
                coordinator.TryBeginRealtimeTurn(61_007));
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
        var foreign = new SwarmCombatPublicationCoordinator();
        using SwarmCombatPublicationCoordinator.PublicationTurn turn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                owner.TryBeginRealtimeTurn(61_008));

        Assert.Throws<InvalidOperationException>(() => foreign.BeginCapture(turn));
        using SwarmCombatPublicationCoordinator.CaptureScope capture = owner.BeginCapture(turn);
        Assert.Throws<InvalidOperationException>(() => owner.BeginCapture(turn));

        SwarmCombatPublicationCoordinator.PublicationPlan plan = capture.Freeze();
        owner.DispatchAndRetire(turn, plan);
    }

    [Fact]
    public void DefaultFailure_AbortsRemainingPlan_AndRetiresTurn()
    {
        const long matchingId = 61_009;
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(matchingId);
        using SwarmCombatPublicationCoordinator.PublicationTurn turn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginRealtimeTurn(matchingId));
        var events = new List<string>();
        using SwarmCombatPublicationCoordinator.CaptureScope capture = coordinator.BeginCapture(turn);
        coordinator.AppendDeferredStep(() => events.Add("before"));
        coordinator.AppendDeferredStep(() => throw new InvalidOperationException("send failed"));
        coordinator.AppendDeferredStep(() => events.Add("after"));
        SwarmCombatPublicationCoordinator.PublicationPlan plan = capture.Freeze();

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => coordinator.DispatchAndRetire(turn, plan));

        Assert.Equal("send failed", failure.Message);
        Assert.Equal(["before"], events);
        Assert.False(coordinator.Inspect(matchingId)?.HasActiveTurn);
        using SwarmCombatPublicationCoordinator.PublicationTurn next =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginRealtimeTurn(matchingId));
    }

    [Fact]
    public void BestEffortFailure_SkipsItsGroup_AndContinuesFollowingSteps()
    {
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(61_010);
        using SwarmCombatPublicationCoordinator.PublicationTurn turn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginRealtimeTurn(61_010));
        var events = new List<string>();
        var failures = new List<string>();
        using SwarmCombatPublicationCoordinator.CaptureScope capture = coordinator.BeginCapture(turn);
        coordinator.AppendDeferredStep(() => events.Add("before"));
        using (coordinator.BeginBestEffortGroup(failure => failures.Add(failure.Message)))
        {
            coordinator.AppendDeferredStep(() => events.Add("group-start"));
            coordinator.AppendDeferredStep(() => throw new InvalidOperationException("bot send failed"));
            coordinator.AppendDeferredStep(() => events.Add("group-skipped"));
        }
        coordinator.AppendDeferredStep(() => events.Add("after"));
        SwarmCombatPublicationCoordinator.PublicationPlan plan = capture.Freeze();

        coordinator.DispatchAndRetire(turn, plan);

        Assert.Equal(["before", "group-start", "after"], events);
        Assert.Equal(["bot send failed"], failures);
    }

    [Fact]
    public async Task DisposeDuringDispatch_DoesNotReleaseSameMatchTurn()
    {
        const long matchingId = 61_011;
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(matchingId);
        SwarmCombatPublicationCoordinator.PublicationTurn turn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginRealtimeTurn(matchingId));
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
            Assert.Null(coordinator.TryBeginRealtimeTurn(matchingId));
        }
        finally
        {
            releaseDispatch.Set();
        }

        await dispatch.WaitAsync(TimeSpan.FromSeconds(2));
        using SwarmCombatPublicationCoordinator.PublicationTurn next =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginRealtimeTurn(matchingId));
    }

    [Fact]
    public void RetiredPlan_IsRejectedWithoutReplay()
    {
        const long matchingId = 61_012;
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(matchingId);
        SwarmCombatPublicationCoordinator.PublicationTurn turn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginRealtimeTurn(matchingId));
        var events = new List<string>();
        using SwarmCombatPublicationCoordinator.CaptureScope capture = coordinator.BeginCapture(turn);
        coordinator.AppendDeferredStep(() => events.Add("replayed"));
        SwarmCombatPublicationCoordinator.PublicationPlan plan = capture.Freeze();

        turn.Dispose();

        Assert.Throws<InvalidOperationException>(() => coordinator.DispatchAndRetire(turn, plan));
        Assert.Empty(events);
        using SwarmCombatPublicationCoordinator.PublicationTurn next =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginRealtimeTurn(matchingId));
    }

    [Fact]
    public void Replay_IsNotCapturedByAnotherAmbientMatchCapture()
    {
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(61_013, 61_014);
        var directlySent = new List<byte[]>();
        SwarmCombatPublicationCoordinator.PacketRecipient? recipient = null;
        recipient = new SwarmCombatPublicationCoordinator.PacketRecipient(packet =>
        {
            if (!coordinator.TryCapturePacket(recipient!, packet))
                directlySent.Add(packet.ToBytes());
        });

        using SwarmCombatPublicationCoordinator.PublicationTurn firstTurn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginRealtimeTurn(61_013));
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
                coordinator.TryBeginRealtimeTurn(61_014));
        using SwarmCombatPublicationCoordinator.CaptureScope secondCapture =
            coordinator.BeginCapture(secondTurn);

        coordinator.DispatchAndRetire(firstTurn, firstPlan);

        Assert.Single(directlySent);
        Assert.Equal(expected, directlySent[0]);
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
                coordinator.TryBeginRealtimeTurn(matchingId));
        Task<SwarmCombatPublicationCoordinator.PublicationTurn?> waiting =
            Task.Run(() => coordinator.BeginRequiredTurn(matchingId));
        Assert.True(SpinWait.SpinUntil(
            () => coordinator.Inspect(matchingId)?.RequiredWaiterCount == 1,
            TimeSpan.FromSeconds(2)));

        coordinator.ClearMatching(matchingId);

        Assert.Null(await waiting.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Null(coordinator.Inspect(matchingId));
        Assert.Null(coordinator.TryBeginRealtimeTurn(matchingId));
        Assert.Null(coordinator.BeginRequiredTurn(matchingId));

        active.Dispose();
        Assert.Null(coordinator.Inspect(matchingId));
        Assert.Null(coordinator.TryBeginRealtimeTurn(matchingId));
    }

    [Fact]
    public void Begin_FailsClosedUntilMatchRegistration()
    {
        const long matchingId = 61_016;
        var coordinator = new SwarmCombatPublicationCoordinator();

        Assert.Null(coordinator.TryBeginRealtimeTurn(matchingId));
        Assert.Null(coordinator.BeginRequiredTurn(matchingId));
        Assert.True(coordinator.RegisterMatching(matchingId));
        Assert.False(coordinator.RegisterMatching(matchingId));

        using SwarmCombatPublicationCoordinator.PublicationTurn turn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginRealtimeTurn(matchingId));
    }

    private static SwarmCombatPublicationCoordinator CreateCoordinator(params long[] matchingIds)
    {
        var coordinator = new SwarmCombatPublicationCoordinator();
        foreach (long matchingId in matchingIds)
            Assert.True(coordinator.RegisterMatching(matchingId));
        return coordinator;
    }
}
