using MessagePack;
using network.infrastructure.redis;
using network.routing;

namespace network.infrastructure.routing;

/// <summary>
///     Redis에서 GameServerNodeDescriptor를 저장·조회·삭제한다.
/// </summary>
public sealed class RedisGameServerRegistry(IRedisOperations redisOperations) : IGameServerRegistry
{
    public const string NodesKey = "game_server:nodes";

    private static readonly MessagePackSerializerOptions SerializerOptions =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    public Task PublishAsync(GameServerNodeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!descriptor.IsValid())
            throw new ArgumentException("A game server node descriptor must carry an id, address, capacity, and heartbeat.", nameof(descriptor));

        byte[] serialized = MessagePackSerializer.Serialize(descriptor, SerializerOptions);
        return redisOperations.HashSetAsync(NodesKey, descriptor.NodeId, serialized);
    }

    public Task RemoveAsync(string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        return redisOperations.HashDeleteAsync(NodesKey, nodeId);
    }

    public async Task<IReadOnlyList<GameServerNodeDescriptor>> DiscoverAsync()
    {
        var entries = await redisOperations.HashGetAllAsync(NodesKey);
        var descriptors = new List<GameServerNodeDescriptor>(entries.Length);
        foreach (var entry in entries)
        {
            if (entry.Value.IsNullOrEmpty) continue;

            GameServerNodeDescriptor? descriptor;
            try
            {
                descriptor = MessagePackSerializer.Deserialize<GameServerNodeDescriptor>(
                    (byte[])entry.Value!,
                    SerializerOptions);
            }
            catch (MessagePackSerializationException)
            {
                continue;
            }

            if (!descriptor.IsValid() || descriptor.NodeId != entry.Name.ToString())
                continue;
            descriptors.Add(descriptor);
        }

        return descriptors;
    }
}
