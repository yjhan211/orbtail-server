using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.core;
using user_server.network;
using user_server.services;

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
        using var connection = new ActiveUserToken();
        var session = NewSession(connection.Token, matching);

        string firstRequest = Assert.IsType<string>(await session.TryBeginMatchingRequestAsync(7));
        Assert.True(session.TryAssignMatching(42, firstRequest));

        string secondRequest = Assert.IsType<string>(await session.TryBeginMatchingRequestAsync(7));

        Assert.NotEqual(firstRequest, secondRequest);
        Assert.Equal(secondRequest, session.ActiveMatchingRequestId);
        Assert.Equal(1, matching.ClaimReadCount);
    }

    [Fact]
    public async Task ExistingClaim_PreservesAssignmentAndRejectsNewRequest()
    {
        var matching = new RecordingMatchingManager();
        using var connection = new ActiveUserToken();
        var session = NewSession(connection.Token, matching);

        string firstRequest = Assert.IsType<string>(await session.TryBeginMatchingRequestAsync(7));
        Assert.True(session.TryAssignMatching(42, firstRequest));
        matching.HasClaim = true;

        Assert.Null(await session.TryBeginMatchingRequestAsync(7));
        Assert.Equal(firstRequest, session.ActiveMatchingRequestId);
        Assert.Equal(1, matching.ClaimReadCount);
    }

    [Fact]
    public async Task ConcurrentClearAndNewRequest_AreNotOverwrittenByOlderReconciliation()
    {
        var matching = new RecordingMatchingManager();
        using var connection = new ActiveUserToken();
        var session = NewSession(connection.Token, matching);

        string firstRequest = Assert.IsType<string>(await session.TryBeginMatchingRequestAsync(7));
        Assert.True(session.TryAssignMatching(42, firstRequest));
        matching.ClaimCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task<string?> delayedReconciliation = session.TryBeginMatchingRequestAsync(7);
        session.ClearMatchingAssignment(42);
        string concurrentRequest = Assert.IsType<string>(await session.TryBeginMatchingRequestAsync(7));
        matching.ClaimCompletion.SetResult(false);

        Assert.Null(await delayedReconciliation);
        Assert.Equal(concurrentRequest, session.ActiveMatchingRequestId);
    }

    [Fact]
    public async Task ClaimReadFailure_FailsClosedWithoutClearingAssignment()
    {
        var matching = new RecordingMatchingManager();
        using var connection = new ActiveUserToken();
        var session = NewSession(connection.Token, matching);

        string firstRequest = Assert.IsType<string>(await session.TryBeginMatchingRequestAsync(7));
        Assert.True(session.TryAssignMatching(42, firstRequest));
        matching.ClaimError = new InvalidOperationException("redis down");

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.TryBeginMatchingRequestAsync(7));
        Assert.Equal(firstRequest, session.ActiveMatchingRequestId);
    }

    private static GameSession NewSession(UserToken token, IMatchingManager matchingManager)
    {
        return new GameSession(
            token,
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
        public Task QuiesceAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
    }

    private sealed class ActiveUserToken : IDisposable
    {
        public ActiveUserToken()
        {
            Token.InitializeConnection(
                new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp),
                new SocketAsyncEventArgs(),
                new SocketAsyncEventArgs(),
                static (token, _, _) =>
                {
                    token.CloseTransport(static _ => { });
                    token.NotifySessionClosed(static _ => { });
                    token.MarkClosePrepared();
                },
                static token =>
                {
                    token.DetachEventArgs(out SocketAsyncEventArgs? receive, out SocketAsyncEventArgs? send);
                    receive?.Dispose();
                    send?.Dispose();
                    token.MarkReleased();
                });
        }

        public UserToken Token { get; } = new();

        public void Dispose()
        {
            Token.Disconnect();
        }
    }
}
