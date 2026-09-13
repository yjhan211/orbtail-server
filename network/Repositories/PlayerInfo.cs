using MessagePack;
using network.common.data;
using network.infrastructure.redis;
using RedLockNet;
using StackExchange.Redis;

// ReSharper disable once CheckNamespace
namespace network.common.data.models;

/// <summary>
///     공용 PlayerInfo 모델(Common/Data/Models/PlayerInfo)에 서버 전용 저장·조회·삭제와 분산 잠금 기능을 추가한다.
///     Unity에는 Common 쪽 모델만 포함되므로 Redis 의존성은 서버에만 남는다.
///
///     Save는 인벤토리와 플레이어 정보를 각각 저장한다.
///     두 저장은 하나의 트랜잭션으로 묶여 있지 않다.
///
///     Load는 인벤토리를 별도로 조회한다. 위치는 저장하거나 복원하지 않는다.
///     로비 위치는 로그인 시, 매치 위치는 GameServer에서 별도로 정한다.
///     LoadAll은 플레이어 본체만 읽으며, 인벤토리는 조회하지 않는다.
///     잠금이 필요한 작업은 호출 측에서 Lock을 획득한 뒤 수행한다.
/// </summary>
public partial class PlayerInfo
{
    public static async Task<IRedLock> Lock(IRedLockFactory redLock, long playerId)
    {
        return await redLock.AcquireLockAsync(GetLockKey(playerId), Config.LOCK_TTL);
    }

    public async Task<IRedLock> Lock(IRedLockFactory redLock)
    {
        return await redLock.AcquireLockAsync(GetLockKey(), Config.LOCK_TTL);
    }

    public async Task Save(IRedisOperations redisOperations)
    {
        await InventoryInfo.Save(redisOperations);
        var profile = new StoredProfile
        {
            PlayerId = PlayerId, Name = Name, WearItemIdList = new List<int>(WearItemIdList),
            Gold = Gold, IsNew = IsNew
        };
        await redisOperations.HashSetAsync(HashKey, PlayerId, MessagePackSerializer.Serialize(profile));
    }

    public static async Task<PlayerInfo?> Load(IRedisOperations redisOperations, long playerId)
    {
        var serialized = await redisOperations.HashGetAsync(HashKey, playerId);
        if (serialized == RedisValue.Null) return null;

        var playerInfo = MessagePackSerializer.Deserialize<PlayerInfo>(serialized);
        playerInfo.ObjectInfo = null;
        playerInfo.State = PlayerState.IDLE;

        playerInfo.InventoryInfo = await InventoryInfo.Load(redisOperations, InventoryOwnerType.PLAYER, playerId) ??
                                   new InventoryInfo(InventoryOwnerType.PLAYER, playerId);

        return playerInfo;
    }

    public static async Task<List<PlayerInfo>> LoadAll(IRedisOperations redisOperations, RedisValue[] objectKeys)
    {
        var hashEntries = await redisOperations.HashGetAsync(HashKey, objectKeys);
        var hashStrings = hashEntries
            .Where(entry => entry != RedisValue.Null)
            .Select(entry => entry)
            .ToList();

        return hashStrings.Select(hashString =>
        {
            var player = MessagePackSerializer.Deserialize<PlayerInfo>(hashString);
            player.ObjectInfo = null;
            player.State = PlayerState.IDLE;
            return player;
        }).ToList();
    }

    // 공통 모델의 인게임 상태를 제외하고 영속 프로필만 저장한다.
    [MessagePackObject]
    public sealed class StoredProfile
    {
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("name")] public string Name { get; set; } = "";
        [Key("wearItemIdList")] public List<int> WearItemIdList { get; set; } = [];
        [Key("gold")] public long Gold { get; set; }
        [Key("isNew")] public bool IsNew { get; set; }
    }

    public async Task Delete(IRedisOperations redisOperations, PlayerInfo playerInfo)
    {
        await redisOperations.HashDeleteAsync(HashKey, playerInfo.PlayerId);
    }

    public static async Task Delete(IRedisOperations redisOperations, long playerId)
    {
        await InventoryInfo.Delete(redisOperations, InventoryOwnerType.PLAYER, playerId);
        await redisOperations.HashDeleteAsync(HashKey, playerId);
    }

}
