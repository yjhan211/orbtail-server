using System.Collections.Concurrent;
using MessagePack;
using network.common;
using network.helpers;
using network.infrastructure;
using network.interfaces;
using user_server.managers;

namespace user_server.handlers
{
    public class MovementHandler : IHandler
    {
        private readonly ConcurrentQueue<C_TO_U_MOVE> _moveQueue = new();
        private readonly SemaphoreSlim _moveLock = new(1, 1);
        private Cell? _lastCell;

        private readonly GameObjectInfo _objectInfo;
        private readonly NatsClient _natsClient;
        private readonly UpdateObjectManager _updateObjectManager;
        private readonly Func<ChangeMapInfo, Task> _spawn;
        private bool _disposed = false;

        public MovementHandler(GameObjectInfo objectInfo, NatsClient natsClient, UpdateObjectManager updateObjectManager, Func<ChangeMapInfo, Task> spawn)
        {
            _objectInfo = objectInfo;
            _natsClient = natsClient;
            _updateObjectManager = updateObjectManager;
            _spawn = spawn;
        }

        public Task Initialize() => Task.CompletedTask;
        public Task Stop() => Task.CompletedTask;
        public Task ProcessAsync(object request)
        {
            if (request is C_TO_U_MOVE moveRequest)
            {
                return Move(moveRequest);
            }
            return Task.CompletedTask;
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
            _lastCell = Cell.Clone(_objectInfo.CurrentCell);
            _objectInfo.CurrentCell = Cell.Clone(_objectInfo.TargetCell);
            _objectInfo.TargetCell = MapHelper.CalcTargetCell(_objectInfo.TargetCell, moveRequest.Direction);
            _objectInfo.SetFlip(moveRequest.Direction);
            _objectInfo.MoveTimestamp = DateTime.UtcNow;
            await _objectInfo.Save();
            UpdateObjectState();

            var moveElapsedTime = await CalcMoveElapsedTime();
            await Task.Delay((int)(moveElapsedTime * 1000));

            await ProcessNextMoveAsync();
        }

        private void UpdateObjectState()
        {
            var lastPositionKey = MapHelper.GetPositionKey(_objectInfo.MapId, _objectInfo.MapSubId, _lastCell!);
            var currentPositionKey = MapHelper.GetPositionKey(_objectInfo.MapId, _objectInfo.MapSubId, _objectInfo.CurrentCell);

            int lastManageServer;
            int currentManageServer;
            switch (_objectInfo.MapId)
            {
                case MapID.LAB_1:
                    lastManageServer = MapHelper.GetServerIdByMapSubID(Program.GameServerNum, _objectInfo.MapSubId);
                    currentManageServer = lastManageServer;
                    break;

                default:
                    lastManageServer = MapHelper.GetServerIdByPositionKey(Program.GameServerNum, lastPositionKey);
                    currentManageServer = MapHelper.GetServerIdByPositionKey(Program.GameServerNum, currentPositionKey);
                    break;
            }

            // 담당 서버가 변경되었을 경우 이전 서버에게 떠났음을 알림
            if (lastManageServer != currentManageServer)
            {
                var leaveSubject = MapHelper.GetLeaveManageSubject(_objectInfo.MapId, _objectInfo.MapSubId, lastManageServer);
                _natsClient.Publish(leaveSubject, MessagePackSerializer.Serialize((lastPositionKey, _objectInfo.GetHashField())));
            }

            // 현재 서버에 이동 처리 요청
            var moveSubject = MapHelper.GetMoveManageSubject(_objectInfo.MapId, _objectInfo.MapSubId, currentManageServer);
            _natsClient.Publish(moveSubject, MessagePackSerializer.Serialize((lastPositionKey, _objectInfo)));

            // 자신의 이동이므로 큐에 즉시 넣음
            _updateObjectManager.EnqueueUpdateObject(_objectInfo);
        }

        private async Task ProcessNextMoveAsync()
        {
            await _moveLock.WaitAsync();
            try
            {
                // 포탈 여부 확인 및 맵 이동
                var isChangedMap = await TryHandleMapChange();
                if (isChangedMap)
                {
                    return;
                }

                // 큐에 있는 다음 이동요청 처리
                if (_moveQueue.TryDequeue(out var nextMove))
                {
                    await ProcessMoveAsync(nextMove);
                    return;
                }

                // 이동 완료 처리
                await FinalizeMoveAsync();
            }
            finally
            {
                _moveLock.Release();
            }
        }

        private async Task FinalizeMoveAsync()
        {
            _objectInfo.CurrentCell = Cell.Clone(_objectInfo.TargetCell);
            await _objectInfo.Save();
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
            return 0;
        }

        private async Task<bool> TryHandleMapChange()
        {
            if (!MapHelper.PortalInfo.TryGetValue(MapHelper.GetPortalKey(_objectInfo.MapId, _objectInfo.TargetCell), out var portalResult))
            {
                RequestSpawnInfo(_objectInfo.MapId, false);
                return false;
            }

            var (mapId, spawnPosition, isFlip) = portalResult;
            long mapSubId = await GetMapSubId(mapId);

            var mapChangeInfo = new ChangeMapInfo(mapId, mapSubId, spawnPosition, isFlip);
            await _spawn.Invoke(mapChangeInfo);
            _moveQueue.Clear();
            RequestSpawnInfo(mapChangeInfo.MapId, true);

            return true;
        }

        private void RequestSpawnInfo(MapID targetMapId, bool isMapChange)
        {
            if (targetMapId == MapID.LAB_1)
            {
                var serverId = MapHelper.GetServerIdByMapSubID(Program.GameServerNum, _objectInfo.MapSubId);
                var instanceKey = MapHelper.GetInstanceKey(_objectInfo.MapId, _objectInfo.MapSubId);
                RequestSpawnObjectList(serverId, new List<string> { instanceKey });
                return;
            }

            RequestCommmonMapSpawnList(isMapChange);
        }

        // 최초 맵 입장 or 이동 시 새로운 영역에 대한 오브젝트 정보 요청
        private void RequestSpawnObjectList(int serverId, List<string>? positionKeyList = null)
        {
            var subject = MapHelper.GetSpawnManageSubject(_objectInfo.MapId, _objectInfo.MapSubId, serverId);
            var message = MessagePackSerializer.Serialize((_objectInfo.GetHashField(), positionKeyList ?? new()));
            _natsClient.Publish(subject, message);
        }

        private void RequestCommmonMapSpawnList(bool isMapChange)
        {
            // 현재 바운드 - 이전 바운드 = spawn 대상
            List<Cell> lastBoundCellList = isMapChange ? new() : MapHelper.GetBoundCellList(_objectInfo.CurrentCell);
            var currentBoundCellList = MapHelper.GetBoundCellList(_objectInfo.CurrentCell);
            var objectSpawnList = currentBoundCellList
                .Except(lastBoundCellList)
                .Select(lastBoundCell => MapHelper.GetPositionKey(_objectInfo.MapId, 0, lastBoundCell))
                .GroupBy(
                    positionKey => MapHelper.GetServerIdByPositionKey(Program.GameServerNum, positionKey),
                    positionKey => positionKey,
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
                return;

            if (disposing)
            {
                // 관리되는 리소스 해제
                _moveLock.Dispose();
            }

            // 비관리 리소스 해제 (이 클래스에는 없음)

            _disposed = true;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
