using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.core;
using network.packets;
using user_server.sessions;
using user_server.matching;
using user_server.network;

namespace demo_regression_tests;

/// <summary>
///     Core NATS의 session.clear 유실 시 GameSession이 Redis claim을 정본으로 로컬 배정을 복구하는 경로.
/// </summary>
public sealed class UserServerMatchingReconciliationTests
{
    [Fact]
    public async Task MissingClaim_ClearsStaleAssignmentAndStartsNewRequest()
    {
        var matching = new RecordingMatchingManager();
        using var connection = new ActiveTcpConnection();
        var session = NewSession(connection.Connection, matching);

        string firstRequest = Assert.IsType<string>(await session.TryBeginMatchingRequestAsync(7));
        DeliverMatchingSuccess(session, 42, firstRequest);

        string secondRequest = Assert.IsType<string>(await session.TryBeginMatchingRequestAsync(7));

        Assert.NotEqual(firstRequest, secondRequest);
        Assert.Equal(secondRequest, session.ActiveMatchingRequestId);
        Assert.Equal(1, matching.ClaimReadCount);
    }

    [Fact]
    public async Task ExistingClaim_PreservesAssignmentAndRejectsNewRequest()
    {
        var matching = new RecordingMatchingManager();
        using var connection = new ActiveTcpConnection();
        var session = NewSession(connection.Connection, matching);

        string firstRequest = Assert.IsType<string>(await session.TryBeginMatchingRequestAsync(7));
        DeliverMatchingSuccess(session, 42, firstRequest);
        matching.HasClaim = true;

        Assert.Null(await session.TryBeginMatchingRequestAsync(7));
        Assert.Equal(firstRequest, session.ActiveMatchingRequestId);
        Assert.Equal(1, matching.ClaimReadCount);
    }

    [Fact]
    public async Task ConcurrentClearAndNewRequest_AreNotOverwrittenByOlderReconciliation()
    {
        var matching = new RecordingMatchingManager();
        using var connection = new ActiveTcpConnection();
        var session = NewSession(connection.Connection, matching);

        string firstRequest = Assert.IsType<string>(await session.TryBeginMatchingRequestAsync(7));
        DeliverMatchingSuccess(session, 42, firstRequest);
        matching.ClaimCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task<string?> delayedReconciliation = session.TryBeginMatchingRequestAsync(7);
        ((IMatchingSessionEndpoint)session).ClearMatchingAssignment(42);
        string concurrentRequest = Assert.IsType<string>(await session.TryBeginMatchingRequestAsync(7));
        matching.ClaimCompletion.SetResult(false);

        Assert.Null(await delayedReconciliation);
        Assert.Equal(concurrentRequest, session.ActiveMatchingRequestId);
    }

    [Fact]
    public async Task ClaimReadFailure_FailsClosedWithoutClearingAssignment()
    {
        var matching = new RecordingMatchingManager();
        using var connection = new ActiveTcpConnection();
        var session = NewSession(connection.Connection, matching);

        string firstRequest = Assert.IsType<string>(await session.TryBeginMatchingRequestAsync(7));
        DeliverMatchingSuccess(session, 42, firstRequest);
        matching.ClaimError = new InvalidOperationException("redis down");

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.TryBeginMatchingRequestAsync(7));
        Assert.Equal(firstRequest, session.ActiveMatchingRequestId);
    }

    private static void DeliverMatchingSuccess(GameSession session, long matchingId, string requestId)
    {
        using var packet = PacketMaker.U_TO_C_MATCHING_SUCCESS(
            matchingId, "127.0.0.1", 9001, 0, "test-ticket", []);
        Assert.True(((IMatchingSessionEndpoint)session).TryDeliverMatchingSuccess(
            matchingId, requestId, packet));
    }

    private static GameSession NewSession(TcpConnection connection, IMatchingManager matchingManager)
    {
        return new GameSession(
            connection,
            NullLogger.Instance,
            new InMemoryRedisOperations(),
            new FakeRedLockFactory(),
            null!,
            matchingManager,
            null!,
            null!,
            "user-server-test",
            static (_, _) => (true, null),
            static (_, _) => { },
            static (_, _) => true);
    }

    private sealed class RecordingMatchingManager : IMatchingManager
    {
        public bool HasClaim { get; set; }
        public Exception? ClaimError { get; set; }
        public TaskCompletionSource<bool>? ClaimCompletion { get; set; }
        public int ClaimReadCount { get; private set; }

        public Task<bool> HasMatchingClaimAsync(long playerId)
        {
            ClaimReadCount++;
            if (ClaimError != null)
                return Task.FromException<bool>(ClaimError);
            return ClaimCompletion?.Task ?? Task.FromResult(HasClaim);
        }

        public Task<ErrorCode> AddToQueue(long playerId, GameSession session) =>
            Task.FromResult(ErrorCode.SUCCESS);

        public Task<ErrorCode> CancelMatching(long playerId) => Task.FromResult(ErrorCode.SUCCESS);
        public Task RecordGameCompletionAsync(long playerId, long matchingId) => Task.CompletedTask;
        public Task RecordLeaveAsync(long playerId, long matchingId) => Task.CompletedTask;
        public Task AbortMatchingAdmissionAsync(long playerId, long matchingId) => Task.CompletedTask;
        public Task ReleaseMatchingClaimAsync(long playerId, long matchingId) => Task.CompletedTask;
        public bool TryRunBackgroundOperation(Func<Task> operation, string operationName) => false;
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
