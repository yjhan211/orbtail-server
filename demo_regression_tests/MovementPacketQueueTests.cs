using System.Reflection;
using System.Threading.Channels;
using game_server.services;
using game_server.sessions;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;
using network.core;
using network.gameentry;
using network.packets;
using network.routing;

namespace demo_regression_tests;

public sealed class MovementPacketQueueTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task GameSessionPacketEntry_KeepsFinalStopBeforeAction_AndRejectsInvalidReplacement()
    {
        var clock = new ManualClock();
        var session = CreateSession(clock);
        var router = (ProtocolRouter)typeof(SessionBase).GetField("ProtocolRouter",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        var positions = new List<float>();
        bool actionProcessed = false;
        router.RegisterHandler(Protocol.C_TO_G_MOVE, bytes =>
        {
            positions.Add(MessagePackSerializer.Deserialize<C_TO_G_MOVE>(bytes).Position.X);
            return Task.CompletedTask;
        });
        router.RegisterHandler(Protocol.C_TO_G_PLAYER_STATE, _ =>
        {
            Assert.Equal(new[] { 1f, 3f }, positions);
            actionProcessed = true;
            return Task.CompletedTask;
        });

        Task Move(uint sequence, float position) => Send(session, Protocol.C_TO_G_MOVE,
            new C_TO_G_MOVE { InputSequence = sequence, Position = new Vector3f(position, 0, 0), Velocity = new Vector3f() });

        await Move(1, 1);
        Task pending = Move(2, 2);
        await clock.NextDelayAsync();
        Task stop = Move(3, 3);
        await Move(4, float.NaN); // 정상 정지 요청을 잘못된 위치로 덮어쓰지 않는다.
        Task action = Send(session, Protocol.C_TO_G_PLAYER_STATE, new C_TO_G_PLAYER_STATE { State = PlayerState.SLEEP });
        Assert.False(pending.IsCompleted);
        Assert.False(stop.IsCompleted);
        Assert.False(action.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(10));
        await Task.WhenAll(pending, stop, action).WaitAsync(TestTimeout);
        Assert.Equal(new[] { 1f, 3f }, positions);
        Assert.True(actionProcessed);
    }

    private static Task Send<T>(GameClientSession session, Protocol protocol, T body)
    {
        using var packet = Packet.Create((int)protocol);
        packet.SetBody(MessagePackSerializer.Serialize(body));
        packet.RecordSize();
        return session.OnMessageFromClient(packet.ToBytes());
    }

    private static GameClientSession CreateSession(ManualClock clock)
    {
        // 소켓 없이 실제 SessionBase 파싱·GameClientSession 스케줄링·라우팅 경계를 검사한다.
        // 이동 판정 자체 대신 기록 핸들러를 등록하여 맵 파일과 실제 시간에 의존하지 않는다.
        var connection = new TcpConnection();
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var active = (int)typeof(TcpConnection).GetField("StateActive", flags)!.GetRawConstantValue()!;
        typeof(TcpConnection).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(connection, active);
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var logs = TestGameEventLogs.Create();
        var session = new GameClientSession(
            connection,
            NullLogger.Instance,
            null!,
            TestGameSessionServices.CreateLeaveHandler(),
            static (_, _) => null,

            logs,
            TestGameSessionServices.CreateEliminationService(store, logs,
                new MatchSummaryFileStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))),
                GameServerDevOptions.Disabled, NullLogger.Instance),
            new FakePlayerGrowthHandler(),

            new FakeGameSessionLifecycle(),
            static () => false,
            new FakeMatchEntryFailureHandler(),
            matchEntry: TestGameSessionServices.CreateEntryService(null!, store, GameServerDevOptions.Disabled, NullLogger.Instance),
            movementValidation: new MovementValidationService(NullLogger<MovementValidationService>.Instance),
            movementTimeProvider: clock);
        connection.SetSession(session);
        return session;
    }

    [Fact]
    public async Task FinalStopIsAppliedWithoutAnotherPacket_AndBurstKeepsOnlyLatestPosition()
    {
        var clock = new ManualClock();
        var queue = new MovementPacketQueue(() => true, clock);
        var positions = new List<(int Position, int Velocity)>();
        Task Send(uint sequence, int position, int velocity) => queue.EnqueueAsync(() =>
        {
            positions.Add((position, velocity));
            return Task.CompletedTask;
        }, sequence);

        await Send(1, 10, 6);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Task pending = Send(2, 11, 6);
        Assert.Equal(TimeSpan.FromMilliseconds(9), await clock.NextDelayAsync());
        Task newer = Send(3, 12, 6);
        // 클라이언트의 정지 통지는 직전 이동과 같은 입력 순번일 수도 있다.
        Task stop = Send(3, 13, 0);
        Assert.Same(pending, newer);
        Assert.Same(pending, stop);
        Assert.False(stop.IsCompleted);
        Assert.Equal(new[] { (10, 6) }, positions);

        clock.Advance(TimeSpan.FromMilliseconds(9));
        await Task.WhenAll(pending, newer, stop).WaitAsync(TestTimeout);
        Assert.Equal(new[] { (10, 6), (13, 0) }, positions);
    }

    [Fact]
    public async Task MovementCannotBeCoalescedAcrossAnActionPacket()
    {
        var clock = new ManualClock();
        var queue = new MovementPacketQueue(() => true, clock);
        var calls = new List<string>();
        Task Send(string name, uint? sequence) => queue.EnqueueAsync(() =>
        {
            calls.Add(name);
            return Task.CompletedTask;
        }, sequence);

        await Send("move1", 1);
        Task stop = Send("stop", 2);
        await clock.NextDelayAsync();
        Task action = Send("explore", null);
        Task nextMove = Send("move3", 3);
        Task latest = Send("move4", 4);
        Assert.NotSame(stop, nextMove);
        Assert.Same(nextMove, latest);

        clock.Advance(TimeSpan.FromMilliseconds(10));
        await Task.WhenAll(stop, action).WaitAsync(TestTimeout);
        Assert.Equal(new[] { "move1", "stop", "explore" }, calls);
        await clock.NextDelayAsync();
        clock.Advance(TimeSpan.FromMilliseconds(10));
        await latest.WaitAsync(TestTimeout);
        Assert.Equal(new[] { "move1", "stop", "explore", "move4" }, calls);
    }

    [Fact]
    public async Task OldSequenceCannotReplaceLatestPendingMove_AndWrapAroundIsAllowed()
    {
        var clock = new ManualClock();
        var queue = new MovementPacketQueue(() => true, clock);
        var sequences = new List<uint>();
        Task Send(uint sequence) => queue.EnqueueAsync(() =>
        {
            sequences.Add(sequence);
            return Task.CompletedTask;
        }, sequence);

        await Send(uint.MaxValue - 1);
        Task pending = Send(uint.MaxValue);
        await clock.NextDelayAsync();
        Task wrapped = Send(0);
        Task stale = Send(uint.MaxValue - 2);
        Assert.Same(pending, wrapped);
        Assert.Same(wrapped, stale);
        clock.Advance(TimeSpan.FromMilliseconds(10));
        await stale.WaitAsync(TestTimeout);
        Assert.Equal(new[] { uint.MaxValue - 1, 0u }, sequences);
    }

    [Fact]
    public async Task DisconnectSkipsPendingMoveAndAction_ButCompletesTheirTasks()
    {
        var clock = new ManualClock();
        bool connected = true;
        int processed = 0;
        var queue = new MovementPacketQueue(() => connected, clock);
        Task Dispatch() { processed++; return Task.CompletedTask; }
        await queue.EnqueueAsync(Dispatch, 1);
        Task stop = queue.EnqueueAsync(Dispatch, 2);
        await clock.NextDelayAsync();
        Task action = queue.EnqueueAsync(Dispatch);
        connected = false;
        Assert.False(stop.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(10));
        await Task.WhenAll(stop, action).WaitAsync(TestTimeout);
        Assert.Equal(1, processed);
    }

    [Fact]
    public async Task IntervalStartsAfterHandlerCompletes_AndEarlyTimerDoesNotProcessTooSoon()
    {
        var clock = new ManualClock();
        var queue = new MovementPacketQueue(() => true, clock);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task first = queue.EnqueueAsync(() => release.Task, 1);
        bool processed = false;
        Task stop = queue.EnqueueAsync(() => { processed = true; return Task.CompletedTask; }, 2);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(processed);
        release.SetResult();
        await first.WaitAsync(TestTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(10), await clock.NextDelayAsync());
        clock.FireNextTimerEarly();
        Assert.Equal(TimeSpan.FromMilliseconds(10), await clock.NextDelayAsync());
        Assert.False(processed);
        clock.Advance(TimeSpan.FromMilliseconds(10));
        await stop.WaitAsync(TestTimeout);
        Assert.True(processed);
    }

    [Fact]
    public async Task HandlerFailureIsObservedAndDoesNotStrandFollowingRequests()
    {
        var clock = new ManualClock();
        var queue = new MovementPacketQueue(() => true, clock);
        await queue.EnqueueAsync(() => Task.CompletedTask, 1);
        Task failure = queue.EnqueueAsync(() => Task.FromException(new InvalidOperationException("test")), 2);
        await clock.NextDelayAsync();
        bool actionProcessed = false;
        Task action = queue.EnqueueAsync(() => { actionProcessed = true; return Task.CompletedTask; });
        clock.Advance(TimeSpan.FromMilliseconds(10));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failure.WaitAsync(TestTimeout));
        await action.WaitAsync(TestTimeout);
        Assert.True(actionProcessed);
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private readonly Channel<TimeSpan> _scheduled = Channel.CreateUnbounded<TimeSpan>();
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public async Task<TimeSpan> NextDelayAsync() =>
            await _scheduled.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            Interlocked.Add(ref _ticks, elapsed.Ticks);
            ManualTimer[] due;
            lock (_gate)
            {
                due = _timers.Where(timer => timer.Due <= GetTimestamp()).ToArray();
                foreach (var timer in due) _timers.Remove(timer);
            }
            foreach (var timer in due) timer.Fire();
        }

        public void FireNextTimerEarly()
        {
            ManualTimer timer;
            lock (_gate) { timer = _timers[0]; _timers.RemoveAt(0); }
            timer.Fire();
        }

        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public long Due { get; private set; }
            public void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate)
                {
                    clock._timers.Remove(this);
                    Due = clock.GetTimestamp() + dueTime.Ticks;
                    clock._timers.Add(this);
                    clock._scheduled.Writer.TryWrite(dueTime);
                }
                return true;
            }
            public void Dispose() { lock (clock._gate) clock._timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
