using network.helpers;
using network.interfaces;

namespace network.gamehandoff;

public sealed class GameHandoffTicketOptions
{
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(3);
}

public sealed class GameHandoffTicketService(
    IGameHandoffTicketStore ticketStore,
    GameHandoffTicketOptions options) : IGameHandoffTicketService
{
    private const string TicketPrefix = "game_";
    private const int MaxTicketGenerationAttempts = 5;

    public async Task<string> IssueAsync(GameHandoffContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!TryValidateContext(context, out string validationError))
            throw new ArgumentException(validationError, nameof(context));

        for (int attempt = 0; attempt < MaxTicketGenerationAttempts; attempt++)
        {
            string ticket = OpaqueTokenCodec.Create(TicketPrefix);
            string ticketHash = OpaqueTokenCodec.Fingerprint(ticket);
            if (await ticketStore.TryStoreAsync(ticketHash, context, options.Lifetime))
                return ticket;
        }

        throw new InvalidOperationException("A unique game handoff ticket could not be issued.");
    }

    public async Task<GameHandoffContext?> ConsumeAsync(string? ticket, string gameServerNodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameServerNodeId);
        if (string.IsNullOrWhiteSpace(ticket))
            return null;

        string normalizedTicket = ticket.Trim();
        if (!OpaqueTokenCodec.IsValid(normalizedTicket, TicketPrefix))
            return null;

        GameHandoffContext? context =
            await ticketStore.ConsumeAsync(OpaqueTokenCodec.Fingerprint(normalizedTicket));
        if (context == null || !TryValidateContext(context, out _))
            return null;

        // 노드 결합: 다른 노드에 배정된 매치의 ticket은 여기서 끝난다 (fail closed).
        return string.Equals(context.GameServerNodeId, gameServerNodeId, StringComparison.Ordinal)
            ? context
            : null;
    }

    private static bool TryValidateContext(GameHandoffContext context, out string error)
    {
        if (context.PlayerId <= 0 || context.MatchingId <= 0)
        {
            error = "A game handoff requires a valid owner and match.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(context.GameServerNodeId))
        {
            error = "A game handoff must be bound to the game server node that owns the match.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
