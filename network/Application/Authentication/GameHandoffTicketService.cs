using network.contracts.authentication;
using network.contracts.scaling;
using network.core.security;

namespace network.application.authentication;

public sealed class GameHandoffTicketOptions
{
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(3);
}

public sealed class GameHandoffTicketService(
    IGameHandoffTicketStore ticketStore,
    GameHandoffTicketOptions options) : IGameHandoffTicketService
{
    private const string TicketPrefix = "game_";
    private const string ConsumeNoncePrefix = "consume_";
    private const int MaxTicketGenerationAttempts = 5;
    private static readonly TimeSpan ConsumeReceiptLifetime = TimeSpan.FromMinutes(5);

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

    public async Task<GameHandoffContext?> ConsumeAsync(string? ticket)
    {
        if (string.IsNullOrWhiteSpace(ticket))
            return null;

        string normalizedTicket = ticket.Trim();
        if (!OpaqueTokenCodec.IsValid(normalizedTicket, TicketPrefix))
            return null;

        GameHandoffContext? context =
            await ticketStore.ConsumeAsync(OpaqueTokenCodec.Fingerprint(normalizedTicket));
        return context != null && TryValidateContext(context, out _)
            ? context
            : null;
    }

    public async Task<GameHandoffContext?> ConsumeForOwnerAsync(
        string? ticket,
        GameServerNodeIdentity identity,
        TimeSpan provisionalOwnerLifetime)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!identity.IsValid || provisionalOwnerLifetime <= TimeSpan.Zero || string.IsNullOrWhiteSpace(ticket))
            return null;

        string normalizedTicket = ticket.Trim();
        if (!OpaqueTokenCodec.IsValid(normalizedTicket, TicketPrefix))
            return null;

        string ticketHash = OpaqueTokenCodec.Fingerprint(normalizedTicket);
        GameHandoffContext? pendingContext = await ticketStore.PeekOwnedAsync(ticketHash);
        if (pendingContext == null ||
            !TryValidateContext(pendingContext, out _) ||
            !TryGetExactOwner(pendingContext, identity, out GameServerMatchOwner? owner))
        {
            return null;
        }

        string consumeNonce = OpaqueTokenCodec.Create(ConsumeNoncePrefix);
        GameHandoffContext? consumedContext =
            await ticketStore.ConsumeOwnedAsync(
                ticketHash,
                consumeNonce,
                identity,
                owner!,
                provisionalOwnerLifetime,
                ConsumeReceiptLifetime);
        return consumedContext != null &&
               TryValidateContext(consumedContext, out _) &&
               TryGetExactOwner(consumedContext, identity, out GameServerMatchOwner? consumedOwner) &&
               consumedOwner == owner
            ? consumedContext
            : null;
    }

    private static bool TryValidateContext(GameHandoffContext context, out string error)
    {
        if (context.PlayerId <= 0 ||
            context.MatchingId <= 0 ||
            context.MapSubId != context.MatchingId ||
            context.MapId == global::network.common.MapId.None ||
            context.SpawnPosition == null)
        {
            error = "A game handoff requires a valid owner, match, map, and spawn.";
            return false;
        }

        if (context.ActiveBuffIds == null || context.HumanRoster == null || context.HumanRoster.Count == 0)
        {
            error = "A game handoff requires non-null buff and human roster collections.";
            return false;
        }

        bool hasAnyOwnerField =
            !string.IsNullOrWhiteSpace(context.GameServerNodeId) ||
            !string.IsNullOrWhiteSpace(context.GameServerGeneration) ||
            context.GameServerFence != 0;
        if (hasAnyOwnerField && !context.HasGameServerOwner)
        {
            error = "A game handoff owner binding must contain node, generation, and fence together.";
            return false;
        }

        var playerIds = new HashSet<long>();
        GameHandoffRosterEntry? ownerEntry = null;
        foreach (GameHandoffRosterEntry entry in context.HumanRoster)
        {
            if (entry == null ||
                entry.PlayerId <= 0 ||
                entry.TargetPlayerId == 0 ||
                !playerIds.Add(entry.PlayerId))
            {
                error = "A game handoff roster contains an invalid or duplicate entry.";
                return false;
            }

            if (entry.PlayerId == context.PlayerId)
                ownerEntry = entry;
        }

        if (ownerEntry == null ||
            ownerEntry.TargetPlayerId != context.TargetPlayerId)
        {
            error = "A game handoff roster must contain a matching ticket owner entry.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryGetExactOwner(
        GameHandoffContext context,
        GameServerNodeIdentity identity,
        out GameServerMatchOwner? owner)
    {
        owner = context.GetGameServerOwner();
        return owner is { IsValid: true } &&
               string.Equals(owner.NodeId, identity.NodeId, StringComparison.Ordinal) &&
               string.Equals(owner.Generation, identity.Generation, StringComparison.Ordinal);
    }
}
