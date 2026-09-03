using network.gamehandoff;

namespace network.interfaces;

public interface IGameHandoffTicketService
{
    public Task<string> IssueAsync(GameHandoffContext context);

    /// <summary>
    ///     ticket을 1회 소비한다. context가 <paramref name="gameServerNodeId" /> 노드의 것이 아니면 null —
    ///     ticket은 이미 소비됐으므로 잘못 들어온 클라이언트는 매칭을 다시 받아야 한다.
    /// </summary>
    public Task<GameHandoffContext?> ConsumeAsync(string? ticket, string gameServerNodeId);
}
