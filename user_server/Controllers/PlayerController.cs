using network.common;
using network.common.data.models;
using network.packets;
using StackExchange.Redis;

namespace user_server.controllers;

public static class PlayerController
{
    public static async Task GetPlayerInfo(GameUser user, C_TO_U_PLAYER_INFO body)
    {
        var keys = body.PlayerIdList.ConvertAll(x => (RedisValue)x).ToArray();
        var playerInfoList = await PlayerInfo.LoadAll(keys);

        using var packet = PacketMaker.U_TO_C_PLAYER_INFO(playerInfoList);
        user.Send(packet);
    }
}