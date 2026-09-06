namespace user_server.matching.creation;

/// <summary>
///     매치 하나가 배정된 Game Server. 클라이언트에는 주소가, ticket에는 노드 ID가 실린다.
/// </summary>
internal sealed record GameServerAllocation(string PublicHost, int PublicPort, string NodeId);

/// <summary>
///     새 매치를 받을 GameServer를 선택한다.
///     매치 생성 로직을 실제 서버 조회·선택 없이 테스트할 수 있도록 인터페이스로 분리.
/// </summary>
internal interface IGameServerAllocator
{
    public Task<GameServerAllocation?> TryAllocateAsync();
}
