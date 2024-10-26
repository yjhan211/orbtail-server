using System.Collections.Concurrent;
using System.Diagnostics;
using MessagePack;
using network.common;
using network.helpers;
using network.managers;
using network.infrastructure;
using user_server.managers;
using network.packets;

namespace user_server.handlers
{
    public class MovementHandler
    {
        private readonly LogManager _logManager;
        private readonly ConcurrentQueue<C_TO_U_MOVE> _moveQueue = new();
        private readonly SemaphoreSlim _moveLock = new(1, 1);
        private Cell? _lastCell;

        private readonly GameObjectInfo _objectInfo;
        private readonly NatsClient _natsClient;
        private readonly SendPacketDelegate _sendToClient;
        private readonly UpdateObjectManager _updateObjectManager;
        private readonly Func<ChangeMapInfo, Task> _spawn;
        private bool _disposed = false;

        public MovementHandler(LogManager logManager, GameObjectInfo objectInfo, NatsClient natsClient, SendPacketDelegate sendToClient, UpdateObjectManager updateObjectManager, Func<ChangeMapInfo, Task> spawn)
        {
            _logManager = logManager;
            _objectInfo = objectInfo;
            _natsClient = natsClient;
            _sendToClient = sendToClient;
            _updateObjectManager = updateObjectManager;
            _spawn = spawn;
        }

        public Task Initialize() => Task.CompletedTask;
        public Task Stop() => Task.CompletedTask;

        public async Task ProcessAsync(C_TO_U_MOVE request)
        {
            await Move(request);
        }

        public async Task Spawn()
        {
            await ProcessAsync(new C_TO_U_MOVE { Direction = DirectionType.NONE });
            RequestSpawnInfo(_objectInfo.MapId, true);
        }

        public async Task Move(C_TO_U_MOVE body)
        {
            await _moveLock.WaitAsync();
            try
            {
                if (_moveQueue.IsEmpty)
                {
                    await ProcessMoveAsync(body);
                    return;
                }

                if (_moveQueue.Count > Config.MAX_QUEUE_SIZE)
                {
                    // TODO 싱크 완전히 깨진 상태이므로 위치 강제보정
                    throw new Exception("[RequestMove] moveQueue is Full.");
                }
                _moveQueue.Enqueue(body);
            }
            finally
            {
                _moveLock.Release();
            }
        }

        private async Task ProcessMoveAsync(C_TO_U_MOVE moveRequest)
        {
            _lastCell ??= Cell.Clone(_objectInfo.CurrentCell);
            _objectInfo.CurrentCell = Cell.Clone(_objectInfo.TargetCell);
            _objectInfo.TargetCell = _objectInfo.TargetCell.GetNextCell(moveRequest.Direction);

            _objectInfo.SetFlip(moveRequest.Direction);
            _objectInfo.MoveTimestamp = moveRequest.Direction == DirectionType.NONE ? default : DateTime.UtcNow;
            await _objectInfo.Save();

            var isCommonMap = CommonMapHelper.IsCommonMap(_objectInfo.MapId);

            var lastPartKey = isCommonMap
                ? CommonMapHelper.CreatePartKey(_objectInfo.MapId, _lastCell!)
                : InstanceMapHelper.CreatePartKey(_objectInfo.MapId, _objectInfo.MapSubId);

            var currentPartKey = isCommonMap
                ? CommonMapHelper.CreatePartKey(_objectInfo.MapId, _objectInfo.CurrentCell!)
                : InstanceMapHelper.CreatePartKey(_objectInfo.MapId, _objectInfo.MapSubId);

            int lastManageServer = isCommonMap
                ? CommonMapHelper.GetManageServerId(lastPartKey)
                : InstanceMapHelper.GetManageServerId(_objectInfo.MapSubId);

            int currentManageServer = isCommonMap
                ? CommonMapHelper.GetManageServerId(currentPartKey)
                : InstanceMapHelper.GetManageServerId(_objectInfo.MapSubId);

            // 담당 서버가 변경되었을 경우 이전 서버에게 떠났음을 알림
            if (lastManageServer != currentManageServer)
            {
                var leaveSubject = SubjectHelper.GetLeaveManageSubject(_objectInfo, lastManageServer);
                _natsClient.Publish(leaveSubject, MessagePackSerializer.Serialize((lastPartKey, _objectInfo.GetGameObjectKey())));
            }

            // 현재 서버에 이동 처리 요청
            var moveSubject = SubjectHelper.GetMoveManageSubject(_objectInfo, currentManageServer);
            _natsClient.Publish(moveSubject, MessagePackSerializer.Serialize((lastPartKey, _objectInfo)));

            // 자신의 이동이므로 큐에 즉시 넣음
            _updateObjectManager.EnqueueUpdateObject(_objectInfo);

            var moveElapsedTime = await CalcMoveElapsedTime();
            await Task.Delay((int)(moveElapsedTime * 1000));

            // 포탈 여부 확인 및 맵 이동
            var isChangedMap = await TryHandleMapChange();
            if (isChangedMap)
            {
                return;
            }

            bool isArrive = true;
            while (_moveQueue.TryDequeue(out var nextMove))
            {
                // 큐에 있는 모든 이동요청 순차적으로 처리
                await ProcessMoveAsync(nextMove);
                isArrive = false;
            }

            if (!isArrive)
            {
                return;
            }

            await CompleteMovement();
        }

