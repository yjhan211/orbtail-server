using System.Diagnostics.CodeAnalysis;
using MessagePack;
using network.common;
using network.helpers;
using network.infrastructure;
using network.packets;
using network.managers;
using user_server.handlers;

namespace user_server.managers
{
    public delegate void SendPacketDelegate(Packet msg);

    public class PlayerManager
    {
        private readonly LogManager _logManager;
        private readonly NatsClient _natsClient;
        private readonly SendPacketDelegate _sendToClient;
        private readonly UpdateObjectManager _updateObjectManager;
        private MovementHandler? _movementHandler;
        private EnvironmentHandler? _environmentHandler;

        public GameObjectInfo? ObjectInfo { get; private set; }
        public long PlayerId => ObjectInfo?.ObjectId ?? 0;
        public MapID MapId => ObjectInfo?.MapId ?? MapID.NONE;
        public long MapSubId => ObjectInfo?.MapSubId ?? 0;
        public Cell CurrentCell => ObjectInfo?.CurrentCell ?? new Cell(0, 0);
        public bool IsFlip => ObjectInfo?.IsFlip ?? false;
        public PlayerState State { get; private set; }

        public PlayerManager(LogManager logManager, NatsClient natsClient, SendPacketDelegate sendToClient, UpdateObjectManager updateObjectManager)
        {
            _logManager = logManager;
            _natsClient = natsClient;
            _sendToClient = sendToClient;
            _updateObjectManager = updateObjectManager;
        }

        [MemberNotNull(nameof(ObjectInfo))]
        public void Initialize(GameObjectInfo objectInfo, MovementHandler movementHandler, EnvironmentHandler enviromentHandler)
        {
            ObjectInfo = objectInfo;
            ObjectInfo.CurrentCell = ObjectInfo.TargetCell;
            State = PlayerState.IDLE;

            _movementHandler = movementHandler;
            _environmentHandler = enviromentHandler;
        }

        public async Task ChangeMap(ChangeMapInfo? changeMapInfo = null)
        {
            // 기존 맵에 삭제 요청
            await PublishDestroy();

            if (changeMapInfo != null)
            {
                ObjectInfo!.MapId = changeMapInfo.MapId;
                ObjectInfo.MapSubId = changeMapInfo.MapSubId;
                ObjectInfo.CurrentCell = changeMapInfo.SpawnCell;
                ObjectInfo.TargetCell = changeMapInfo.SpawnCell;
                ObjectInfo.IsFlip = changeMapInfo.IsFlip;
                await ObjectInfo.Save();
            }

            switch (MapId)
            {
                case MapID.LAB_1:
                    var serverId = MapHelper.GetServerIdByMapSubID(Program.GameServerNum, MapSubId);
                    var subject = MapHelper.GetCreateInstanceSubject(serverId);
                    var publishObj = MessagePackSerializer.Serialize((ObjectInfo!.GetHashField(), MapId, MapSubId));
                    _natsClient.Publish(subject, publishObj);
                    break;

                default:
                    using (var packet = PacketMaker.U_TO_C_CHANGE_MAP(MapId, MapSubId, CurrentCell, IsFlip))
                    {
                        _sendToClient(packet);
                    }
                    break;
            }
        }

        public async Task Spawn()
        {
            if (_movementHandler == null)
            {
                return;
            }

            await _movementHandler.Spawn();
        }

        public async Task RequestMove(GameUser _, C_TO_U_MOVE body)
        {
            if (_movementHandler == null)
            {
                return;
            }

            await _movementHandler.ProcessAsync(body);
        }

        public void SetState(PlayerInfo playerInfo, PlayerState newState)
        {
            playerInfo.State = newState;
            State = newState;
        }


        // 공통맵에서만 쓰고 있어서 일단 냅둠
        public async Task SetFlip(DirectionType direction)
        {
            if (direction == DirectionType.NONE || ObjectInfo == null)
            {
                return;
            }

            ObjectInfo.SetFlip(direction);
            await ObjectInfo.Save();

            var currentPositionKey = MapHelper.GetPositionKey(MapId, MapSubId, CurrentCell);
            var manageServer = MapHelper.GetServerIdByPositionKey(Program.GameServerNum, currentPositionKey);
            var subject = MapHelper.GetMoveManageSubject(MapId, MapSubId, manageServer);

            _natsClient.Publish(subject, MessagePackSerializer.Serialize((currentPositionKey, ObjectInfo)));
            _updateObjectManager.EnqueueUpdateObject(ObjectInfo);
        }

        public async Task Dispose()
        {
            await PublishDestroy();

            if (_movementHandler != null)
            {
                _movementHandler.Dispose();
            }

            if (_environmentHandler != null)
            {
                _environmentHandler.Dispose();
            }
        }

        // 접속 종료 시 자신의 object_info 삭제 요청 (PublishLeave랑 다른 점 - 후에 Broadcast 처리가 됨)
        private async Task PublishDestroy()
        {
            if (ObjectInfo == null)
            {
                return;
            }

            await ObjectInfo.Save();

            string key;
            int manageServer;
            switch (MapId)
            {
                case MapID.LAB_1:
                    key = MapHelper.GetInstanceKey(MapId, MapSubId);
                    manageServer = MapHelper.GetServerIdByMapSubID(Program.GameServerNum, ObjectInfo.MapSubId);
                    break;

                default:
                    key = MapHelper.GetPositionKey(ObjectInfo.MapId, ObjectInfo.MapSubId, ObjectInfo.CurrentCell);
                    manageServer = MapHelper.GetServerIdByPositionKey(Program.GameServerNum, key);
                    break;
            }

            var subject = MapHelper.GetDestroyObjectSubject(MapId, MapSubId, manageServer);
            var message = MessagePackSerializer.Serialize((key, ObjectInfo.GetHashField()));
            _natsClient.Publish(subject, message);
        }
    }
}
