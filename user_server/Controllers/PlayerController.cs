using network.common;
using network.common.models;
using network.packets;
using StackExchange.Redis;

namespace user_server.controllers;

public static class PlayerController
{
    public static async Task SetName(GameUser user, C_TO_U_SET_NAME body)
    {
        var playerInfo = await PlayerInfo.Load(user.PlayerId);
        if (playerInfo == null) throw new Exception("not found player info");

        await using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
        {
            playerInfo.Name = body.Name;
            playerInfo.TutorialIndex = 3;
            await playerInfo.Save();
        }

        using var packet = PacketMaker.U_TO_C_SET_NAME(ErrorCode.SUCCESS, playerInfo);
        user.Send(packet);

        user.BroadcastUpdateInfo(playerInfo);
    }

    public static async Task GetPlayerInfo(GameUser user, C_TO_U_PLAYER_INFO body)
    {
        var keys = body.PlayerIdList.ConvertAll(x => (RedisValue)x).ToArray();
        var playerInfoList = await PlayerInfo.LoadAll(keys);

        using var packet = PacketMaker.U_TO_C_PLAYER_INFO(playerInfoList);
        user.Send(packet);
    }


    public static async Task UpdateTutorial(GameUser user)
    {
        var playerInfo = await PlayerInfo.Load(user.PlayerId);
        if (playerInfo == null) throw new Exception("not found player info");

        await using (await PlayerInfo.Lock(user.RedLock, user.PlayerId))
        {
            switch (playerInfo.TutorialIndex)
            {
                case 2:
                    return;

                case 26:
                    var item26 = await InventoryController.CreateItem(104000001, 1);
                    playerInfo.InventoryInfo.AddItem(item26);
                    playerInfo.TutorialIndex += 1;
                    await playerInfo.Save();
                    await InventoryController.GetCurrentItemList(user);
                    break;

                case 42:
                    playerInfo.TutorialIndex += 1;
                    var jobType = JobType.NONE;
                    foreach (var jobStat in playerInfo.JobInfo.JobStatDict)
                        if (jobStat.Value.Exp >= 100)
                        {
                            jobType = jobStat.Key;
                            break;
                        }

                    if (jobType == JobType.NONE) return;

                    playerInfo.JobInfo.JobStatDict[jobType].JobGrade = JobGrade.RESEARCHER;
                    playerInfo.JobInfo.JobStatDict[jobType].Exp = 0;
                    await playerInfo.Save();
                    break;

                default:
                    playerInfo.TutorialIndex += 1;
                    await playerInfo.Save();
                    break;
            }
        }

        using var packet = PacketMaker.U_TO_C_UPDATE_TUTORIAL(playerInfo, playerInfo.JobInfo);
        user.Send(packet);
    }
}