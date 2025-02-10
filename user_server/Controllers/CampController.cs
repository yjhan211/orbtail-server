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

            if (await CampInfo.Load(user.PlayerId) != null)
            {
                throw new Exception("already encamp");
            }

            campInfo = new CampInfo(playerInfo.PlayerId, playerInfo.Name, playerInfo.ObjectInfo, targetItem, playerInfo.ObjectInfo.TargetCell);
            await campInfo.Save();
        }
        
        var isCommonMap = GameMapData.IsCommonMap(campInfo.ObjectInfo.MapId);
        var managePartKey = isCommonMap ? MapHelper.CreatePartKey(campInfo.ObjectInfo.MapId, campInfo.ObjectInfo.CurrentCell) : MapHelper.CreatePartKey(campInfo.ObjectInfo.MapId, campInfo.ObjectInfo.MapSubId);
        var manageServer = isCommonMap ? MapHelper.GetManageServerId(managePartKey) : MapHelper.GetManageServerId(campInfo.ObjectInfo.MapSubId);
        var subject = SubjectHelper.GetUpdateManageSubject(campInfo.ObjectInfo, manageServer);
        
        user.NatsClient.Publish(subject, MessagePackSerializer.Serialize((managePartKey, campInfo.ObjectInfo)));
        user.BroadcastUpdateInfo(campInfo);
    }
    
    public static async Task Decamp(GameUser user)
    {
        CampInfo? campInfo;
        await using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
        {
            if (user.PlayerManager.PlayerInfo == null)
            {
                return;
            }

            campInfo = await CampInfo.Load(user.PlayerId);
            if (campInfo == null)
            {
                throw new Exception("not encamp");
            }

            await campInfo.Delete();
        }
        user.BroadcastObjectDestroy(campInfo.ObjectInfo);
    }
}