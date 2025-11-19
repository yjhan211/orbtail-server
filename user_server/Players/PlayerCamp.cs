using network.common;
using network.common.data.models;
using network.interfaces;

namespace user_server.players;

public class PlayerCamp(GameUser user, PlayerInfo playerInfo)
{
    private readonly IRedLockFactory _redLock = user.RedLock;
    private readonly BroadcastDelegate<CampInfo> _broadcastCampInfo = user.BroadcastUpdateInfo;

    public async Task PutItem(C_TO_U_ITEM_PUT body)
    {
        await using (await PlayerInfo.Lock(_redLock, playerInfo.PlayerId))
        {
            if (!playerInfo.InventoryInfo.ItemDict.TryGetValue(body.ItemUid, out _))
            {
                throw new Exception("not found item info");
            }
            
            if (!playerInfo.CampInfo.IsIntall)
            {
                throw new Exception("not encamp");
            }

            if (playerInfo.CampInfo.InteractPropDict.Values.Any((e) => e.InteractPropUid == body.ItemUid))
            {
                throw new Exception($"already put item: {body.ItemUid}");
            }

            if (playerInfo.CampInfo.InteractPropDict.ContainsKey(body.Cell))
            {
                throw new Exception($"already cell full: {body.Cell.X}, {body.Cell.Y}");
            }

            var gameObjectInfo = new GameObjectInfo(ObjectType.INTERACTPROP, body.ItemUid, playerInfo.ObjectInfo.MapId,
                playerInfo.ObjectInfo.MapSubId, body.Cell);
            var interactPropInfo = new InteractPropInfo(gameObjectInfo, body.ItemUid);

            playerInfo.CampInfo.InteractPropDict.Add(body.Cell, interactPropInfo);
            await playerInfo.CampInfo.Save(user.CacheHelper);
        }
        _broadcastCampInfo(playerInfo.CampInfo);
    }
}
