namespace network.routing;

/// <summary>
///     GameServer 노드 정보를 등록·조회·삭제하는 인터페이스.
///     GameServer는 자신의 상태를 등록하고, UserServer는 등록된 노드를 조회해 매치를 배정한다.
///
///     테스트에서는 실제 Redis 없이 노드 목록과 등록·삭제 동작을 대체할 수 있다.
/// </summary>
public interface IGameServerRegistry
{
    public Task PublishAsync(GameServerNodeDescriptor descriptor);
    public Task RemoveAsync(string nodeId);
    public Task<IReadOnlyList<GameServerNodeDescriptor>> DiscoverAsync();
}
