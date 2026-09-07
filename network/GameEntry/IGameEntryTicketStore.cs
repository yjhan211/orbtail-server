namespace network.gameentry;

/// <summary>
///     Game Server 입장 티켓을 유효 기간과 함께 저장하고, 한 번만 꺼내 사용하는 저장소.
///     티켓 생성과 검증은 GameEntryTicketService가, 실제 저장과 일회용 소비는 구현체가 담당한다.
///     테스트에서는 Redis 없이 메모리 저장소로 대체할 수 있다.
/// </summary>
public interface IGameEntryTicketStore
{
    public Task<bool> TryStoreAsync(string ticketHash, GameEntryContext context, TimeSpan lifetime);
    public Task<GameEntryContext?> ConsumeAsync(string ticketHash);
}
