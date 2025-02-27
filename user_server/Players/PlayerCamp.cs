using network.common;
using network.common.data.models;
using network.interfaces;

namespace user_server.players;

public class PlayerCamp(GameUser user, PlayerInfo playerInfo)
{
    private readonly IRedLockFactory _redLock = user.RedLock;
    private readonly BroadcastDelegate<CampInfo> _broadcastCampInfo = user.BroadcastUpdateInfo;

    public async Task Encamp(C_TO_U_ENCAMP request)
    {
        await using (await PlayerInfo.Lock(_redLock, playerInfo.PlayerId))
        {
            if (!playerInfo.InventoryInfo.ItemDict.TryGetValue(request.ItemUid, out _))
            {
                throw new Exception("not found item info");
            }

            if (playerInfo.CampInfo.IsIntall)
            {
                throw new Exception("already encamp");
            }
           
            UpdateCampInfo();
            await playerInfo.CampInfo.Save(user.CacheHelper);
        }

        var campInfo = playerInfo.CampInfo;
        _broadcastCampInfo(campInfo);
    }
    
    private void UpdateCampInfo()
    {
        playerInfo.CampInfo.IsIntall = true;
        playerInfo.CampInfo.PlayerName = playerInfo.Name;
        playerInfo.CampInfo.ObjectInfo.MapId = playerInfo.ObjectInfo.MapId;
        playerInfo.CampInfo.ObjectInfo.MapSubId = playerInfo.PlayerId;
        playerInfo.CampInfo.ObjectInfo.CurrentCell = playerInfo.ObjectInfo.CurrentCell.Clone();
        playerInfo.CampInfo.ObjectInfo.TargetCell = playerInfo.ObjectInfo.CurrentCell.Clone();
        playerInfo.CampInfo.ObjectInfo.IsFlip = playerInfo.ObjectInfo.IsFlip;
    }
    
    public async Task Decamp()
    {
        await using (await PlayerInfo.Lock(user.RedLock, playerInfo.PlayerId))
        {
            if (!playerInfo.CampInfo.IsIntall)
            {
                return;
            }
            playerInfo.CampInfo.IsIntall = false;
            await playerInfo.CampInfo.Save(user.CacheHelper);
        }
        user.BroadcastObjectDestroy(playerInfo.CampInfo.ObjectInfo);
    }

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