using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.infrastructure.messaging;
using network.packets;
using user_server.services;

namespace demo_regression_tests;

/// <summary>
///     #339 2단계 User Server N대: 매칭 리더 lease(획득·갱신·상실·해제), 세션 라우터(로컬 직행, 원격 request는 세션을
///     가진 프로세스만 응답, timeout 재시도는 첫 결과 재사용, 최종 부재는 false, 해제·로그인 알림은 origin 제외 브로드캐스트).
/// </summary>
public sealed class UserServerScaleOutTests
{
    [Fact]
    public async Task LeaderLease_OnlyOneNodeHoldsItAndRenewalKeepsIt()
    {
        var cache = new InMemoryRedisOperations();
        var a = new MatchingLeaderLease(cache, "user-server-0", new RecordingLogger());
        var b = new MatchingLeaderLease(cache, "user-server-1", new RecordingLogger());

        Assert.True(await a.TryAcquireOrRenewAsync());
        Assert.False(await b.TryAcquireOrRenewAsync());
        Assert.True(await a.TryAcquireOrRenewAsync()); // 갱신
        Assert.False(await b.TryAcquireOrRenewAsync());
        Assert.Equal("user-server-0", cache.GetString(MatchingLeaderLease.Key));
        Assert.Equal(MatchingLeaderLease.Lifetime, cache.Expiries[MatchingLeaderLease.Key]);
    }

    [Fact]
    public async Task LeaderLease_ReleaseHandsOverAndExpiryIsTakenByAnother()
    {
        var cache = new InMemoryRedisOperations();
        var a = new MatchingLeaderLease(cache, "user-server-0", new RecordingLogger());
        var b = new MatchingLeaderLease(cache, "user-server-1", new RecordingLogger());
        Assert.True(await a.TryAcquireOrRenewAsync());

        await a.ReleaseAsync();
        Assert.False(a.IsLeader);
        Assert.True(await b.TryAcquireOrRenewAsync());

        // b가 TTL 안에 갱신을 못 해 키가 사라진 상황: a가 가져가고 b는 다음 tick에 상실을 안다
        await cache.KeyDeleteAsync(MatchingLeaderLease.Key);
        Assert.True(await a.TryAcquireOrRenewAsync());
        Assert.False(await b.TryAcquireOrRenewAsync());
        Assert.False(b.IsLeader);
    }

    [Fact]
    public async Task LeaderLease_RedisFailureStandsDown()
    {
        var cache = new InMemoryRedisOperations();
        var logger = new RecordingLogger();
        var lease = new MatchingLeaderLease(cache, "user-server-0", logger);
        Assert.True(await lease.TryAcquireOrRenewAsync());

        cache.StringError = new InvalidOperationException("redis down");
        Assert.False(await lease.TryAcquireOrRenewAsync());
        Assert.False(lease.IsLeader);
        Assert.True(logger.Contains(LogLevel.Warning, "standing down"));
    }

    [Fact]
    public async Task Router_DeliversLocallyWithoutTouchingNats()
    {
        var bus = new InMemoryNatsBus();
        var session = new FakeSessionEndpoint();
        var router = new NatsPlayerSessionRouter(bus.Connect(), id => id == 7 ? session : null, "user-server-0", new RecordingLogger());
        router.Start();

        using Packet packet = PacketMaker.U_TO_C_MATCHING_FAILED(ErrorCode.MATCHING_FAILED, 42);
        Assert.True(await router.DeliverMatchingFailedAsync(7, 42, "req7", packet));

        Assert.Equal(("failed", 42L, "req7"), session.Deliveries.Single());
        Assert.Equal(0, bus.RequestCount);
    }

