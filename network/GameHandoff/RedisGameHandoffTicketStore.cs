using MessagePack;
using network.interfaces;
using StackExchange.Redis;

namespace network.gamehandoff;

/// <summary>
///     Game Server 입장용 ticket 정보를 Redis에 저장하고 한 번만 꺼낼 수 있도록 관리한다.
///     ticket 원문 대신 fingerprint를 Redis 키로 사용하고, 입장 정보는 MessagePack으로 직렬화해 저장한다.
///     발급할 때는 SET NX와 TTL을 적용해 중복 저장과 영구 잔존을 막고,
///     소비할 때는 GETDEL로 값을 읽으면서 삭제해 같은 ticket의 재사용을 막는다.
/// </summary>
public sealed class RedisGameHandoffTicketStore(IRedisOperations redisOperations) : IGameHandoffTicketStore
{
    private const string TicketKeyPrefix = "game_handoff_ticket:";

    private static readonly TimeSpan MinimumRedisLifetime = TimeSpan.FromMilliseconds(1);

    private static readonly MessagePackSerializerOptions SerializerOptions =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    public Task<bool> TryStoreAsync(string ticketHash, GameHandoffContext context, TimeSpan lifetime)
    {
        ValidateRedisLifetime(lifetime, nameof(lifetime));
        byte[] serializedContext = MessagePackSerializer.Serialize(context, SerializerOptions);
        return redisOperations.StringSetIfNotExistsAsync(MakeKey(ticketHash), serializedContext, lifetime);
    }

    public async Task<GameHandoffContext?> ConsumeAsync(string ticketHash)
    {
        var serializedContext = await redisOperations.StringGetDeleteAsync(MakeKey(ticketHash));
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
