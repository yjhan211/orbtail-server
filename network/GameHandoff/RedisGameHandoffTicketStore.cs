using MessagePack;
using StackExchange.Redis;
using network.interfaces;

namespace network.gamehandoff;

/// <summary>
///     Persists one-time game handoff tickets. SET NX issues the ticket and GETDEL consumes it exactly once.
/// </summary>
public sealed class RedisGameHandoffTicketStore(ICacheHelper cacheHelper) : IGameHandoffTicketStore
{
    private const string TicketKeyPrefix = "game_handoff_ticket:";

    private static readonly TimeSpan MinimumRedisLifetime = TimeSpan.FromMilliseconds(1);

    private static readonly MessagePackSerializerOptions SerializerOptions =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    public Task<bool> TryStoreAsync(string ticketHash, GameHandoffContext context, TimeSpan lifetime)
    {
        ValidateRedisLifetime(lifetime, nameof(lifetime));
        byte[] serializedContext = MessagePackSerializer.Serialize(context, SerializerOptions);
        return cacheHelper.StringSetIfNotExistsAsync(MakeKey(ticketHash), serializedContext, lifetime);
    }

    public async Task<GameHandoffContext?> ConsumeAsync(string ticketHash)
    {
        RedisValue serializedContext = await cacheHelper.StringGetDeleteAsync(MakeKey(ticketHash));
        if (serializedContext.IsNullOrEmpty)
            return null;

        return MessagePackSerializer.Deserialize<GameHandoffContext>(
            (byte[])serializedContext!,
            SerializerOptions);
    }

    private static void ValidateRedisLifetime(TimeSpan lifetime, string parameterName)
    {
        if (lifetime < MinimumRedisLifetime)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Redis lifetimes must be at least {MinimumRedisLifetime.TotalMilliseconds:0} millisecond.");
        }
    }

    private static string MakeKey(string ticketHash)
    {
        return TicketKeyPrefix + ticketHash;
    }
}
