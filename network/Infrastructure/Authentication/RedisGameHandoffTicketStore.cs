using MessagePack;
using network.contracts.authentication;
using network.contracts.scaling;
using network.interfaces;
using StackExchange.Redis;

namespace network.infrastructure.authentication;

public sealed class RedisGameHandoffTicketStore(ICacheHelper cacheHelper) : IGameHandoffTicketStore
{
    private const string TicketKeyPrefix = "game_handoff_ticket:";
    private const int ConsumeResolutionAttempts = 3;

    private static readonly MessagePackSerializerOptions SerializerOptions =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    public Task<bool> TryStoreAsync(string ticketHash, GameHandoffContext context, TimeSpan lifetime)
    {
        byte[] serializedContext = MessagePackSerializer.Serialize(context, SerializerOptions);
        return cacheHelper.StringSetIfNotExistsAsync(
            context.HasGameServerOwner
                ? GameServerRoutingKeys.OwnedHandoffTicket(ticketHash)
                : MakeLegacyKey(ticketHash),
            serializedContext,
            lifetime);
    }

    public async Task<GameHandoffContext?> ConsumeAsync(string ticketHash)
    {
        RedisValue serializedContext = await cacheHelper.StringGetDeleteAsync(MakeLegacyKey(ticketHash));
        if (serializedContext.IsNullOrEmpty)
            return null;

        return MessagePackSerializer.Deserialize<GameHandoffContext>(
            (byte[])serializedContext!,
            SerializerOptions);
    }

    public async Task<GameHandoffContext?> PeekOwnedAsync(string ticketHash)
    {
        RedisValue serializedContext =
            await cacheHelper.StringGetAsync(GameServerRoutingKeys.OwnedHandoffTicket(ticketHash));
        return Deserialize(serializedContext);
    }

    public async Task<GameHandoffContext?> ConsumeOwnedAsync(
        string ticketHash,
        string consumeNonce,
        GameServerNodeIdentity identity,
        GameServerMatchOwner owner,
        TimeSpan provisionalOwnerLifetime,
        TimeSpan receiptLifetime)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(owner);
        if (!identity.IsValid || !owner.IsValid ||
            string.IsNullOrWhiteSpace(consumeNonce) || consumeNonce.Length > 128 ||
            provisionalOwnerLifetime <= TimeSpan.Zero ||
            receiptLifetime <= TimeSpan.Zero ||
            !string.Equals(identity.NodeId, owner.NodeId, StringComparison.Ordinal) ||
            !string.Equals(identity.Generation, owner.Generation, StringComparison.Ordinal))
        {
            return null;
        }

        string ticketKey = GameServerRoutingKeys.OwnedHandoffTicket(ticketHash);
        string receiptKey = GameServerRoutingKeys.OwnedHandoffConsumeReceipt(
            ticketHash,
            consumeNonce);
        List<Exception>? ambiguousExceptions = null;
        for (int attempt = 1; attempt <= ConsumeResolutionAttempts; attempt++)
        {
            try
            {
                RedisValue serializedContext =
                    await cacheHelper.StringGetDeleteIfGuardsEqualWithReceiptAsync(
                        ticketKey,
                        GameServerRoutingKeys.NodeLease(owner.NodeId),
                        identity.Generation,
                        GameServerRoutingKeys.MatchOwner(owner.MatchingId),
                        owner.Token,
                        provisionalOwnerLifetime,
                        GameServerRoutingKeys.NodeSlots(owner.NodeId, owner.Generation),
                        owner.MatchingId,
                        receiptKey,
                        consumeNonce,
                        receiptLifetime);
                return DeserializeOwnedContextOrNull(serializedContext, identity, owner);
            }
            catch (Exception consumeException) when (
                consumeException is RedisTimeoutException or RedisConnectionException)
            {
                ambiguousExceptions ??= [];
                ambiguousExceptions.Add(consumeException);
                if (attempt < ConsumeResolutionAttempts)
                    await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt));
            }
        }

        for (int attempt = 1; attempt <= ConsumeResolutionAttempts; attempt++)
        {
            try
            {
                RedisValue reconciledContext =
                    await cacheHelper.StringReconcileConsumeReceiptAsync(
                        ticketKey,
                        receiptKey,
                        consumeNonce,
                        receiptLifetime);
                return DeserializeOwnedContextOrNull(reconciledContext, identity, owner);
            }
            catch (Exception reconcileException) when (
                reconcileException is RedisTimeoutException or RedisConnectionException)
            {
                ambiguousExceptions!.Add(reconcileException);
                if (attempt < ConsumeResolutionAttempts)
                    await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt));
            }
        }

        throw new InvalidOperationException(
            "An owner-bound handoff consume result could not be resolved or causally aborted.",
            new AggregateException(ambiguousExceptions!));
    }

    private static GameHandoffContext? Deserialize(RedisValue serializedContext)
    {
        if (serializedContext.IsNullOrEmpty)
            return null;
        try
        {
            return MessagePackSerializer.Deserialize<GameHandoffContext>(
                (byte[])serializedContext!,
                SerializerOptions);
        }
        catch (MessagePackSerializationException ex)
        {
            throw new InvalidOperationException("Redis contains a malformed game handoff context.", ex);
        }
    }

    private static GameHandoffContext? DeserializeOwnedContext(
        RedisValue serializedContext,
        GameServerNodeIdentity identity,
        GameServerMatchOwner owner)
    {
        GameHandoffContext? context = Deserialize(serializedContext);
        if (context == null)
            return null;

        GameServerMatchOwner? contextOwner = context.GetGameServerOwner();
        if (contextOwner != owner ||
            !string.Equals(contextOwner.NodeId, identity.NodeId, StringComparison.Ordinal) ||
            !string.Equals(contextOwner.Generation, identity.Generation, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Redis returned a handoff consume receipt for a different GameServer owner.");
        }

        return context;
    }

    private static GameHandoffContext? DeserializeOwnedContextOrNull(
        RedisValue serializedContext,
        GameServerNodeIdentity identity,
        GameServerMatchOwner owner)
    {
        if (serializedContext.IsNull)
            return null;

        return DeserializeOwnedContext(serializedContext, identity, owner)
               ?? throw new InvalidOperationException(
                   "Redis returned an empty owner-bound handoff consume context.");
    }

    private static string MakeLegacyKey(string ticketHash)
    {
        return TicketKeyPrefix + ticketHash;
    }
}
