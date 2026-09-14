using System.Net;
using System.Net.Sockets;
using System.Reflection;
using MessagePack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;
using network.core;
using network.packets;
using user_server.matching;
using user_server.players;
using user_server.sessions;

namespace demo_regression_tests;

/// <summary>
///     Core NATS의 session.clear 유실 시 PlayerSession이 Redis reservation을 정본으로 로컬 배정을 복구하는 경로.
/// </summary>
public sealed class UserServerMatchingReconciliationTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public async Task ItemRequest_ChecksLeaseBeforeCallingPlayerService(bool useItem, bool current, bool lookupError)
    {
        using var connection = new ActiveTcpConnection();
        var store = new FailingLeaseStore { Current = current, CheckError = lookupError };
        var playerService = new RecordingPlayerService();
        var session = NewSession(connection.Connection, new RecordingMatchingManager(), store, playerService: playerService);
        typeof(PlayerSession).GetProperty("PlayerId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, 7L);
        SetField(session, "_sessionLease", new PlayerSessionLease(7, "node", "session", 1, "owner"));

        if (useItem)
            await Receive(session, Protocol.C_TO_U_USE_ITEM, new C_TO_U_USE_ITEM());
        else
            await Receive(session, Protocol.C_TO_U_WEAR_ITEM, new C_TO_U_WEAR_ITEM());

        Assert.Equal(current ? 1 : 0, playerService.Calls);
        if (current) Assert.Equal(7L, playerService.PlayerId);
    }

    private sealed class RecordingPlayerService : IPlayerService
    {
        public int Calls { get; private set; }
        public long PlayerId { get; private set; }
        public Task<(ErrorCode, PlayerInfo?)> WearItem(long playerId, C_TO_U_WEAR_ITEM msg) => Record(playerId);
        public Task<(ErrorCode, PlayerInfo?)> UseItem(long playerId, C_TO_U_USE_ITEM msg) => Record(playerId);
        private Task<(ErrorCode, PlayerInfo?)> Record(long playerId)
        {
            Calls++;
            PlayerId = playerId;
            return Task.FromResult<(ErrorCode, PlayerInfo?)>((ErrorCode.PLAYER_NOT_FOUND, null));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MatchingRequest_InvalidLeaseOrLookupErrorDoesNotChangeQueue(bool cancel, bool lookupError)
    {
        using var connection = new ActiveTcpConnection();
        var matching = new RecordingMatchingManager();
        var store = new FailingLeaseStore { CheckError = lookupError };
        var session = NewSession(connection.Connection, matching, store);
        typeof(PlayerSession).GetProperty("PlayerId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, 7L);
        SetField(session, "_sessionLease", new PlayerSessionLease(7, "node", "session", 1, "owner"));

        if (cancel)
            await Receive(session, Protocol.C_TO_U_MATCHING_CANCEL, new C_TO_U_MATCHING());
        else
            await Receive(session, Protocol.C_TO_U_MATCHING, new C_TO_U_MATCHING());

        Assert.Equal(0, matching.AddCount);
        Assert.Equal(0, matching.CancelCount);
        Assert.Null(session.ActiveMatchingRequestId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_LeaseReleaseFailureStillCleansMatching(bool assigned)
    {
        var matching = new RecordingMatchingManager();
        using var connection = new ActiveTcpConnection();
        var session = NewSession(connection.Connection, matching, new FailingLeaseStore());
        SetField(session, "_sessionLease", new PlayerSessionLease(7, "node", "session", 1, "owner"));
        if (assigned)
        {
            string request = Assert.IsType<string>(await session.TryBeginMatchingRequestAsync(7));
            DeliverMatchingSuccess(session, 42, request);
        }

        await (Task)Invoke(session, "CleanupRemovedSessionAsync", 7L, true)!;

        Assert.Equal(assigned ? 1 : 0, matching.ReleaseCount);
        Assert.Equal(assigned ? 0 : 1, matching.CancelCount);
    }

    [Fact]
    public async Task Cleanup_ReleaseFailureStillCancelsMatchingWithoutRenewalTask()
    {
        var matching = new RecordingMatchingManager();
        var store = new FailingLeaseStore();
        var session = NewSession(new TcpConnection(), matching, store);
        SetField(session, "_sessionLease", new PlayerSessionLease(7, "node", "session", 1, "owner"));

        await (Task)Invoke(session, "CleanupRemovedSessionAsync", 7L, true)!;

        Assert.Equal(1, store.ReleaseCount);
        Assert.Equal(1, matching.CancelCount);
    }

    [Fact]
    public void ClosedConnection_LoginRegistrationReturnsFalseWithoutServerError()
    {
        var logger = new RecordingLogger();
        var session = NewSession(new TcpConnection(), new RecordingMatchingManager(), logger: logger);
        object?[] arguments = [null];

        Assert.False((bool)Invoke(session, "TryRegisterLocalSession", arguments)!);
        Assert.Null(arguments[0]);
        Assert.True(logger.Contains(LogLevel.Debug, "Connection closed before login entry"));
        Assert.False(logger.Contains(LogLevel.Error, "Login"));
    }

    private static async Task Receive<T>(PlayerSession session, Protocol protocol, T message)
    {
        using var packet = Packet.Create((int)protocol);
        packet.SetBody(MessagePackSerializer.Serialize(message));
        await session.OnMessageFromClient(packet.ToBytes());
    }

    [Theory]
    [InlineData(Protocol.C_TO_U_LOGIN)]
    [InlineData(Protocol.C_TO_U_HEART_BEAT)]
    public async Task LoginAndPreLoginHeartbeat_DoNotRenewLease(Protocol protocol)
    {
        using var connection = new ActiveTcpConnection();
        var store = new FailingLeaseStore { CheckError = true };
        var session = NewSession(connection.Connection, new RecordingMatchingManager(), store);
        // 로그인은 이미 인증된 경우의 응답 경로를 사용하며 lease 검사는 생략되어야 한다.
        typeof(PlayerSession).GetProperty("PlayerId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, 7L);

        await Receive(session, protocol, new C_TO_U_LOGIN());

        Assert.Equal(0, store.CheckCount);
    }

    [Fact]
    public async Task QueuedRequest_ChecksLeaseOnlyAfterPreviousRequestReleasesSessionLock()
    {
        using var connection = new ActiveTcpConnection();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FailingLeaseStore { FirstCheck = completion.Task };
        var playerService = new RecordingPlayerService();
        var session = NewSession(connection.Connection, new RecordingMatchingManager(), store, playerService: playerService);
        typeof(PlayerSession).GetProperty("PlayerId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, 7L);
        SetField(session, "_sessionLease", new PlayerSessionLease(7, "node", "session", 1, "owner"));

        var first = Receive(session, Protocol.C_TO_U_WEAR_ITEM, new C_TO_U_WEAR_ITEM());
        var second = Receive(session, Protocol.C_TO_U_USE_ITEM, new C_TO_U_USE_ITEM());
        Assert.Equal(1, store.CheckCount);
        completion.SetResult(true);
        await Task.WhenAll(first, second);

        Assert.Equal(2, store.CheckCount);
        Assert.Equal(1, playerService.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AuthenticatedHeartbeat_RenewsOnlyCurrentSession(bool current)
    {
        using var connection = new ActiveTcpConnection();
        var store = new FailingLeaseStore { Current = current };
        var session = NewSession(connection.Connection, new RecordingMatchingManager(), store);
        SetField(session, "_sessionLease", new PlayerSessionLease(7, "node", "session", 1, "owner"));

        await Receive(session, Protocol.C_TO_U_HEART_BEAT, new C_TO_U_LOGIN());

        Assert.Equal(1, store.RenewCount);
        Assert.Equal(current, connection.Connection.IsAcceptingMessages);
    }

    // 상태 준비와 private 경계 호출에만 사용해 프로덕션 API를 테스트용으로 넓히지 않는다.
    private static object? Invoke(PlayerSession session, string name, params object?[] args) =>
        typeof(PlayerSession).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, args);

    private static void SetField(PlayerSession session, string name, object value) =>
        typeof(PlayerSession).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, value);

    private sealed class FailingLeaseStore : IPlayerSessionLeaseStore
    {
        public bool CheckError { get; init; }
        public bool Current { get; init; }
        public Task<bool>? FirstCheck { get; init; }
        public int CheckCount { get; private set; }
        public int RenewCount { get; private set; }
        public int ReleaseCount { get; private set; }
        public TimeSpan LeaseLifetime => TimeSpan.FromSeconds(90);
        public Task<PlayerSessionLease?> TryAcquireAsync(long playerId, string nodeId, string sessionId) =>
            throw new NotSupportedException();
        public Task<bool> TryRenewAsync(PlayerSessionLease lease)
        {
            RenewCount++;
            CheckCount++;
            if (CheckCount == 1 && FirstCheck != null) return FirstCheck;
            return CheckError
                ? Task.FromException<bool>(new InvalidOperationException("Redis unavailable"))
                : Task.FromResult(Current);
        }
        public Task<bool> TryReleaseAsync(PlayerSessionLease lease)
        {
            ReleaseCount++;
            return Task.FromException<bool>(new InvalidOperationException("redis unavailable"));
        }
    }

    [Fact]
    public async Task MissingReservation_ClearsStaleAssignmentAndStartsNewRequest()
    {
        var matching = new RecordingMatchingManager();
        using var connection = new ActiveTcpConnection();
        var session = NewSession(connection.Connection, matching);

        string firstRequest = Assert.IsType<string>(await session.TryBeginMatchingRequestAsync(7));
        DeliverMatchingSuccess(session, 42, firstRequest);

        string secondRequest = Assert.IsType<string>(await session.TryBeginMatchingRequestAsync(7));

        Assert.NotEqual(firstRequest, secondRequest);
        Assert.Equal(secondRequest, session.ActiveMatchingRequestId);
        Assert.Equal(1, matching.ReservationReadCount);
    }

    [Fact]
    public async Task ExistingReservation_PreservesAssignmentAndRejectsNewRequest()
    {
        var matching = new RecordingMatchingManager();
        using var connection = new ActiveTcpConnection();
        var session = NewSession(connection.Connection, matching);

        string firstRequest = Assert.IsType<string>(await session.TryBeginMatchingRequestAsync(7));
        DeliverMatchingSuccess(session, 42, firstRequest);
        matching.HasReservation = true;

        Assert.Null(await session.TryBeginMatchingRequestAsync(7));
        Assert.Equal(firstRequest, session.ActiveMatchingRequestId);
        Assert.Equal(1, matching.ReservationReadCount);
    }

    [Fact]
    public async Task ConcurrentClearAndNewRequest_AreNotOverwrittenByOlderReconciliation()
    {
        var matching = new RecordingMatchingManager();
        using var connection = new ActiveTcpConnection();
        var session = NewSession(connection.Connection, matching);

        string firstRequest = Assert.IsType<string>(await session.TryBeginMatchingRequestAsync(7));
        DeliverMatchingSuccess(session, 42, firstRequest);
        matching.ReservationCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task<string?> delayedReconciliation = session.TryBeginMatchingRequestAsync(7);
        ((IMatchingSessionEndpoint)session).ClearMatchingAssignment(42);
        string concurrentRequest = Assert.IsType<string>(await session.TryBeginMatchingRequestAsync(7));
        matching.ReservationCompletion.SetResult(false);

        Assert.Null(await delayedReconciliation);
        Assert.Equal(concurrentRequest, session.ActiveMatchingRequestId);
    }

    [Fact]
    public async Task ReservationReadFailure_FailsClosedWithoutClearingAssignment()
    {
        var matching = new RecordingMatchingManager();
        using var connection = new ActiveTcpConnection();
        var session = NewSession(connection.Connection, matching);

        string firstRequest = Assert.IsType<string>(await session.TryBeginMatchingRequestAsync(7));
        DeliverMatchingSuccess(session, 42, firstRequest);
        matching.ReservationError = new InvalidOperationException("redis down");

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.TryBeginMatchingRequestAsync(7));
        Assert.Equal(firstRequest, session.ActiveMatchingRequestId);
    }

    private static void DeliverMatchingSuccess(PlayerSession session, long matchingId, string requestId)
    {
        using var packet = PacketMaker.U_TO_C_MATCHING_SUCCESS(
            matchingId, "127.0.0.1", 9001, 0, "test-ticket");
        Assert.True(((IMatchingSessionEndpoint)session).TryDeliverMatchingSuccess(
            matchingId, requestId, packet));
    }

    private static PlayerSession NewSession(TcpConnection connection, IMatchingManager matchingManager,
        IPlayerSessionLeaseStore? store = null, ILogger? logger = null, IPlayerService? playerService = null)
    {
        return new PlayerSession(
            connection,
            (logger ?? NullLogger.Instance).For<PlayerSession>(),
            new InMemoryRedisOperations(),
            new FakeRedLockFactory(),
            playerService!,
            matchingManager,
            null!,
            store!,
            "user-server-test",
            static (_, _) => (true, null),
            static (_, _) => { },
            static (_, _) => true,
            static (_, _) => false);
    }

    private sealed class RecordingMatchingManager : IMatchingManager
    {
        public bool HasReservation { get; set; }
        public Exception? ReservationError { get; set; }
        public TaskCompletionSource<bool>? ReservationCompletion { get; set; }
        public int ReservationReadCount { get; private set; }
        public int AddCount { get; private set; }
        public int CancelCount { get; private set; }
        public int ReleaseCount { get; private set; }

        public Task<bool> IsMatchingBlockedAsync(long playerId)
        {
            ReservationReadCount++;
            if (ReservationError != null)
                return Task.FromException<bool>(ReservationError);
            return ReservationCompletion?.Task ?? Task.FromResult(HasReservation);
        }

        public Task<ErrorCode> AddToQueue(long playerId, PlayerSession session)
        {
            AddCount++;
            return Task.FromResult(ErrorCode.SUCCESS);
        }

        public Task<ErrorCode> CancelMatching(long playerId)
        {
            CancelCount++;
            return Task.FromResult(ErrorCode.SUCCESS);
        }
        public Task HandleEntryFailureAsync(long playerId, long matchingId) => Task.CompletedTask;
        public Task ReleaseAssignmentAsync(long playerId, long matchingId)
        {
            ReleaseCount++;
            return Task.CompletedTask;
        }
        public Task StopMatchingLoopAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
    }

    private sealed class ActiveTcpConnection : IDisposable
    {
        private readonly Socket _client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        public ActiveTcpConnection()
        {
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            _client.Connect(listener.LocalEndPoint!);
            var socket = listener.Accept();
            var sendArgs = new SocketAsyncEventArgs();
            sendArgs.SetBuffer(new byte[Config.BUFFER_SIZE], 0, Config.BUFFER_SIZE);
            sendArgs.Completed += (_, args) => Connection.ProcessSend(args);
            Connection.InitializeConnection(
                socket,
                new SocketAsyncEventArgs(),
                sendArgs,
                static (connection, _, _) =>
                {
                    connection.CloseTransport(static _ => { });
                    connection.NotifySessionClosed(static _ => { });
                    connection.MarkClosePrepared();
                },
                static connection =>
                {
                    connection.DetachEventArgs(out SocketAsyncEventArgs? receive, out SocketAsyncEventArgs? send);
                    receive?.Dispose();
                    send?.Dispose();
                    connection.MarkReleased();
                });
        }

        public TcpConnection Connection { get; } = new();

        public void Dispose()
        {
            Connection.Disconnect();
            _client.Dispose();
            Assert.True(Connection.ReleaseTask.Wait(TimeSpan.FromSeconds(5)));
        }
    }
}