        private async Task CompleteMovement()
        {
            _lastCell = Cell.Clone(_objectInfo.CurrentCell);
            _objectInfo.CurrentCell = Cell.Clone(_objectInfo.TargetCell);
            await _objectInfo.Save();

            using var packet = PacketMaker.U_TO_C_MOVE(_objectInfo.ObjectId, ErrorCode.SUCCESS, _objectInfo);
            _sendToClient(packet);

            if (CommonMapHelper.IsCommonMap(_objectInfo.MapId))
            {
                RequestSpawnInfo(_objectInfo.MapId);
            }
        }

        private async Task<long> GetMapSubId(MapID mapId)
        {
            if (mapId == MapID.LAB_1)
            {
                var playerInfo = await PlayerInfo.Load(_objectInfo.ObjectId);
                if (playerInfo == null || playerInfo.LabId <= 0)
                {
                    throw new Exception($"Invalid player info for LAB. PlayerId: {_objectInfo.ObjectId}");
                }
                return playerInfo.LabId;
            }
            if (mapId == MapID.LIBRARY)
            {
                return 0;
            }
            return 0;
        }

        private async Task<bool> TryHandleMapChange()
        {
            var portalInfo = CommonMapHelper.GetPortalOrNull(_objectInfo);
            if (portalInfo == null)
            {
                return false;
            }

            (MapID mapId, Cell spawnPosition, bool isFlip) = portalInfo.Value;
            long mapSubId = await GetMapSubId(mapId);

            var mapChangeInfo = new ChangeMapInfo(mapId, mapSubId, spawnPosition, isFlip);
            await _spawn.Invoke(mapChangeInfo);
            _moveQueue.Clear();

            return true;
        }

        private void RequestSpawnInfo(MapID targetMapId, bool isSpawn = false)
        {
            if (!CommonMapHelper.IsCommonMap(targetMapId))
            {
                var serverId = InstanceMapHelper.GetManageServerId(_objectInfo.MapSubId);
                var instanceKey = InstanceMapHelper.CreatePartKey(_objectInfo.MapId, _objectInfo.MapSubId);
                RequestSpawnObjectList(serverId, new() { instanceKey });
                return;
            }

            RequestCommmonMapSpawnList(isSpawn);
        }

        // 최초 맵 입장 or 이동 시 새로운 영역에 대한 오브젝트 정보 요청
        private void RequestSpawnObjectList(int serverId, List<string>? positionKeyList = null)
        {
            var subject = SubjectHelper.GetSpawnManageSubject(_objectInfo, serverId);
            var message = MessagePackSerializer.Serialize((_objectInfo.GetGameObjectKey(), positionKeyList ?? new()));
            _natsClient.Publish(subject, message);
        }

        private void RequestCommmonMapSpawnList(bool isSpawn)
        {
            // 현재 바운드 - 이전 바운드 = spawn 대상
            var lastBoundCellList = isSpawn || _lastCell == null ? new List<Cell>() : _lastCell.GetBoundCellList();
            var currentBoundCellList = _objectInfo.CurrentCell.GetBoundCellList();
            var objectSpawnList = currentBoundCellList
                .Except(lastBoundCellList)
                .Select(lastBoundCell => CommonMapHelper.CreatePartKey(_objectInfo.MapId, lastBoundCell))
                .GroupBy(
                    CommonMapHelper.GetManageServerId,
                    (serverId, positionKeys) => new { serverId, positionKeyList = positionKeys.ToList() }
                );

            foreach (var item in objectSpawnList)
            {
                RequestSpawnObjectList(item.serverId, item.positionKeyList);
            }
        }

        private async Task<float> CalcMoveElapsedTime()
        {
            var moveElapsedTime = Config.MOVE_ELAPSED_TIME;
            switch (_objectInfo.MapId)
            {
                case MapID.WETLAND_1:
                    PlayerInfo? player = await PlayerInfo.Load(_objectInfo.ObjectId);
                    if (player == null)
                    {
                        throw new Exception("cannot find player info");
                    }

                    bool hasSppedBoost = player.WearItemIdList.Contains(104000004);
                    if (!hasSppedBoost)
                    {
                        moveElapsedTime *= 2;
                    }
                    break;

                default:
                    break;
            }

            return moveElapsedTime;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                // 관리되는 리소스 해제
                _moveLock.Dispose();
                _moveQueue.Clear();
            }

            _disposed = true;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