    [Fact]
    public async Task Router_DelegatesToTheProcessThatOwnsTheSession()
    {
        var bus = new InMemoryNatsBus();
        var owner = new FakeSessionEndpoint();
        var leader = new NatsPlayerSessionRouter(bus.Connect(), _ => null, "user-server-0", new RecordingLogger());
        var other = new NatsPlayerSessionRouter(bus.Connect(), id => id == 7 ? owner : null, "user-server-1", new RecordingLogger());
        var bystander = new NatsPlayerSessionRouter(bus.Connect(), _ => null, "user-server-2", new RecordingLogger());
        leader.Start();
        other.Start();
        bystander.Start();

        using Packet packet = PacketMaker.U_TO_C_MATCHING_FAILED(ErrorCode.MATCHING_FAILED, 42);
        Assert.True(await leader.DeliverMatchingFailedAsync(7, 42, "req7", packet));

        (string Op, long MatchingId, string RequestId) delivery = owner.Deliveries.Single();
        Assert.Equal(("failed", 42L, "req7"), delivery);
        Assert.Equal((int)Protocol.U_TO_C_MATCHING_FAILED, owner.LastPacketProtocolId);
        Assert.Equal(1, bus.RequestCount);
        Assert.Equal(1, bus.RepliesForLastRequest); // 세션 없는 두 프로세스는 침묵한다
    }

