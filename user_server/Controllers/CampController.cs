using network.common;
using network.packets;

namespace user_server.controllers
{
    public static class CampController
    {
        public static async Task GetCampInfo(GameUser user, C_TO_U_CAMP_INFO body)
        {
            var campIdList = body.CampInfoList;
            var campInfoList = new List<CampInfo>();

            for (int i = 0; i < campIdList.Count; i++)
            {
                CampInfo? targetCampInfo = await CampInfo.Load(campIdList[i]);
                if (targetCampInfo == null)
                {
                    continue;
                }

                campInfoList.Add(targetCampInfo);

                bool isMax = campInfoList.Count >= Config.BROADCAST_UNIT;
                bool isEnded = i == (campInfoList.Count - 1);

                if (isMax || isEnded)
                {
                    using var packet = PacketMaker.U_TO_C_CAMP_INFO(campInfoList);
                    user.Send(packet);
                }
            }
        }
    }
}
