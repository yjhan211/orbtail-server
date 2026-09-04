namespace network.gamehandoff;

public interface IGameHandoffTicketStore
{
    public Task<bool> TryStoreAsync(string ticketHash, GameHandoffContext context, TimeSpan lifetime);
    public Task<GameHandoffContext?> ConsumeAsync(string ticketHash);
}
