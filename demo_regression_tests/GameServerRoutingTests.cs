using user_server.matching.creation;
using game_server.services;
using network.common;
using network.common.data.models;
using network.gamehandoff;
using network.routing;
using user_server.matching;

namespace demo_regression_tests;

/// <summary>
///     #339 Game Server 다중 인스턴스: 레지스트리 왕복, 최소 부하 선택 정책(낡은·draining·만석 노드 제외,
///     하트비트 미반영 배정 가산, nodeId 순 동률), 광고자의 accepting 전이, ticket의 노드 결합.
/// </summary>
public sealed class GameServerRoutingTests
{
    private static readonly TimeSpan MaxAge = GameServerAllocator.MaximumNodeAge;

    private static GameServerNodeDescriptor Node(
        string id,
        int active = 0,
        int max = 8,
        bool accepting = true,
        long heartbeat = 100_000)
    {
        return new GameServerNodeDescriptor
        {
            NodeId = id,
            PublicHost = "10.0.0." + id[^1],
            PublicPort = 9001,
            MaxConcurrentMatches = max,
            ActiveMatches = active,
            Accepting = accepting,
            HeartbeatUnixMs = heartbeat,
            StartedUnixMs = 1
        };
    }

    [Fact]
    public async Task Registry_RoundTripsDescriptorsAndSkipsCorruptEntries()
    {
        var cache = new InMemoryRedisOperations();
        var registry = new RedisGameServerRegistry(cache);
        await registry.PublishAsync(Node("game-server-0", active: 2));
        await registry.PublishAsync(Node("game-server-1", active: 5, accepting: false));
        await cache.HashSetAsync(RedisGameServerRegistry.NodesKey, "broken", new byte[] { 0xC1, 0x00 });

        IReadOnlyList<GameServerNodeDescriptor> nodes = await registry.DiscoverAsync();

        Assert.Equal(2, nodes.Count);
        GameServerNodeDescriptor first = Assert.Single(nodes, node => node.NodeId == "game-server-0");
        Assert.Equal(2, first.ActiveMatches);
        Assert.Equal("10.0.0.0", first.PublicHost);
        Assert.False(Assert.Single(nodes, node => node.NodeId == "game-server-1").Accepting);

        await registry.RemoveAsync("game-server-0");
        Assert.Single(await registry.DiscoverAsync());
    }

    [Fact]
    public async Task Registry_RejectsDescriptorWithoutAddressOrCapacity()
    {
        var registry = new RedisGameServerRegistry(new InMemoryRedisOperations());

        await Assert.ThrowsAsync<ArgumentException>(() => registry.PublishAsync(Node("game-server-0", max: 0)));
        await Assert.ThrowsAsync<ArgumentException>(() => registry.PublishAsync(new GameServerNodeDescriptor
        {
            NodeId = "game-server-0",
            PublicPort = 9001,
            MaxConcurrentMatches = 1,
            HeartbeatUnixMs = 1
        }));
    }

    [Fact]
    public void Policy_PicksLowestLoadRatioAndBreaksTiesByNodeId()
    {
        var nodes = new[]
        {
            Node("game-server-2", active: 1, max: 4), // 0.25
            Node("game-server-1", active: 2, max: 16), // 0.125
            Node("game-server-0", active: 1, max: 8) // 0.125 — 동률이면 nodeId 순
        };

        GameServerNodeDescriptor? chosen = GameServerSelectionPolicy.Select(nodes, 100_500, MaxAge, _ => 0);

        Assert.Equal("game-server-0", chosen?.NodeId);
    }

    [Fact]
    public void Policy_SkipsStaleDrainingAndFullNodes()
    {
        long now = 200_000;
        var nodes = new[]
        {
            Node("game-server-0", heartbeat: now - (long)MaxAge.TotalMilliseconds - 1), // 낡음
            Node("game-server-1", accepting: false, heartbeat: now), // 종료 중
            Node("game-server-2", active: 8, max: 8, heartbeat: now), // 만석
            Node("game-server-3", active: 7, max: 8, heartbeat: now)
        };

        Assert.Equal("game-server-3", GameServerSelectionPolicy.Select(nodes, now, MaxAge, _ => 0)?.NodeId);
        Assert.Null(GameServerSelectionPolicy.Select(nodes.Take(3).ToArray(), now, MaxAge, _ => 0));
    }

    [Fact]
    public void Policy_CountsPendingAssignmentsAgainstCapacity()
    {
        var nodes = new[] { Node("game-server-0", active: 0, max: 4), Node("game-server-1", active: 1, max: 4) };

        // 첫 노드에 아직 하트비트에 안 잡힌 배정이 2건이면 0.5 > 0.25라 두 번째 노드로 간다.
        GameServerNodeDescriptor? chosen = GameServerSelectionPolicy.Select(
            nodes, 100_500, MaxAge, node => node.NodeId == "game-server-0" ? 2 : 0);

        Assert.Equal("game-server-1", chosen?.NodeId);
    }

    [Fact]
    public async Task Allocator_SpreadsConsecutiveMatchesUntilHeartbeatCatchesUp()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var registry = new RecordingGameServerRegistry();
        registry.Published.Add(Node("game-server-0", active: 0, max: 2, heartbeat: now));
        registry.Published.Add(Node("game-server-1", active: 0, max: 2, heartbeat: now));
        var allocator = new GameServerAllocator(registry, new RecordingLogger());

