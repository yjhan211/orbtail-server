using MessagePack;
using network.contracts.scaling;
using network.interfaces;
using StackExchange.Redis;

namespace network.infrastructure.scaling;

public sealed class RedisGameServerRoutingStore(
    ICacheHelper cacheHelper,
    IRedisConnectionPool redisPool) : IGameServerRoutingStore
{
    private const string MatchOwnerActiveScript = """
        if redis.call('GET', KEYS[1]) ~= ARGV[1] then
            return 0
        end
        if redis.call('GET', KEYS[2]) ~= ARGV[2] then
            return 0
        end
        return 1
        """;

    private const string ClampMatchOwnerLifetimeScript = """
        if redis.call('GET', KEYS[1]) ~= ARGV[1] then
            return 0
        end

        local redisTime = redis.call('TIME')
        local now = tonumber(redisTime[1]) * 1000 + math.floor(tonumber(redisTime[2]) / 1000)
        local maximumTtl = tonumber(ARGV[3])
        local currentTtl = redis.call('PTTL', KEYS[1])
        if currentTtl < 0 or currentTtl > maximumTtl then
            redis.call('PEXPIRE', KEYS[1], maximumTtl)
        end

        local currentScore = redis.call('ZSCORE', KEYS[2], ARGV[2])
        local maximumScore = now + maximumTtl
        if currentScore and tonumber(currentScore) > maximumScore then
            redis.call('ZADD', KEYS[2], 'XX', maximumScore, ARGV[2])
        end
        return 1
        """;

    private const string CountActiveNodeSlotsScript = """
        local redisTime = redis.call('TIME')
        local now = tonumber(redisTime[1]) * 1000 + math.floor(tonumber(redisTime[2]) / 1000)
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now)
        return redis.call('ZCARD', KEYS[1])
        """;

    private const string PublishNodeHeartbeatScript = """
        if redis.call('GET', KEYS[1]) ~= ARGV[1] then
            return 0
        end
        local redisTime = redis.call('TIME')
        local now = tonumber(redisTime[1]) * 1000 + math.floor(tonumber(redisTime[2]) / 1000)
        redis.call('ZADD', KEYS[2], now, ARGV[2])
        return 1
        """;

    private const string DiscoverNodeHeartbeatsScript = """
        local redisTime = redis.call('TIME')
        local now = tonumber(redisTime[1]) * 1000 + math.floor(tonumber(redisTime[2]) / 1000)
        local cutoff = now - tonumber(ARGV[1])
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', cutoff)
        return redis.call('ZRANGEBYSCORE', KEYS[1], cutoff, '+inf')
        """;

    public async Task<bool> TryAcquireNodeLeaseAsync(
        GameServerNodeDescriptor descriptor,
        TimeSpan leaseLifetime)
    {
        ValidateDescriptor(descriptor);
        bool acquired = await cacheHelper.StringSetIfNotExistsAsync(
            GameServerRoutingKeys.NodeLease(descriptor.NodeId),
            descriptor.Generation,
            leaseLifetime);
        if (!acquired)
            return false;

        try
        {
            if (!await PublishDescriptorAsync(descriptor, leaseLifetime))
                throw new InvalidOperationException("Node lease changed while publishing its descriptor.");
            await PublishAcceptingStateAsync(descriptor, leaseLifetime);
            await PublishHeartbeatIndexAsync(descriptor);
            return true;
        }
        catch
        {
            await ReleaseNodeLeaseAsync(new GameServerNodeIdentity(descriptor.NodeId, descriptor.Generation));
            throw;
        }
    }

    public async Task<bool> TryHeartbeatNodeAsync(
        GameServerNodeDescriptor descriptor,
        TimeSpan leaseLifetime)
    {
        ValidateDescriptor(descriptor);
        bool renewed = await cacheHelper.StringSetIfEqualsAsync(
            GameServerRoutingKeys.NodeLease(descriptor.NodeId),
            descriptor.Generation,
            descriptor.Generation,
            leaseLifetime);
        if (!renewed)
            return false;

        if (!await PublishDescriptorAsync(descriptor, leaseLifetime))
            return false;
        await PublishAcceptingStateAsync(descriptor, leaseLifetime);
        await PublishHeartbeatIndexAsync(descriptor);
        return true;
    }

    public async Task<bool> TryBeginDrainAsync(
        GameServerNodeDescriptor descriptor,
        TimeSpan leaseLifetime)
    {
        ValidateDescriptor(descriptor);
        if (descriptor.Status != GameServerNodeStatus.Draining)
            throw new ArgumentException("A draining descriptor is required.", nameof(descriptor));

        await cacheHelper.StringDeleteIfEqualsAsync(
            GameServerRoutingKeys.NodeAccepting(descriptor.NodeId),
            descriptor.Generation);
        return await TryHeartbeatNodeAsync(descriptor, leaseLifetime);
    }

    public async Task ReleaseNodeLeaseAsync(GameServerNodeIdentity identity)
    {
        if (!identity.IsValid)
            return;

        await cacheHelper.ReleaseGameServerNodeLeaseAsync(
            GameServerRoutingKeys.NodeLease(identity.NodeId),
            GameServerRoutingKeys.NodeAccepting(identity.NodeId),
            GameServerRoutingKeys.NodeDescriptor(identity.NodeId),
            GameServerRoutingKeys.NodeHeartbeatIndex,
            identity.NodeId,
            identity.Generation);
    }

    public async Task<IReadOnlyList<GameServerNodeDescriptor>> DiscoverHealthyNodesAsync(
        TimeSpan maximumAge)
    {
        if (maximumAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumAge));

        RedisResult redisResult = await redisPool.ExecuteWithRetryAsync(
            database => database.ScriptEvaluateAsync(
                DiscoverNodeHeartbeatsScript,
                [GameServerRoutingKeys.NodeHeartbeatIndex],
                [checked((long)maximumAge.TotalMilliseconds)],
                CommandFlags.DemandMaster));
        RedisResult[] nodeIds = (RedisResult[])redisResult!;
        var result = new List<GameServerNodeDescriptor>(nodeIds.Length);
        foreach (RedisResult rawNodeId in nodeIds)
        {
            string nodeId = rawNodeId.ToString();
            if (string.IsNullOrWhiteSpace(nodeId))
                continue;
            var serialized = await cacheHelper.StringGetAsync(GameServerRoutingKeys.NodeDescriptor(nodeId));
            if (serialized.IsNullOrEmpty)
                continue;

            GameServerNodeDescriptor descriptor;
            try
            {
                descriptor = MessagePackSerializer.Deserialize<GameServerNodeDescriptor>((byte[])serialized!);
            }
            catch
            {
                continue;
            }

            if (descriptor.Status != GameServerNodeStatus.Accepting)
                continue;

            var lease = await cacheHelper.StringGetAsync(GameServerRoutingKeys.NodeLease(nodeId));
            var accepting = await cacheHelper.StringGetAsync(GameServerRoutingKeys.NodeAccepting(nodeId));
            if (lease.IsNullOrEmpty || accepting.IsNullOrEmpty ||
                !string.Equals(lease.ToString(), descriptor.Generation, StringComparison.Ordinal) ||
                !string.Equals(accepting.ToString(), descriptor.Generation, StringComparison.Ordinal))
                continue;

            result.Add(descriptor);
        }

        return result
            .OrderBy(node => (double)node.ActiveMatchCount / node.MaxConcurrentMatches)
            .ThenBy(node => node.NodeId, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<GameServerMatchOwner?> TryReserveMatchAsync(
        long matchingId,
        GameServerNodeDescriptor node,
        TimeSpan ownerLifetime)
    {
        ValidateDescriptor(node);
        if (node.Status != GameServerNodeStatus.Accepting)
            return null;

        long fence = await cacheHelper.TryReserveGameServerMatchOwnerAsync(
            GameServerRoutingKeys.NodeLease(node.NodeId),
            GameServerRoutingKeys.NodeAccepting(node.NodeId),
            node.Generation,
            GameServerRoutingKeys.NodeSlots(node.NodeId, node.Generation),
            GameServerRoutingKeys.MatchOwner(matchingId),
            GameServerRoutingKeys.OwnerFence,
            node.NodeId,
            matchingId,
            node.MaxConcurrentMatches,
            ownerLifetime);
        return fence > 0
            ? new GameServerMatchOwner(matchingId, node.NodeId, node.Generation, fence)
            : null;
    }

    public Task<bool> RenewMatchOwnerAsync(GameServerMatchOwner owner, TimeSpan ownerLifetime)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!owner.IsValid)
            return Task.FromResult(false);

        return cacheHelper.RenewGameServerMatchOwnerAsync(
            GameServerRoutingKeys.MatchOwner(owner.MatchingId),
            owner.Token,
            GameServerRoutingKeys.NodeSlots(owner.NodeId, owner.Generation),
            owner.MatchingId,
            ownerLifetime);
    }

    public async Task<bool> ClampMatchOwnerLifetimeAsync(
        GameServerMatchOwner owner,
        TimeSpan maximumRemainingLifetime)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!owner.IsValid)
            return false;
        if (maximumRemainingLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumRemainingLifetime));
        long maximumRemainingMilliseconds = checked(
            (long)Math.Ceiling(maximumRemainingLifetime.TotalMilliseconds));

        RedisResult result = await redisPool.ExecuteWithRetryAsync(
            database => database.ScriptEvaluateAsync(
                ClampMatchOwnerLifetimeScript,
                [
                    GameServerRoutingKeys.MatchOwner(owner.MatchingId),
                    GameServerRoutingKeys.NodeSlots(owner.NodeId, owner.Generation)
                ],
                [
                    owner.Token,
                    owner.MatchingId,
                    maximumRemainingMilliseconds
                ],
                CommandFlags.DemandMaster));
        return (long)result == 1;
    }

    public Task<bool> ReleaseMatchOwnerAsync(GameServerMatchOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!owner.IsValid)
            return Task.FromResult(false);

        return cacheHelper.ReleaseGameServerMatchOwnerAsync(
            GameServerRoutingKeys.MatchOwner(owner.MatchingId),
            owner.Token,
            GameServerRoutingKeys.NodeSlots(owner.NodeId, owner.Generation),
            owner.MatchingId);
    }

    public async Task<bool> IsMatchOwnerActiveAsync(GameServerMatchOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!owner.IsValid)
            return false;

        RedisResult result = await redisPool.ExecuteWithRetryAsync(
            database => database.ScriptEvaluateAsync(
                MatchOwnerActiveScript,
                [
                    GameServerRoutingKeys.MatchOwner(owner.MatchingId),
                    GameServerRoutingKeys.NodeLease(owner.NodeId)
                ],
                [owner.Token, owner.Generation],
                CommandFlags.DemandMaster));
        return (long)result == 1;
    }

    public async Task<int> GetOwnedMatchCountAsync(GameServerNodeIdentity identity)
    {
        if (!identity.IsValid)
            return 0;

        RedisResult result = await redisPool.ExecuteWithRetryAsync(
            database => database.ScriptEvaluateAsync(
                CountActiveNodeSlotsScript,
                [GameServerRoutingKeys.NodeSlots(identity.NodeId, identity.Generation)],
                [],
                CommandFlags.DemandMaster));
        return checked((int)(long)result);
    }

    private Task<bool> PublishDescriptorAsync(GameServerNodeDescriptor descriptor, TimeSpan leaseLifetime)
    {
        byte[] serialized = MessagePackSerializer.Serialize(descriptor);
        return cacheHelper.StringSetWithExpiryIfGuardEqualsAsync(
            GameServerRoutingKeys.NodeDescriptor(descriptor.NodeId),
            serialized,
            leaseLifetime,
            GameServerRoutingKeys.NodeLease(descriptor.NodeId),
            descriptor.Generation);
    }

    private async Task PublishAcceptingStateAsync(
        GameServerNodeDescriptor descriptor,
        TimeSpan leaseLifetime)
    {
        string acceptingKey = GameServerRoutingKeys.NodeAccepting(descriptor.NodeId);
        if (descriptor.Status == GameServerNodeStatus.Accepting)
        {
            bool published = await cacheHelper.StringSetWithExpiryIfGuardEqualsAsync(
                acceptingKey,
                descriptor.Generation,
                leaseLifetime,
                GameServerRoutingKeys.NodeLease(descriptor.NodeId),
                descriptor.Generation);
            if (!published)
                throw new InvalidOperationException("Node lease changed while publishing accepting state.");
            return;
        }

        await cacheHelper.StringDeleteIfEqualsAsync(acceptingKey, descriptor.Generation);
    }

    private async Task PublishHeartbeatIndexAsync(GameServerNodeDescriptor descriptor)
    {
        RedisResult result = await redisPool.ExecuteWithRetryAsync(
            database => database.ScriptEvaluateAsync(
                PublishNodeHeartbeatScript,
                [
                    GameServerRoutingKeys.NodeLease(descriptor.NodeId),
                    GameServerRoutingKeys.NodeHeartbeatIndex
                ],
                [descriptor.Generation, descriptor.NodeId],
                CommandFlags.DemandMaster));
        if ((long)result != 1)
            throw new InvalidOperationException("Node lease changed while publishing its heartbeat index.");
    }

    private static void ValidateDescriptor(GameServerNodeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (string.IsNullOrWhiteSpace(descriptor.NodeId) ||
            string.IsNullOrWhiteSpace(descriptor.Generation) ||
            string.IsNullOrWhiteSpace(descriptor.PublicHost) ||
            descriptor.PublicPort is <= 0 or > ushort.MaxValue ||
            descriptor.MaxConcurrentMatches <= 0)
        {
            throw new ArgumentException("GameServer node descriptor is invalid.", nameof(descriptor));
        }
    }
}
