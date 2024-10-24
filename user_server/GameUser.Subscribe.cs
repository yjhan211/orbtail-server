using StackExchange.Redis;
using network.interfaces;
using network.packets;
using network.common;
using user_server.controllers;

namespace user_server
{
    public partial class GameUser : IPeer
    {
        public void OnMessageFromSubscribe(RedisValue message)
        {
            try
            {
                if (_playerManager.State == PlayerState.NONE)
                {
                    throw new Exception($"invalid PlayerState");
                }

                using var packet = new Packet((byte[])message!);
                var protocolId = (PROTOCOL)packet.PopProtocolId();
                _ = packet.PopPlayerId();
                var body = packet.PopBody();

                switch (protocolId)
                {
                    case PROTOCOL.G_TO_U_UPDATE_OBJECT:
                        HandleMessage<G_TO_U_MOVE>(body, SubscribeUpdateObject);
                        break;

                    case PROTOCOL.G_TO_U_SPAWN:
                        HandleMessage<G_TO_U_SPAWN>(body, SubscribeSpawn);
                        break;

                    case PROTOCOL.G_TO_U_DESTROY:
                        HandleMessage<G_TO_U_DESTROY>(body, SubscribeDestroy);
                        break;

                    case PROTOCOL.U_TO_C_CHAT_MSG:
                        HandleMessage<U_TO_C_CHAT_MSG>(body, SubscribeChatMsg);
                        break;

                    case PROTOCOL.G_TO_U_PLAYER_INFO:
                        HandleMessage<G_TO_U_PLAYER_INFO>(body, SubscribePlayerInfo);
                        break;

                    case PROTOCOL.G_TO_U_EXPLORE_TARGET_INFO:
                        HandleMessage<G_TO_U_EXPLORE_TARGET_INFO>(body, SubscribeExploreTargetInfo);
                        break;

                    case PROTOCOL.G_TO_U_CREATE_INSTANCE_SUCCESS:
                        HandleMessage<G_TO_U_CREATE_INSTANCE_SUCCESS>(body, SubscribeCreateinstanceSuccess);
                        break;

                    case PROTOCOL.U_TO_C_LAB_INFO:
                        HandleMessage<U_TO_C_LAB_INFO>(body, SubscribeLabInfo);
                        break;

                    case PROTOCOL.U_TO_U_LAB_INVENTORY:
                        HandleMessage<U_TO_U_LAB_INVENTORY>(body, SubscribeLabInventory);
                        break;

                    case PROTOCOL.G_TO_U_CAMP_INFO:
                        HandleMessage<G_TO_U_CAMP_INFO>(body, SubscribeCampInfo);
                        break;

                    case PROTOCOL.U_TO_U_PLAYER_INFO:
                        HandleMessage<U_TO_U_PLAYER_INFO>(body, SubscribePlayerInfo);
                        break;

                    case PROTOCOL.U_TO_U_DUPLICATE:
                        RecvDuplicate();
                        break;
                }
            }
            catch (Exception e)
            {
                LogManager.WriteErrorLog(e);
            }
        }

        private void SubscribeUpdateObject(GameUser _, G_TO_U_MOVE body)
        {
            if (body.ObjectInfo.ObjectType == ObjectType.PLAYER)
            {
                if (body.ObjectInfo.ObjectId == _playerManager.PlayerId)
                {
                    // 자신의 이동은 RequestMove시 이미 넣음
                    return;
                }
            }

            _updateObjectManager.EnqueueUpdateObject(body.ObjectInfo);
        }

        private void SubscribeSpawn(GameUser _, G_TO_U_SPAWN body)
        {
            var count = 0;
            var batchList = new List<string>(Config.BROADCAST_UNIT);

            for (int i = 0; i < body.ObjectKeyList.Count; i++)
            {
                var key = body.ObjectKeyList[i];
                if (key == _playerManager.ObjectKey)
                    continue;

                batchList.Add(key.ToString());
                count++;

                if (count == Config.BROADCAST_UNIT || i == body.ObjectKeyList.Count - 1)
                {
                    var isEnded = i == body.ObjectKeyList.Count - 1;
                    using var packet = PacketMaker.U_TO_C_SPAWN(batchList, isEnded);
                    Send(packet);

                    batchList.Clear();
                    count = 0;
                }
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

        private void SubscribePlayerInfo(GameUser _, U_TO_U_PLAYER_INFO body)
        {
            using var packet = PacketMaker.U_TO_C_PLAYER_INFO(new() { body.PlayerInfo });
            Send(packet);
        }

        private void SubscribePlayerInfo(GameUser _, G_TO_U_PLAYER_INFO body)
        {
            using var packet = PacketMaker.U_TO_C_PLAYER_INFO(new() { body.PlayerInfo });
            Send(packet);
        }

        private void SubscribeExploreTargetInfo(GameUser _, G_TO_U_EXPLORE_TARGET_INFO body)
        {
            using var packet = PacketMaker.U_TO_C_EXPLORE_TARGET_INFO(new() { body.ExploreTargetInfo });
            Send(packet);
        }

        private void SubscribeCreateinstanceSuccess(GameUser _, G_TO_U_CREATE_INSTANCE_SUCCESS body)
        {
            if (body.MapId != _playerManager.MapId || body.MapSubId != _playerManager.MapSubId)
            {
                return;
            }
            using var packet = PacketMaker.U_TO_C_CHANGE_MAP(
                _playerManager.MapId,
                _playerManager.MapSubId,
                _playerManager.CurrentCell,
                _playerManager.IsFlip
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
            using var packet = PacketMaker.U_TO_C_CAMP_INFO(new() { body.CampInfo });
            Send(packet);
        }
    }
}