        var picks = new List<string>();
        for (int i = 0; i < 5; i++)
            picks.Add((await allocator.TryAllocateAsync())?.NodeId ?? "none");

        // 하트비트가 갱신되지 않는 동안 배정 2건씩 채우면 두 노드 모두 만석 → 다섯 번째는 없음
        Assert.Equal(new[] { "game-server-0", "game-server-1", "game-server-0", "game-server-1", "none" }, picks);

        // 새 하트비트가 배정을 반영하면(활성 1) 미반영 배정은 잊고 다시 받는다
        registry.Published[0] = Node("game-server-0", active: 1, max: 2, heartbeat: now + 5_000);
        Assert.Equal("game-server-0", (await allocator.TryAllocateAsync())?.NodeId);
    }

    [Fact]
    public async Task Allocator_ReturnsNullAndWarnsOnceWhenNoNodeIsRegistered()
    {
        var logger = new RecordingLogger();
        var allocator = new GameServerAllocator(new RecordingGameServerRegistry(), logger);

        Assert.Null(await allocator.TryAllocateAsync());
        Assert.Null(await allocator.TryAllocateAsync());

        Assert.True(logger.Contains(Microsoft.Extensions.Logging.LogLevel.Warning, "No game server node can take a match"));
    }

    [Fact]
    public async Task Advertiser_PublishesAcceptingThenDrainingThenRemoves()
    {
        var registry = new RecordingGameServerRegistry();
        var options = new GameServerNodeOptions { NodeId = "game-server-1", PublicHost = "10.0.0.5", PublicPort = 9002, MaxConcurrentMatches = 3 };
        int active = 1;
        await using var advertiser = new GameServerNodeAdvertiser(registry, options, () => active, new RecordingLogger());

        await advertiser.StartAsync();
        active = 2;
        await advertiser.StopAcceptingAsync();
        await advertiser.RemoveAsync();

        Assert.Equal(2, registry.Published.Count);
        Assert.True(registry.Published[0].Accepting);
        Assert.Equal(1, registry.Published[0].ActiveMatches);
        Assert.Equal(("10.0.0.5", 9002, 3), (registry.Published[0].PublicHost, registry.Published[0].PublicPort, registry.Published[0].MaxConcurrentMatches));
        Assert.False(registry.Published[1].Accepting);
        Assert.Equal(2, registry.Published[1].ActiveMatches);
        Assert.Equal(new[] { "game-server-1" }, registry.Removed);
    }

    [Fact]
    public async Task Advertiser_FailsStartupWhenFirstPublishFails()
    {
        var registry = new RecordingGameServerRegistry { PublishError = new InvalidOperationException("redis down") };
        var options = new GameServerNodeOptions { NodeId = "game-server-1", PublicHost = "10.0.0.5" };
        await using var advertiser = new GameServerNodeAdvertiser(registry, options, () => 0, new RecordingLogger());

        await Assert.ThrowsAsync<InvalidOperationException>(advertiser.StartAsync);
    }

    [Fact]
    public void NodeOptions_RequireNodeIdentityAndPublicHost()
    {
        Assert.Throws<InvalidOperationException>(() => new GameServerNodeOptions
        {
            PublicHost = "10.0.0.5"
        }.Validate());
        Assert.Throws<InvalidOperationException>(() => new GameServerNodeOptions
        {
            NodeId = "game-server-blue"
        }.Validate());

        new GameServerNodeOptions
        {
            NodeId = "game-server-blue",
            PublicHost = "10.0.0.5"
        }.Validate();
    }

    [Fact]
    public async Task Ticket_IsConsumedOnlyByTheAssignedNode()
    {
        var store = new InMemoryGameHandoffTicketStore();
        var service = new GameHandoffTicketService(store, new GameHandoffTicketOptions());
        string ticket = await service.IssueAsync(NewContext("game-server-1"));

        // 다른 노드가 먼저 받으면 거절되고, ticket은 이미 소비돼 원래 노드도 못 쓴다 (fail closed).
        Assert.Null(await service.ConsumeAsync(ticket, "game-server-0"));
        Assert.Null(await service.ConsumeAsync(ticket, "game-server-1"));

        string second = await service.IssueAsync(NewContext("game-server-1"));
        GameHandoffContext? consumed = await service.ConsumeAsync(second, "game-server-1");
        Assert.Equal("game-server-1", consumed?.GameServerNodeId);
    }

    [Fact]
    public async Task Ticket_CannotBeIssuedWithoutNodeBinding()
    {
        var service = new GameHandoffTicketService(new InMemoryGameHandoffTicketStore(), new GameHandoffTicketOptions());

        await Assert.ThrowsAsync<ArgumentException>(() => service.IssueAsync(NewContext(string.Empty)));
    }

    private static GameHandoffContext NewContext(string nodeId)
    {
        return new GameHandoffContext
        {
            PlayerId = 7,
            MatchingId = 42,
            GameServerNodeId = nodeId
        };
    }

    private sealed class InMemoryGameHandoffTicketStore : IGameHandoffTicketStore
    {
        private readonly Dictionary<string, GameHandoffContext> _tickets = new(StringComparer.Ordinal);

        public Task<bool> TryStoreAsync(string ticketHash, GameHandoffContext context, TimeSpan lifetime) =>
            Task.FromResult(_tickets.TryAdd(ticketHash, context));

        public Task<GameHandoffContext?> ConsumeAsync(string ticketHash)
        {
            _tickets.Remove(ticketHash, out GameHandoffContext? context);
            return Task.FromResult(context);
        }
    }
}
