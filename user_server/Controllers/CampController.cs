using MessagePack;
using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;
using network.packets;

namespace user_server.controllers;

public static class CampController
{
    public static async Task GetCampInfo(GameUser user, C_TO_U_CAMP_INFO body)
    {
        var campIdList = body.CampInfoList;
        var campInfoList = new List<CampInfo>();

        for (var i = 0; i < campIdList.Count; i++)
        {
            var targetCampInfo = await CampInfo.Load(campIdList[i]);
            if (targetCampInfo == null) continue;

            campInfoList.Add(targetCampInfo);

            var isMax = campInfoList.Count >= Config.BROADCAST_UNIT;
            var isEnded = i == campInfoList.Count - 1;
            if (!isMax && !isEnded) continue;

            using var packet = PacketMaker.U_TO_C_CAMP_INFO(campInfoList);
            user.Send(packet);
        }
    }

    public static async Task Encamp(GameUser user, C_TO_U_ENCAMP body)
    {
        await using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
        {
            if (user.PlayerManager.PlayerInfo == null)
            {
                return;
            }

            if (!user.PlayerManager.PlayerInfo.InventoryInfo.ItemDict.TryGetValue(body.ItemUid, out var targetItem))
            {
                throw new Exception("not found item info");
            }

            if (user.PlayerManager.PlayerInfo.CampInfo.IsIntall)
            {
                throw new Exception("already encamp");
            }
            
            user.PlayerManager.PlayerInfo.CampInfo.IsIntall = true;
            user.PlayerManager.PlayerInfo.CampInfo.PlayerName = user.PlayerManager.PlayerInfo.Name;
            user.PlayerManager.PlayerInfo.CampInfo.ObjectInfo.MapId = user.PlayerManager.PlayerInfo.ObjectInfo.MapId;
            user.PlayerManager.PlayerInfo.CampInfo.ObjectInfo.MapSubId = user.PlayerManager.PlayerId;
            user.PlayerManager.PlayerInfo.CampInfo.ObjectInfo.CurrentCell = user.PlayerManager.PlayerInfo.ObjectInfo.CurrentCell.Clone();
            user.PlayerManager.PlayerInfo.CampInfo.ObjectInfo.TargetCell = user.PlayerManager.PlayerInfo.ObjectInfo.CurrentCell.Clone();
            user.PlayerManager.PlayerInfo.CampInfo.ObjectInfo.IsFlip = user.PlayerManager.PlayerInfo.ObjectInfo.IsFlip;
            await user.PlayerManager.PlayerInfo.CampInfo.Save();
        }

        var campInfo = user.PlayerManager.PlayerInfo.CampInfo;
        var isCommonMap = GameMapData.IsCommonMap(campInfo.ObjectInfo.MapId);
        var managePartKey = isCommonMap ? MapHelper.CreatePartKey(campInfo.ObjectInfo.MapId, campInfo.ObjectInfo.CurrentCell) : MapHelper.CreatePartKey(campInfo.ObjectInfo.MapId, campInfo.ObjectInfo.MapSubId);
        var manageServer = isCommonMap ? MapHelper.GetManageServerId(managePartKey) : MapHelper.GetManageServerId(campInfo.ObjectInfo.MapSubId);
        var subject = SubjectHelper.GetUpdateManageSubject(campInfo.ObjectInfo, manageServer);
        
        user.NatsClient.Publish(subject, MessagePackSerializer.Serialize((managePartKey, campInfo.ObjectInfo)));
        user.BroadcastUpdateInfo(campInfo);
    }
    
    public static async Task Decamp(GameUser user)
    {
        await using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
        {
            if (user.PlayerManager.PlayerInfo == null)
            {
                return;
            }
            if (!user.PlayerManager.PlayerInfo.CampInfo.IsIntall)
            {
                throw new Exception("not encamp");
            }
            user.PlayerManager.PlayerInfo.CampInfo.IsIntall = false;
            await user.PlayerManager.PlayerInfo.CampInfo.Save();
        }
        user.BroadcastObjectDestroy(user.PlayerManager.PlayerInfo.CampInfo.ObjectInfo);
    }

    public static async Task PutItem(GameUser user, C_TO_U_ITEM_PUT body)
    {
        CampInfo? campInfo;
        await using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
        {
            if (user.PlayerManager.PlayerInfo == null)
            {
                return;
            }

            var playerInfo = user.PlayerManager.PlayerInfo;
            if (!playerInfo.InventoryInfo.ItemDict.TryGetValue(body.ItemUid, out var targetItem))
            {
                throw new Exception("not found item info");
            }

            campInfo = await CampInfo.Load(user.PlayerId);
            if (campInfo == null)
            {
                throw new Exception("not encamp");
            }

            if (campInfo.InteractPropDict.Values.Any((e) => e.InteractPropUid == body.ItemUid))
            {
                throw new Exception($"already put item: {body.ItemUid}");
            }

            if (campInfo.InteractPropDict.ContainsKey(body.Cell))
            {
                throw new Exception($"already cell full: {body.Cell.X}, {body.Cell.Y}");
            }

            var gameObjectInfo = new GameObjectInfo(ObjectType.INTERACTPROP, body.ItemUid, playerInfo.ObjectInfo.MapId,
                playerInfo.ObjectInfo.MapSubId, body.Cell, false);
            var interactPropInfo = new InteractPropInfo(gameObjectInfo, body.ItemUid);

            campInfo.InteractPropDict.Add(body.Cell, interactPropInfo);
            await campInfo.Save();
        }
        user.BroadcastUpdateInfo(campInfo);
    }
}