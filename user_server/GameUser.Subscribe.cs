using network.common;
using network.common.data.models;
using network.packets;
using StackExchange.Redis;
using user_server.controllers;

namespace user_server;

public partial class GameUser
{
    private async Task OnMessageFromSubscribe(RedisValue message)
    {
        try
        {
            if (PlayerManager.State == PlayerState.NONE)
            {
                throw new Exception("invalid PlayerState");
            }

            await _userLock.WaitAsync();

            using var packet = new Packet((byte[])message!);
            var protocolId = (Protocol)packet.PopProtocolId();
            _ = packet.PopPlayerId();
            var body = packet.PopBody();

            switch (protocolId)
            {
                case Protocol.G_TO_U_UPDATE_OBJECT:
                    HandleMessage<G_TO_U_MOVE>(body, SubscribeUpdateObject);
                    break;

                case Protocol.G_TO_U_SPAWN:
                    HandleMessage<G_TO_U_SPAWN>(body, SubscribeSpawn);
                    break;

                case Protocol.G_TO_U_DESTROY:
                    HandleMessage<G_TO_U_DESTROY>(body, SubscribeDestroy);
                    break;

                case Protocol.U_TO_C_CHAT_MSG:
                    HandleMessage<U_TO_C_CHAT_MSG>(body, SubscribeChatMsg);
                    break;

                case Protocol.G_TO_U_PLAYER_INFO:
                    HandleMessage<G_TO_U_PLAYER_INFO>(body, SubscribePlayerInfo);
                    break;

                case Protocol.G_TO_U_EXPLORE_TARGET_INFO:
                    HandleMessage<G_TO_U_EXPLORE_TARGET_INFO>(body, SubscribeExploreTargetInfo);
                    break;

                case Protocol.G_TO_U_CREATE_INSTANCE_SUCCESS:
                    HandleMessage<G_TO_U_CREATE_INSTANCE_SUCCESS>(body, SubscribeCreateInstanceSuccess);
                    break;

                case Protocol.U_TO_C_LAB_INFO:
                    HandleMessage<U_TO_C_LAB_INFO>(body, SubscribeLabInfo);
                    break;

                case Protocol.U_TO_U_LAB_INVENTORY:
                    HandleMessage<U_TO_U_LAB_INVENTORY>(body, SubscribeLabInventory);
                    break;

                case Protocol.G_TO_U_CAMP_INFO:
                    HandleMessage<G_TO_U_CAMP_INFO>(body, SubscribeCampInfo);
                    break;

                case Protocol.U_TO_U_DUPLICATE:
                    RecvDuplicate();
                    break;
            }
        }
        catch (Exception e)
        {
            _logManager.WriteErrorLog(e);
        }
        finally
        {
            _userLock.Release();
        }
    }

    private void SubscribeUpdateObject(GameUser _, G_TO_U_MOVE body)
    {
        if (body.ObjectInfo.ObjectType == ObjectType.PLAYER)
            if (body.ObjectInfo.ObjectId == PlayerManager.PlayerId)
                // 자신의 이동은 RequestMove시 이미 넣음
                return;

        _updateObjectManager.EnqueueUpdateObject(body.ObjectInfo);
    }

    private void SubscribeSpawn(GameUser user, G_TO_U_SPAWN body)
    {
        _logManager.WriteDebugLog($"[SubscribeSpawn] {body.ObjectKeyList.Count} | {body.CellsToRemove.Count}");
    
        var objectKeys = body.ObjectKeyList.Where(key => key != PlayerManager.ObjectKey).ToList();
        var cellsToRemove = body.CellsToRemove ?? [];

        var batchCount = (int)Math.Ceiling((double)Math.Max(objectKeys.Count, cellsToRemove.Count) / Config.BROADCAST_UNIT);
        for (var i = 0; i < batchCount; i++)
        {
            var batch = objectKeys
                .Skip(i * Config.BROADCAST_UNIT)
                .Take(Config.BROADCAST_UNIT)
                .ToList();
            
            var cellBatch = cellsToRemove
                .Skip(i * Config.BROADCAST_UNIT)
                .Take(Config.BROADCAST_UNIT)
                .ToList();

            var isEnded = i == batchCount - 1;
        
            using var packet = PacketMaker.U_TO_C_SPAWN(
                batch.Select(item => item.ToString()).ToList(), 
                isEnded,
                cellBatch
            );
            Send(packet);
        
            _logManager.WriteDebugLog($"[SubscribeSpawn] Send {batch.Count} | {cellBatch.Count}");
        }
    }

    private void SubscribeDestroy(GameUser _, G_TO_U_DESTROY body)
    {
        using var packet = PacketMaker.U_TO_C_DESTROY(body.ObjectKey);
        Send(packet);
    }

    private void SubscribeChatMsg(GameUser _, U_TO_C_CHAT_MSG body)
    {
        using var packet = PacketMaker.U_TO_C_CHAT_MSG(body.ChatType, body.Name, body.ChatMessage);
        Send(packet);
    }

    private void SubscribePlayerInfo(GameUser _, G_TO_U_PLAYER_INFO body)
    {
        using var packet = PacketMaker.U_TO_C_PLAYER_INFO([body.PlayerInfo]);
        Send(packet);
    }

    private void SubscribeExploreTargetInfo(GameUser _, G_TO_U_EXPLORE_TARGET_INFO body)
    {
        using var packet = PacketMaker.U_TO_C_EXPLORE_TARGET_INFO([body.ExploreTargetInfo]);
        Send(packet);
    }

    private void SubscribeCreateInstanceSuccess(GameUser _, G_TO_U_CREATE_INSTANCE_SUCCESS body)
    {
        if (body.MapId != PlayerManager.MapId || body.MapSubId != PlayerManager.MapSubId) return;
        using var packet = PacketMaker.U_TO_C_CHANGE_MAP(
            PlayerManager.MapId,
            PlayerManager.MapSubId,
            PlayerManager.CurrentCell,
            PlayerManager.IsFlip
        );

        Send(packet);
    }

    private void SubscribeLabInfo(GameUser _, U_TO_C_LAB_INFO body)
    {
        using var packet = PacketMaker.U_TO_C_LAB_INFO(body.JoinPlayerInfo, body.LabInfo);
        Send(packet);
    }

    private void SubscribeLabInventory(GameUser user, U_TO_U_LAB_INVENTORY body)
    {
        LabController.SendLabItemList(user, body.ItemDict);
    }

    private void SubscribeCampInfo(GameUser _, G_TO_U_CAMP_INFO body)
    {
        using var packet = PacketMaker.U_TO_C_CAMP_INFO([body.CampInfo]);
        Send(packet);
    }
}