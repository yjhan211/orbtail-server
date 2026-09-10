using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;
using network.gameentry;
using network.infrastructure.messaging;
using network.packets;
using user_server.matching;
using user_server.matching.creation;
using user_server.matching.queue;
using user_server.sessions;

namespace demo_regression_tests;

/// <summary>
///     #339 2단계 User Server N대: 매칭 리더 lease(획득·갱신·상실·해제), 세션 라우터(로컬 직행, 원격 request는 세션을
///     가진 프로세스만 응답, timeout은 재시도 없이 false, 최종 부재는 false, 해제·로그인 알림은 origin 제외 브로드캐스트).
/// </summary>
public sealed class UserServerScaleOutTests
{
    [Fact]
    public async Task LeaderLease_OnlyOneNodeHoldsItAndRenewalKeepsIt()
    {
        var cache = new InMemoryRedisOperations();
        var a = new MatchingLeaderLease(cache, "user-server-0", new RecordingLogger().For<MatchingLeaderLease>());
        var b = new MatchingLeaderLease(cache, "user-server-1", new RecordingLogger().For<MatchingLeaderLease>());

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
        var a = new MatchingLeaderLease(cache, "user-server-0", new RecordingLogger().For<MatchingLeaderLease>());
        var b = new MatchingLeaderLease(cache, "user-server-1", new RecordingLogger().For<MatchingLeaderLease>());
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
        var lease = new MatchingLeaderLease(cache, "user-server-0", logger.For<MatchingLeaderLease>());
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
        var router = new NatsPlayerSessionRouter(bus.Connect(), id => id == 7 ? session : null, "user-server-0", new RecordingLogger().For<NatsPlayerSessionRouter>());
        router.Start();

        Assert.True(await router.DeliverMatchingFailedAsync(7, 42, "req7", ErrorCode.MATCHING_FAILED));

        Assert.Equal(("failed", 42L, "req7"), session.Deliveries.Single());
        Assert.Equal(0, bus.RequestCount);
    }

    [Fact]
    public async Task Router_DelegatesToTheProcessThatOwnsTheSession()
    {
        var bus = new InMemoryNatsBus();
        var owner = new FakeSessionEndpoint();
        var leader = new NatsPlayerSessionRouter(bus.Connect(), _ => null, "user-server-0", new RecordingLogger().For<NatsPlayerSessionRouter>());
        var other = new NatsPlayerSessionRouter(bus.Connect(), id => id == 7 ? owner : null, "user-server-1", new RecordingLogger().For<NatsPlayerSessionRouter>());
        var bystander = new NatsPlayerSessionRouter(bus.Connect(), _ => null, "user-server-2", new RecordingLogger().For<NatsPlayerSessionRouter>());
        leader.Start();
        other.Start();
        bystander.Start();

        Assert.True(await leader.DeliverMatchingFailedAsync(7, 42, "req7", ErrorCode.MATCHING_FAILED));

        (string Op, long MatchingId, string RequestId) delivery = owner.Deliveries.Single();
        Assert.Equal(("failed", 42L, "req7"), delivery);
        Assert.Equal((int)Protocol.U_TO_C_MATCHING_FAILED, owner.LastPacketProtocolId);
        Assert.Equal(1, bus.RequestCount);
        Assert.Equal(1, bus.RepliesForLastRequest); // 세션 없는 두 프로세스는 침묵한다
    }

