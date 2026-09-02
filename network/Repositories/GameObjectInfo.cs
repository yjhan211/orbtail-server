using network.interfaces;

// ReSharper disable once CheckNamespace
namespace network.common.data.models;

/// <summary>
///     GameObjectInfo의 Redis 파셜. ObjectInfo는 Hash에 저장하지 않고 PlayerInfo.Load가 런타임 재구성하므로
///     Load/Save는 호출처가 없어 삭제했다 (#335). Delete만 PlayerInfo.Delete 경로에 남는다.
/// </summary>
public partial class GameObjectInfo
{
    public static async Task Delete(ICacheHelper cacheHelper, string hashField)
    {
        await cacheHelper.HashDeleteAsync(HashKey, hashField);
    }

    public async Task Delete(ICacheHelper cacheHelper)
    {
        await cacheHelper.HashDeleteAsync(HashKey, GetGameObjectKey());
    }
}
