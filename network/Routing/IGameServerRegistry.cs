namespace network.routing;

public interface IGameServerRegistry
{
    /// <summary>descriptor를 레지스트리에 쓴다(있으면 덮어쓴다).</summary>
    public Task PublishAsync(GameServerNodeDescriptor descriptor);

    /// <summary>노드 항목을 지운다. 정상 종료 마지막 단계에서 부른다.</summary>
    public Task RemoveAsync(string nodeId);

    /// <summary>레지스트리의 모든 항목. 손상된 항목은 건너뛴다. 신선도 판정은 호출자가 한다.</summary>
    public Task<IReadOnlyList<GameServerNodeDescriptor>> DiscoverAsync();
}
