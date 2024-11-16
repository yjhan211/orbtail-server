using System.Collections.Concurrent;
using MessagePack;
using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;
using network.infrastructure;
using network.managers;
using network.packets;
using user_server.managers;

namespace user_server.handlers;

public sealed class MovementHandler(
    LogManager? logManager,
    GameObjectInfo objectInfo,
    NatsClient natsClient,
    SendPacketDelegate sendToClient,
    UpdateObjectManager updateObjectManager,
    Func<Task> spawn)
{
    // ReSharper disable once UnusedMember.Local
    private readonly LogManager? _logManager = logManager;
    private readonly SemaphoreSlim _moveLock = new(1, 1);
    private readonly ConcurrentQueue<C_TO_U_MOVE> _moveQueue = new();

    private bool _disposed;
    private Cell? _lastCell;

    public async Task ProcessAsync(C_TO_U_MOVE request)
    {
        await Move(request);
    }

    public async Task Spawn()
    {
        await ProcessAsync(new C_TO_U_MOVE { Direction = DirectionType.NONE });
        RequestSpawnInfo(objectInfo.MapId, true);
    }

    private async Task Move(C_TO_U_MOVE body)
    {
        await _moveLock.WaitAsync();
        try
        {
            if (_moveQueue.IsEmpty)
            {
                await ProcessMoveAsync(body);
                return;
            }

            if (_moveQueue.Count > Config.MAX_MOVE_QUEUE_SIZE)
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
        _lastCell ??= Cell.Clone(objectInfo.CurrentCell);
        objectInfo.CurrentCell = Cell.Clone(objectInfo.TargetCell);
        objectInfo.TargetCell = objectInfo.TargetCell.GetNextCell(moveRequest.Direction);

        objectInfo.SetFlip(moveRequest.Direction);
        objectInfo.MoveTimestamp = moveRequest.Direction == DirectionType.NONE ? default : DateTime.UtcNow;
        await objectInfo.Save();

        var isCommonMap = GameMapData.IsCommonMap(objectInfo.MapId);

        var lastPartKey = isCommonMap
            ? MapHelper.CreatePartKey(objectInfo.MapId, _lastCell)
            : MapHelper.CreatePartKey(objectInfo.MapId, objectInfo.MapSubId);

        var currentPartKey = isCommonMap
            ? MapHelper.CreatePartKey(objectInfo.MapId, objectInfo.CurrentCell)
            : MapHelper.CreatePartKey(objectInfo.MapId, objectInfo.MapSubId);

        var lastManageServer = isCommonMap
            ? MapHelper.GetManageServerId(lastPartKey)
            : MapHelper.GetManageServerId(objectInfo.MapSubId);

        var currentManageServer = isCommonMap
            ? MapHelper.GetManageServerId(currentPartKey)
            : MapHelper.GetManageServerId(objectInfo.MapSubId);

        // 담당 서버가 변경되었을 경우 이전 서버에게 떠났음을 알림
        if (lastManageServer != currentManageServer)
        {
            var leaveSubject = SubjectHelper.GetLeaveManageSubject(objectInfo, lastManageServer);
            natsClient.Publish(leaveSubject,
                MessagePackSerializer.Serialize((lastPartKey, objectInfo.GetGameObjectKey())));
        }

        // 현재 서버에 이동 처리 요청
        var moveSubject = SubjectHelper.GetUpdateManageSubject(objectInfo, currentManageServer);
        natsClient.Publish(moveSubject, MessagePackSerializer.Serialize((lastPartKey, _objectInfo: objectInfo)));

        // 자신의 이동이므로 큐에 즉시 넣음
        updateObjectManager.EnqueueUpdateObject(objectInfo);

        var moveElapsedTime = CalcMoveElapsedTime();
        await Task.Delay((int)(moveElapsedTime * 1000));

        var isArrive = true;
        while (_moveQueue.TryDequeue(out var nextMove))
        {
            // 큐에 있는 모든 이동요청 순차적으로 처리
            await ProcessMoveAsync(nextMove);
            isArrive = false;
        }

        if (!isArrive) return;

        await CompleteMovement();
    }

    private async Task CompleteMovement()
    {
        _lastCell = Cell.Clone(objectInfo.CurrentCell);
        objectInfo.CurrentCell = Cell.Clone(objectInfo.TargetCell);
        await objectInfo.Save();

        using var packet = PacketMaker.U_TO_C_MOVE(objectInfo.ObjectId, ErrorCode.SUCCESS, objectInfo);
        sendToClient(packet);

        if (GameMapData.IsCommonMap(objectInfo.MapId)) RequestSpawnInfo(objectInfo.MapId);
    }

    private void RequestSpawnInfo(MapId targetMapId, bool isSpawn = false)
    {
        if (!GameMapData.IsCommonMap(targetMapId))
        {
            var serverId = MapHelper.GetManageServerId(objectInfo.MapSubId);
            var instanceKey = MapHelper.CreatePartKey(objectInfo.MapId, objectInfo.MapSubId);
            RequestSpawnObjectList(serverId, [instanceKey]);
            return;
        }

        RequestCommonMapSpawnList(isSpawn);
    }

    // 최초 맵 입장 or 이동 시 새로운 영역에 대한 오브젝트 정보 요청
    private void RequestSpawnObjectList(int serverId, List<string>? positionKeyList = null)
    {
        var subject = SubjectHelper.GetSpawnManageSubject(objectInfo, serverId);
        var message = MessagePackSerializer.Serialize((objectInfo.GetGameObjectKey(), positionKeyList ?? []));
        natsClient.Publish(subject, message);
    }

    private void RequestCommonMapSpawnList(bool isSpawn)
    {
        // 현재 바운드 - 이전 바운드 = spawn 대상
        var lastBoundCellList = isSpawn ? [] : _lastCell?.GetBoundCellList();
        var currentBoundCellList = objectInfo.CurrentCell.GetBoundCellList();
        var objectSpawnList = currentBoundCellList
            .Except(lastBoundCellList!)
            .Select(lastBoundCell => MapHelper.CreatePartKey(objectInfo.MapId, lastBoundCell))
            .GroupBy(
                MapHelper.GetManageServerId,
                (serverId, positionKeys) => new { serverId, positionKeyList = positionKeys.ToList() }
            );

        foreach (var item in objectSpawnList) RequestSpawnObjectList(item.serverId, item.positionKeyList);
    }

    private float CalcMoveElapsedTime()
    {
        var moveElapsedTime = GameRuleData.MoveElapsedTime;
        // switch (objectInfo.MapId)
        // {
        //     case MapId.WETLAND_1:
        //         var player = await PlayerInfo.Load(objectInfo.ObjectId);
        //         if (player == null) throw new Exception("cannot find player info");
        //
        //         var hasSpeedBoost = player.WearItemIdList.Contains(104000004);
        //         if (!hasSpeedBoost) moveElapsedTime *= 2;
        //         break;
        //
        //     case MapId.NONE:
        //     case MapId.CAMPUS_1:
        //     case MapId.FACTORY_1:
        //     case MapId.LAB_1:
        //     case MapId.LIBRARY:
        //         break;
        //     default:
        //         throw new ArgumentOutOfRangeException();
        // }

        return moveElapsedTime;
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;

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
    }
}