    [Fact]
    public async Task Router_RetriesRequestLostBeforeTheOwnerHandlesIt()
    {
        var bus = new InMemoryNatsBus { RequestsToDropBeforeHandling = 1 };
        var owner = new FakeSessionEndpoint();
        var leader = new NatsPlayerSessionRouter(bus.Connect(), _ => null, "user-server-0", new RecordingLogger());
        var other = new NatsPlayerSessionRouter(bus.Connect(), id => id == 7 ? owner : null, "user-server-1", new RecordingLogger());
        leader.Start();
        other.Start();

        using Packet packet = PacketMaker.U_TO_C_MATCHING_FAILED(ErrorCode.MATCHING_FAILED, 42);
        Assert.True(await leader.DeliverMatchingFailedAsync(7, 42, "req7", packet));

        Assert.Equal(2, bus.RequestCount);
        Assert.Single(owner.Deliveries);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Router_RetriesLostReplyAndReplaysFirstResultWithoutApplyingTwice(bool accepted)
    {
        var bus = new InMemoryNatsBus { RepliesToDropAfterHandling = 1 };
        var owner = new FakeSessionEndpoint { Accept = accepted };
        var leader = new NatsPlayerSessionRouter(bus.Connect(), _ => null, "user-server-0", new RecordingLogger());
        var other = new NatsPlayerSessionRouter(bus.Connect(), id => id == 7 ? owner : null, "user-server-1", new RecordingLogger());
        leader.Start();
        other.Start();

        using Packet packet = PacketMaker.U_TO_C_MATCHING_FAILED(ErrorCode.MATCHING_FAILED, 42);
        Assert.Equal(accepted, await leader.DeliverMatchingFailedAsync(7, 42, "req7", packet));

        Assert.Equal(2, bus.RequestCount);
        Assert.Single(owner.Deliveries);
        Assert.Equal(("failed", 42L, "req7"), owner.Deliveries[0]);
    }

    [Fact]
    public async Task Router_ReturnsFalseWhenNoProcessOwnsTheSession()
    {
        var bus = new InMemoryNatsBus();
        var leader = new NatsPlayerSessionRouter(bus.Connect(), _ => null, "user-server-0", new RecordingLogger());
        var other = new NatsPlayerSessionRouter(bus.Connect(), _ => null, "user-server-1", new RecordingLogger());
        leader.Start();
        other.Start();

        using Packet packet = PacketMaker.U_TO_C_MATCHING_FAILED(ErrorCode.MATCHING_FAILED, 42);
        Assert.False(await leader.DeliverMatchingFailedAsync(7, 42, "req7", packet));
        Assert.Equal(0, bus.RepliesForLastRequest);
        Assert.Equal(NatsPlayerSessionRouter.RemoteDeliveryAttempts, bus.RequestCount);
    }

    [Fact]
    public async Task Router_RemoteRejectionIsReportedAsFalse()
    {
        var bus = new InMemoryNatsBus();
        var owner = new FakeSessionEndpoint { Accept = false };
        var leader = new NatsPlayerSessionRouter(bus.Connect(), _ => null, "user-server-0", new RecordingLogger());
        var other = new NatsPlayerSessionRouter(bus.Connect(), id => id == 7 ? owner : null, "user-server-1", new RecordingLogger());
        leader.Start();
        other.Start();

        using Packet packet = PacketMaker.U_TO_C_MATCHING_FAILED(ErrorCode.MATCHING_FAILED, 42);
        Assert.False(await leader.DeliverAdmissionFailedAsync(7, 42, packet));
        Assert.Equal(("admission", 42L, ""), owner.Deliveries.Single());
    }

    [Fact]
    public void Router_ClearAndLoginNoticesReachOtherProcessesOnly()
    {
        var bus = new InMemoryNatsBus();
        var mine = new FakeSessionEndpoint { SessionGeneration = 2 };
        var theirs = new FakeSessionEndpoint { SessionGeneration = 1 };
        var a = new NatsPlayerSessionRouter(bus.Connect(), id => id == 7 ? mine : null, "user-server-0", new RecordingLogger());
        var b = new NatsPlayerSessionRouter(bus.Connect(), id => id == 7 ? theirs : null, "user-server-1", new RecordingLogger());
        a.Start();
        b.Start();

        a.ClearMatchingAssignment(7, 42);
        Assert.Equal([42L], mine.Cleared);
        Assert.Equal([42L], theirs.Cleared);

        a.AnnounceLogin(7, 2);
        Assert.False(mine.DuplicateDisconnected);
        Assert.True(theirs.DuplicateDisconnected);
    }

    [Fact]
    public void Router_DelayedOlderLoginNoticeDoesNotDisconnectNewerSession()
    {
        var bus = new InMemoryNatsBus();
        var newer = new FakeSessionEndpoint { SessionGeneration = 3 };
        var oldNode = new NatsPlayerSessionRouter(bus.Connect(), _ => null, "user-server-0", new RecordingLogger());
        var newNode = new NatsPlayerSessionRouter(
            bus.Connect(),
            id => id == 7 ? newer : null,
            "user-server-1",
            new RecordingLogger());
        oldNode.Start();
        newNode.Start();

        oldNode.AnnounceLogin(7, 2);

        Assert.False(newer.DuplicateDisconnected);
    }

    [Fact]
    public async Task SessionOwnership_LaterGenerationSupersedesAndOldLeaseCannotRenewOrRelease()
    {
        var cache = new InMemoryRedisOperations();
        var store = new RedisPlayerSessionOwnershipStore(
            cache,
            NullLogger<RedisPlayerSessionOwnershipStore>.Instance);

        PlayerSessionLease first = Assert.IsType<PlayerSessionLease>(
            await store.TryAcquireAsync(7, "user-server-0", "session-a"));
        PlayerSessionLease second = Assert.IsType<PlayerSessionLease>(
            await store.TryAcquireAsync(7, "user-server-1", "session-b"));

        Assert.True(second.Generation > first.Generation);
        Assert.Equal(second.OwnerValue, cache.GetString(RedisPlayerSessionOwnershipStore.OwnerKey(7)));
        Assert.False(await store.TryRenewAsync(first));
        Assert.False(await store.TryReleaseAsync(first));
        Assert.True(await store.TryRenewAsync(second));
        Assert.True(await store.TryReleaseAsync(second));
        Assert.Null(cache.GetString(RedisPlayerSessionOwnershipStore.OwnerKey(7)));
    }

    [Fact]
    public async Task SessionOwnership_ConcurrentClaimsLeaveHighestGenerationAsOwner()
    {
        var cache = new InMemoryRedisOperations();
        var store = new RedisPlayerSessionOwnershipStore(
            cache,
            NullLogger<RedisPlayerSessionOwnershipStore>.Instance);

        PlayerSessionLease?[] leases = await Task.WhenAll(
            Enumerable.Range(0, 16)
                .Select(index => store.TryAcquireAsync(9, $"user-server-{index}", $"session-{index}")));

        PlayerSessionLease winner = leases
            .Where(lease => lease != null)
            .Select(lease => lease!)
            .MaxBy(lease => lease.Generation)!;

        Assert.Equal(winner.OwnerValue, cache.GetString(RedisPlayerSessionOwnershipStore.OwnerKey(9)));
        Assert.Equal(16, winner.Generation);
    }

    private sealed class FakeSessionEndpoint : IMatchingSessionEndpoint
    {
        public bool Accept { get; set; } = true;
        public long SessionGeneration { get; set; } = 1;
        public List<(string Op, long MatchingId, string RequestId)> Deliveries { get; } = new();
        public List<long> Cleared { get; } = new();
        public int LastPacketProtocolId { get; private set; }
        public bool DuplicateDisconnected { get; private set; }

        public bool TryDeliverMatchingSuccess(long matchingId, string requestId, Packet packet) =>
            Record("success", matchingId, requestId, packet);

        public bool TryDeliverMatchingFailed(long matchingId, string requestId, Packet packet) =>
            Record("failed", matchingId, requestId, packet);

        public bool TryDeliverAdmissionFailed(long matchingId, Packet packet) =>
            Record("admission", matchingId, string.Empty, packet);

        public void ClearMatchingAssignment(long matchingId) => Cleared.Add(matchingId);
        public void DisconnectIfSuperseded(long newGeneration) =>
            DuplicateDisconnected = newGeneration > SessionGeneration;

        private bool Record(string op, long matchingId, string requestId, Packet packet)
        {
            Deliveries.Add((op, matchingId, requestId));
            LastPacketProtocolId = packet.ProtocolId;
            return Accept;
        }
    }

    /// <summary>
    ///     프로세스 여러 개가 붙은 NATS를 흉내 낸다: publish는 모든 구독자에게, request는 응답(null 아님)을 준 첫 핸들러의
    ///     값을 돌려주고 아무도 답하지 않으면 타임아웃처럼 던진다.
    /// </summary>
    private sealed class InMemoryNatsBus
    {
        private readonly List<(string Subject, Action<string, byte[]> Handler)> _subscribers = new();
        private readonly List<(string Subject, Func<string, byte[], CancellationToken, Task<byte[]?>> Handler)> _responders = new();

        public int RequestCount { get; private set; }
        public int RepliesForLastRequest { get; private set; }
        public int RequestsToDropBeforeHandling { get; set; }
        public int RepliesToDropAfterHandling { get; set; }

        public INatsClient Connect() => new Client(this);

        private static bool Matches(string pattern, string subject)
        {
            if (pattern.EndsWith(".*", StringComparison.Ordinal))
            {
                string prefix = pattern[..^1];
                return subject.StartsWith(prefix, StringComparison.Ordinal) &&
                       !subject[prefix.Length..].Contains('.');
            }

            return pattern == subject;
        }

        private sealed class Client(InMemoryNatsBus bus) : INatsClient
        {
            public void Publish(string subject, byte[] message)
            {
                foreach ((string pattern, Action<string, byte[]> handler) in bus._subscribers.ToArray())
                    if (Matches(pattern, subject))
                        handler(subject, message);
            }

            public void Subscribe(string subject, Action<string, byte[]> messageHandler, string? queue = null) =>
                bus._subscribers.Add((subject, messageHandler));

            public async Task<byte[]> RequestAsync(string subject, byte[] message, TimeSpan timeout,
                CancellationToken cancellationToken = default)
            {
                bus.RequestCount++;
                bus.RepliesForLastRequest = 0;
                if (bus.RequestsToDropBeforeHandling > 0)
                {
                    bus.RequestsToDropBeforeHandling--;
                    throw new TimeoutException("request lost before handling");
                }

                byte[]? first = null;
                foreach ((string pattern, var handler) in bus._responders.ToArray())
                {
                    if (!Matches(pattern, subject)) continue;
                    byte[]? reply = await handler(subject, message, cancellationToken);
                    if (reply == null) continue;
                    bus.RepliesForLastRequest++;
                    first ??= reply;
                }

                if (first == null)
                    throw new TimeoutException("no responder");
                if (bus.RepliesToDropAfterHandling > 0)
                {
                    bus.RepliesToDropAfterHandling--;
                    throw new TimeoutException("reply lost after handling");
                }

                return first;
            }

            public void SubscribeRequest(string subject,
                Func<string, byte[], CancellationToken, Task<byte[]?>> messageHandler, string? queue = null) =>
                bus._responders.Add((subject, messageHandler));

            public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public void Close()
            {
            }
        }
    }
}
