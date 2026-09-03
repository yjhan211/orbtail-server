using MessagePack;
using StackExchange.Redis;
using network.interfaces;

namespace network.infrastructure.routing;

/// <summary>
///     Redis Hash 하나(<c>game_server:nodes</c>)에 노드별 descriptor를 두는 레지스트리.
///     필드 단위 TTL이 없으므로 신선도는 descriptor의 하트비트 시각으로 판정하고, 정상 종료한 노드만 자기 필드를 지운다.
///     비정상 종료로 남은 항목은 하트비트가 낡아 무시된다.
/// </summary>
public sealed class RedisGameServerRegistry(ICacheHelper cacheHelper) : IGameServerRegistry
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
        return cacheHelper.HashSetAsync(NodesKey, descriptor.NodeId, serialized);
    }

    public Task RemoveAsync(string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        return cacheHelper.HashDeleteAsync(NodesKey, nodeId);
    }

    public async Task<IReadOnlyList<GameServerNodeDescriptor>> DiscoverAsync()
    {
        HashEntry[] entries = await cacheHelper.HashGetAllAsync(NodesKey);
        var descriptors = new List<GameServerNodeDescriptor>(entries.Length);
        foreach (HashEntry entry in entries)
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

            if (descriptor == null || !descriptor.IsValid() || descriptor.NodeId != entry.Name.ToString())
                continue;
            descriptors.Add(descriptor);
        }

        return descriptors;
    }
}
