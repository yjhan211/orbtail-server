using network.common;
using network.common.data.models;
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
}