    [Fact]
    public async Task Router_LostRequestReturnsFalseWithoutRetry()
    {
        var bus = new InMemoryNatsBus { RequestsToDropBeforeHandling = 1 };
        var owner = new FakeSessionEndpoint();
        var leader = new NatsPlayerSessionRouter(bus.Connect(), _ => null, "user-server-0", new RecordingLogger().For<NatsPlayerSessionRouter>());
        var other = new NatsPlayerSessionRouter(bus.Connect(), id => id == 7 ? owner : null, "user-server-1", new RecordingLogger().For<NatsPlayerSessionRouter>());
        leader.Start();
        other.Start();

        Assert.False(await leader.DeliverMatchingFailedAsync(7, 42, "req7", ErrorCode.MATCHING_FAILED));

        Assert.Equal(1, bus.RequestCount);
        Assert.Empty(owner.Deliveries);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Router_LostReplyReturnsFalseEvenIfAlreadyHandled(bool accepted)
    {
        var bus = new InMemoryNatsBus { RepliesToDropAfterHandling = 1 };
        var owner = new FakeSessionEndpoint { Accept = accepted };
        var leader = new NatsPlayerSessionRouter(bus.Connect(), _ => null, "user-server-0", new RecordingLogger().For<NatsPlayerSessionRouter>());
        var other = new NatsPlayerSessionRouter(bus.Connect(), id => id == 7 ? owner : null, "user-server-1", new RecordingLogger().For<NatsPlayerSessionRouter>());
        leader.Start();
        other.Start();

        Assert.False(await leader.DeliverMatchingFailedAsync(7, 42, "req7", ErrorCode.MATCHING_FAILED));

        Assert.Equal(1, bus.RequestCount);
        Assert.Single(owner.Deliveries);
        Assert.Equal(("failed", 42L, "req7"), owner.Deliveries[0]);
    }

    [Fact]
    public async Task Router_ReturnsFalseWhenNoProcessOwnsTheSession()
    {
        var bus = new InMemoryNatsBus();
        var leader = new NatsPlayerSessionRouter(bus.Connect(), _ => null, "user-server-0", new RecordingLogger().For<NatsPlayerSessionRouter>());
        var other = new NatsPlayerSessionRouter(bus.Connect(), _ => null, "user-server-1", new RecordingLogger().For<NatsPlayerSessionRouter>());
        leader.Start();
        other.Start();

        Assert.False(await leader.DeliverMatchingFailedAsync(7, 42, "req7", ErrorCode.MATCHING_FAILED));
        Assert.Equal(0, bus.RepliesForLastRequest);
        Assert.Equal(1, bus.RequestCount);
    }

    [Fact]
    public async Task Router_RemoteRejectionIsReportedAsFalse()
    {
        var bus = new InMemoryNatsBus();
        var owner = new FakeSessionEndpoint { Accept = false };
        var leader = new NatsPlayerSessionRouter(bus.Connect(), _ => null, "user-server-0", new RecordingLogger().For<NatsPlayerSessionRouter>());
        var other = new NatsPlayerSessionRouter(bus.Connect(), id => id == 7 ? owner : null, "user-server-1", new RecordingLogger().For<NatsPlayerSessionRouter>());
        leader.Start();
        other.Start();

        Assert.False(await leader.DeliverEntryFailedAsync(7, 42, ErrorCode.MATCHING_FAILED));
        Assert.Equal(("entry", 42L, ""), owner.Deliveries.Single());
    }

    [Fact]
    public void Router_ClearAndLoginNoticesReachOtherProcessesOnly()
    {
        var bus = new InMemoryNatsBus();
        var mine = new FakeSessionEndpoint { SessionGeneration = 2 };
        var theirs = new FakeSessionEndpoint { SessionGeneration = 1 };
        var a = new NatsPlayerSessionRouter(bus.Connect(), id => id == 7 ? mine : null, "user-server-0", new RecordingLogger().For<NatsPlayerSessionRouter>());
        var b = new NatsPlayerSessionRouter(bus.Connect(), id => id == 7 ? theirs : null, "user-server-1", new RecordingLogger().For<NatsPlayerSessionRouter>());
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
        var oldNode = new NatsPlayerSessionRouter(bus.Connect(), _ => null, "user-server-0", new RecordingLogger().For<NatsPlayerSessionRouter>());
        var newNode = new NatsPlayerSessionRouter(
            bus.Connect(),
            id => id == 7 ? newer : null,
            "user-server-1",
            new RecordingLogger().For<NatsPlayerSessionRouter>());
        oldNode.Start();
        newNode.Start();

        oldNode.AnnounceLogin(7, 2);

        Assert.False(newer.DuplicateDisconnected);
    }

    [Fact]
    public async Task SessionLease_LaterGenerationSupersedesAndOldLeaseCannotRenewOrRelease()
    {
        var cache = new InMemoryRedisOperations();
        var store = new RedisPlayerSessionLeaseStore(
            cache,
            NullLogger<RedisPlayerSessionLeaseStore>.Instance);

        PlayerSessionLease first = Assert.IsType<PlayerSessionLease>(
            await store.TryAcquireAsync(7, "user-server-0", "session-a"));
        PlayerSessionLease second = Assert.IsType<PlayerSessionLease>(
            await store.TryAcquireAsync(7, "user-server-1", "session-b"));

        Assert.True(second.Generation > first.Generation);
        Assert.Equal(second.OwnerValue, cache.GetString(RedisPlayerSessionLeaseStore.OwnerKey(7)));
        Assert.False(await store.TryRenewAsync(first));
        Assert.False(await store.TryReleaseAsync(first));
        Assert.True(await store.TryRenewAsync(second));
        Assert.True(await store.TryReleaseAsync(second));
        Assert.Null(cache.GetString(RedisPlayerSessionLeaseStore.OwnerKey(7)));
    }

    [Fact]
    public async Task SessionLease_ConcurrentReservationsLeaveHighestGenerationAsOwner()
    {
        var cache = new InMemoryRedisOperations();
        var store = new RedisPlayerSessionLeaseStore(
            cache,
            NullLogger<RedisPlayerSessionLeaseStore>.Instance);

        PlayerSessionLease?[] leases = await Task.WhenAll(
            Enumerable.Range(0, 16)
                .Select(index => store.TryAcquireAsync(9, $"user-server-{index}", $"session-{index}")));

        PlayerSessionLease winner = leases
            .Where(lease => lease != null)
            .Select(lease => lease!)
            .MaxBy(lease => lease.Generation)!;

        Assert.Equal(winner.OwnerValue, cache.GetString(RedisPlayerSessionLeaseStore.OwnerKey(9)));
        Assert.Equal(16, winner.Generation);
    }

    [Fact]
    public async Task Router_DeliverySubjectsInvokeTheirOwnSessionMethods()
    {
        var bus = new InMemoryNatsBus();
        var owner = new FakeSessionEndpoint();
        var sender = new NatsPlayerSessionRouter(bus.Connect(), _ => null, "sender", new RecordingLogger().For<NatsPlayerSessionRouter>());
        var receiver = new NatsPlayerSessionRouter(bus.Connect(), id => id == 7 ? owner : null, "receiver", new RecordingLogger().For<NatsPlayerSessionRouter>());
        sender.Start();
        receiver.Start();

        Assert.True(await sender.DeliverMatchingSuccessAsync(7, "req7", new U_TO_C_MATCHING_SUCCESS
        {
            MatchingId = 42, GameServerIp = "localhost", GameServerPort = 9001,
            GameEndTimestamp = 123456, GameEntryTicket = "test-ticket"
        }));
        Assert.True(await sender.DeliverMatchingFailedAsync(7, 42, "req7", ErrorCode.MATCHING_FAILED));
        Assert.True(await sender.DeliverEntryFailedAsync(7, 42, ErrorCode.MATCHING_FAILED));
        Assert.Equal(3, owner.Deliveries.Count);
        Assert.Equal(("success", 42L, "req7"), owner.Deliveries[0]);
        Assert.Equal(("failed", 42L, "req7"), owner.Deliveries[1]);
        Assert.Equal(("entry", 42L, ""), owner.Deliveries[2]);
        Assert.Equal(3, bus.RequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Router_UnconfirmedSuccessRollsBackMatchWithoutPublishingEntryReady(bool loseReply)
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var cache = new InMemoryRedisOperations();
        var logger = new RecordingLogger();
        var reservations = new MatchingReservationService(cache, logger.For<MatchingReservationService>());
        var queue = new MatchingQueue(cache, new FakeRedLockFactory(), reservations, logger.For<MatchingQueue>());
        var bus = new InMemoryNatsBus
        {
            RequestsToDropBeforeHandling = loseReply ? 0 : 1,
            RepliesToDropAfterHandling = loseReply ? 1 : 0
        };
        var owner = new FakeSessionEndpoint();
        var sender = new NatsPlayerSessionRouter(bus.Connect(), _ => null, "sender", logger.For<NatsPlayerSessionRouter>());
        var receiver = new NatsPlayerSessionRouter(bus.Connect(), id => id == 7 ? owner : null, "receiver", logger.For<NatsPlayerSessionRouter>());
        sender.Start();
        receiver.Start();
        using var entryTasks = new network.infrastructure.BackgroundTaskTracker(logger);
        var entryService = new MatchEntryService(
            cache,
            new GameEntryTicketService(new RedisGameEntryTicketStore(cache), new GameEntryTicketOptions()),
            reservations, sender,
            entryTasks,
            logger.For<MatchEntryService>());
        var pass = new MatchCreationService(
            cache, queue, reservations, entryService,
            new FixedGameServerAllocator(),
            false,
            logger.For<MatchCreationService>(), CancellationToken.None);
        var entry = UserServerMatchingTestData.HumanEntry(7);
        await UserServerMatchingTestData.AddEntryAsync(cache, entry, 1);

        Assert.False(await pass.CreateMatchAsync([entry], 7));

        // 성공 응답만 유실돼도 성공 전달을 재시도하지 않고 실패 통지로 진행한다.
        Assert.Equal(2, bus.RequestCount); // 성공 요청 1회 + 실패 통지 1회
        Assert.Equal(loseReply ? 1 : 0, owner.Deliveries.Count(d => d.Op == "success"));
        Assert.Single(owner.Deliveries, d => d.Op == "failed");
        Assert.Null(cache.GetString(MatchingRedisKeys.ReservationKey(7)));
        Assert.Equal(0, cache.SortedSetCount(MatchingQueue.QueueKey));
        Assert.True((await cache.HashGetAsync(
            MatchingRedisKeys.Key(1), MatchingRedisKeys.EntryReadyField)).IsNullOrEmpty);
        Assert.True((await cache.HashGetAsync(
            MatchingRedisKeys.Key(1), MatchingRedisKeys.ManifestField)).IsNullOrEmpty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Router_ResultDataBuildsEquivalentClientPackets(bool remote)
    {
        var bus = new InMemoryNatsBus();
        var owner = new FakeSessionEndpoint();
        var sender = new NatsPlayerSessionRouter(bus.Connect(),
            id => !remote && id == 7 ? owner : null, "sender", new RecordingLogger().For<NatsPlayerSessionRouter>());
        var receiver = new NatsPlayerSessionRouter(bus.Connect(),
            id => remote && id == 7 ? owner : null, "receiver", new RecordingLogger().For<NatsPlayerSessionRouter>());
        sender.Start();
        receiver.Start();
        var result = new U_TO_C_MATCHING_SUCCESS
        {
            MatchingId = 42, GameServerIp = "game.example", GameServerPort = 9001,
            GameEndTimestamp = 123456789, GameEntryTicket = "ticket"
        };

        Assert.True(await sender.DeliverMatchingSuccessAsync(7, "req7", result));
        using (var expected = PacketMaker.U_TO_C_MATCHING_SUCCESS(
            result.MatchingId, result.GameServerIp, result.GameServerPort,
            result.GameEndTimestamp, result.GameEntryTicket))
        {
            expected.RecordSize();
            Assert.Equal(expected.ToBytes(), owner.LastPacketBytes);
        }

        Assert.True(await sender.DeliverMatchingFailedAsync(7, 42, "req7", ErrorCode.AUTH_FAILED));
        using (var expected = PacketMaker.U_TO_C_MATCHING_FAILED(ErrorCode.AUTH_FAILED, 42))
        {
            expected.RecordSize();
            Assert.Equal(expected.ToBytes(), owner.LastPacketBytes);
        }
        Assert.True(await sender.DeliverEntryFailedAsync(7, 42, ErrorCode.MATCHING_FAILED));
        using (var expected = PacketMaker.U_TO_C_MATCHING_FAILED(ErrorCode.MATCHING_FAILED, 42))
        {
            expected.RecordSize();
            Assert.Equal(expected.ToBytes(), owner.LastPacketBytes);
        }
        Assert.Equal(remote ? 3 : 0, bus.RequestCount);
    }

    private sealed class FakeSessionEndpoint : IMatchingSessionEndpoint
    {
        public bool Accept { get; set; } = true;
        public long SessionGeneration { get; set; } = 1;
        public List<(string Op, long MatchingId, string RequestId)> Deliveries { get; } = new();
        public List<long> Cleared { get; } = new();
        public int LastPacketProtocolId { get; private set; }
        public byte[] LastPacketBytes { get; private set; } = [];
        public bool DuplicateDisconnected { get; private set; }

        public bool TryDeliverMatchingSuccess(long matchingId, string requestId, Packet packet) =>
            Record("success", matchingId, requestId, packet);

        public bool TryDeliverMatchingFailed(long matchingId, string requestId, Packet packet) =>
            Record("failed", matchingId, requestId, packet);

        public bool TryDeliverEntryFailed(long matchingId, Packet packet) =>
            Record("entry", matchingId, string.Empty, packet);

        public void ClearMatchingAssignment(long matchingId) => Cleared.Add(matchingId);
        public void DisconnectIfOlderSession(long newGeneration) =>
            DuplicateDisconnected = newGeneration > SessionGeneration;

        private bool Record(string op, long matchingId, string requestId, Packet packet)
        {
            Deliveries.Add((op, matchingId, requestId));
            LastPacketProtocolId = packet.ProtocolId;
            packet.RecordSize();
            LastPacketBytes = packet.ToBytes();
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
