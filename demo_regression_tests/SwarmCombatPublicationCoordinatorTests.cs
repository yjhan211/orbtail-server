using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using game_server.services;
using network.common;
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
    public void DefaultFailure_PreservesPreparedState_AbortsRemainingPlan_AndRetiresTurn()
    {
        const long matchingId = 61_009;
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(matchingId);
        using SwarmCombatPublicationCoordinator.PublicationTurn turn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginRealtimeTurn(matchingId));
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
                coordinator.TryBeginRealtimeTurn(matchingId));
    }

    [Fact]
    public void BestEffortFailure_PreservesPreparedState_SkipsGroup_AndContinuesPlan()
    {
        SwarmCombatPublicationCoordinator coordinator = CreateCoordinator(61_010);
        using SwarmCombatPublicationCoordinator.PublicationTurn turn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginRealtimeTurn(61_010));
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
                coordinator.TryBeginRealtimeTurn(matchingId));
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
                coordinator.TryBeginRealtimeTurn(matchingId));
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
                   coordinator.TryBeginRealtimeTurn(matchingId)))
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
                coordinator.TryBeginRealtimeTurn(matchingId));
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

    [Fact]
    public void CaptureDelegate_WithoutFrameReturnsFalseWithoutMutatingPacket()
    {
        var coordinator = new SwarmCombatPublicationCoordinator();
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
                coordinator.TryBeginRealtimeTurn(matchingId));
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
        var coordinator = new SwarmCombatPublicationCoordinator();
        var registry = new MatchRuntimeRegistry();
        registry.SetRuntimeInitializer(id => Assert.True(coordinator.RegisterMatching(id)));
        IDisposable operation = Assert.IsAssignableFrom<IDisposable>(
            registry.TryAcquireOperation(matchingId, static () => { }));
        SwarmCombatPublicationCoordinator.PublicationTurn turn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginRealtimeTurn(matchingId));

        Assert.True(registry.TryFinalize(
            matchingId,
            static () => true,
            () => coordinator.ClearMatching(matchingId)));
        Assert.NotNull(coordinator.Inspect(matchingId));

        turn.Dispose();
        operation.Dispose();

        Assert.Null(coordinator.Inspect(matchingId));
        Assert.Null(coordinator.TryBeginRealtimeTurn(matchingId));
    }

    [Fact]
    public void RuntimeLease_PublishesTerminalOnlyAfterCombatRetiresAndLeaseReleases()
    {
        const long matchingId = 61_030;
        var events = new List<string>();
        var coordinator = new SwarmCombatPublicationCoordinator();
        var registry = new MatchRuntimeRegistry();
        registry.SetRuntimeInitializer(id => Assert.True(coordinator.RegisterMatching(id)));
        Assert.True(registry.TryExecute(matchingId, static () => { }));
        SwarmCombatPublicationCoordinator.PublicationTurn turn =
            Assert.IsType<SwarmCombatPublicationCoordinator.PublicationTurn>(
                coordinator.TryBeginRealtimeTurn(matchingId));
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
    public void IntegrationSeam_WiresLifecycleSendFallbackAndActivatedEntrypoints()
    {
        string repositoryRoot = FindRepositoryRoot();
        string server = ReadNormalizedSource(repositoryRoot, "game_server", "GameServer.cs");
        string session = ReadNormalizedSource(
            repositoryRoot, "game_server", "Network", "GameClientSession.cs");
        string registry = ReadNormalizedSource(
            repositoryRoot, "game_server", "Services", "MatchRuntimeRegistry.cs");

        Assert.Contains(
            "private readonly SwarmCombatPublicationCoordinator _swarmCombatPublicationCoordinator = new();",
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
        string initialization = ReadMethodSlice(
            server,
            "private void InitializeServices()",
            "private void StartTcpServer()");
        AssertInOrder(
            initialization,
            "_matchRuntimeRegistry.SetRuntimeInitializer(",
            "RegisterCombatPublicationMatching",
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
            "private void RegisterCombatPublicationMatching(long matchingId)",
            "private bool IsSwarmFrontOrbDamaged(");
        AssertInOrder(
            registration,
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
        string publicationHelper = ReadMethodSlice(
            proximity,
            "private void PrepareAndDispatchCombatPublication(",
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
            "_swarmCombatPublicationCoordinator.TryBeginRealtimeTurn(matchingId)",
            "if (publicationTurn == null)",
            "PrepareAndDispatchCombatPublication(",
            "() => ProcessProximityAutoCombatForMatching(matchingId, activeSessions)");
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
            publicationHelper,
            "_matchRuntimeRegistry.TryAcquireOperation(",
            "SwarmCombatPublicationCoordinator.CaptureScope? capture = null;",
            "_swarmCombatPublicationCoordinator.BeginCapture(publicationTurn)",
            "prepare();",
            "preparationFailure = ExceptionDispatchInfo.Capture(ex);",
            "publicationPlan = capture.Freeze();",
            "_swarmCombatPublicationCoordinator.DispatchAndRetire(",
            "publicationTurn.Dispose();",
            "runtimeOperation?.Dispose();",
            "pendingFailure?.Throw();");
        int acquisitionStart = publicationHelper.IndexOf(
            "_matchRuntimeRegistry.TryAcquireOperation(",
            StringComparison.Ordinal);
        int acquiredLeaseBranch = publicationHelper.IndexOf(
            "if (runtimeOperation != null)",
            acquisitionStart,
            StringComparison.Ordinal);
        Assert.True(acquisitionStart >= 0 && acquiredLeaseBranch > acquisitionStart);
        string acquisitionCallback =
            publicationHelper[acquisitionStart..acquiredLeaseBranch];
        AssertInOrder(
            acquisitionCallback,
            "CaptureScope? capture = null;",
            "capture = _swarmCombatPublicationCoordinator.BeginCapture(publicationTurn);",
            "prepare();",
            "preparationFailure = ExceptionDispatchInfo.Capture(ex);",
            "publicationPlan = capture.Freeze();",
            "if (preparationFailure != null && pendingFailure == null)",
            "pendingFailure = preparationFailure;",
            "RecordFailure(ex);",
            "finally",
            "capture?.Dispose();");
        Assert.DoesNotContain("throw;", acquisitionCallback);
        Assert.DoesNotContain("throw ", acquisitionCallback);
        AssertInOrder(
            publicationHelper,
            "publicationTurn.Dispose();",
            "runtimeOperation?.Dispose();",
            "if (ReferenceEquals(pendingFailure, preparationFailure))",
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
        Assert.Equal(1, CountOccurrences(proximity, ".TryBeginRealtimeTurn("));
        Assert.Equal(1, CountOccurrences(settlement, ".BeginRequiredTurn("));
        Assert.Equal(1, CountOccurrences(proximity, ".BeginCapture("));
        Assert.Equal(1, CountOccurrences(proximity, ".DispatchAndRetire("));
    }

    private static SwarmCombatPublicationCoordinator CreateCoordinator(params long[] matchingIds)
    {
        var coordinator = new SwarmCombatPublicationCoordinator();
        foreach (long matchingId in matchingIds)
            Assert.True(coordinator.RegisterMatching(matchingId));
        return coordinator;
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
