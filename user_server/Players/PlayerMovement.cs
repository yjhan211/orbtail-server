using System.Collections.Concurrent;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;
using network.interfaces;
using network.packets;
using user_server.controllers;

namespace user_server.players;

public sealed class PlayerMovement(GameUser user, PlayerInfo playerInfo)
{
    private readonly ICacheHelper _cacheHelper = user.CacheHelper;
    private readonly INatsClient _natsClient = user.NatsClient;
    
    private readonly MapObjectController _mapObjectController = user.MapObjectController;
    
    private readonly BroadcastDelegate<PlayerInfo> _broadcastUpdateInfo = user.BroadcastUpdateInfo;
    private readonly SendPacketDelegate _sendToClient = user.Send;
    
    private readonly SemaphoreSlim _moveLock = new(1, 1);
    private readonly ConcurrentQueue<C_TO_U_MOVE> _moveQueue = new();

    private readonly ILogger _logger = user.Logger;
    
    private bool _disposed;
    private Cell? _lastCell;

    public async Task HandleMove(C_TO_U_MOVE request)
    {
        await HandlePreMove();
        await ProcessAsync(request);
    }

    public async Task Spawn()
    {
        await ProcessAsync(new C_TO_U_MOVE { Direction = DirectionType.NONE });
        RequestSpawnInfo(playerInfo.ObjectInfo.MapId, [], true);
        user.BroadcastUpdateInfo(playerInfo);
    }

    private async Task HandlePreMove()
    {
        if (playerInfo.State == PlayerState.SITGROUND)
        {
            playerInfo.State = PlayerState.IDLE;
            await playerInfo.Save(_cacheHelper);
            _broadcastUpdateInfo(playerInfo);
        }

        await HandleBoostSpeed();
    }

    private async Task HandleBoostSpeed()
    {
        if (playerInfo.Boosts.Contains(BoostType.SPEED) && playerInfo.Hp < 5)
        {
            playerInfo.Boosts.Remove(BoostType.SPEED);
            await playerInfo.Save(_cacheHelper);
            _broadcastUpdateInfo(playerInfo);
        }
    }
    
    private async Task ProcessAsync(C_TO_U_MOVE request)
    {
        await Move(request);
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

            if (_moveQueue.Count >= Config.MAX_MOVE_QUEUE_SIZE)
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
        var moveSpeed = GetMoveSpeed(moveRequest.Direction);
        var moveElapsedTime = GameRuleData.MoveElapsedTime / moveSpeed;
        var consumeHp = (int)(moveSpeed * (moveSpeed + 1) - 2);
        if (consumeHp > 0)
        {
            IncreaseHp(consumeHp * -1);
        }
        
        var nextTargetCell = playerInfo.ObjectInfo.TargetCell.GetNextCell(moveRequest.Direction);
        if (!GameMapData.IsMoveablePosition(playerInfo.ObjectInfo.MapId, nextTargetCell))
        {
            return;
        }

        _lastCell = Cell.Clone(playerInfo.ObjectInfo.CurrentCell);
        playerInfo.ObjectInfo.CurrentCell = Cell.Clone(playerInfo.ObjectInfo.TargetCell);
        playerInfo.ObjectInfo.TargetCell = playerInfo.ObjectInfo.TargetCell.GetNextCell(moveRequest.Direction);

        var currentBoundCells = playerInfo.ObjectInfo.CurrentCell.GetBoundCellList();
        var targetBoundCells = playerInfo.ObjectInfo.TargetCell.GetBoundCellList();
        var cellsToRemove = currentBoundCells.Except(targetBoundCells).ToList();

        playerInfo.ObjectInfo.SetFlip(moveRequest.Direction);
        playerInfo.ObjectInfo.MoveTimestamp = moveRequest.Direction == DirectionType.NONE ? default : DateTime.UtcNow;
        await playerInfo.ObjectInfo.Save(_cacheHelper);

        var isCommonMap = GameMapData.IsCommonMap(playerInfo.ObjectInfo.MapId);
        var currentPartKey = isCommonMap
            ? MapHelper.CreatePartKey(playerInfo.ObjectInfo.MapId, playerInfo.ObjectInfo.CurrentCell)
            : MapHelper.CreatePartKey(playerInfo.ObjectInfo.MapId, playerInfo.ObjectInfo.MapSubId);
        
        var nextPartKey = isCommonMap
            ? MapHelper.CreatePartKey(playerInfo.ObjectInfo.MapId, playerInfo.ObjectInfo.TargetCell)
            : MapHelper.CreatePartKey(playerInfo.ObjectInfo.MapId, playerInfo.ObjectInfo.MapSubId);
        
        var currentManageServer = isCommonMap
            ? MapHelper.GetManageServerId(currentPartKey)
            : MapHelper.GetManageServerId(playerInfo.ObjectInfo.MapSubId);
        
        var nextManageServer = isCommonMap
            ? MapHelper.GetManageServerId(nextPartKey)
            : MapHelper.GetManageServerId(playerInfo.ObjectInfo.MapSubId);
        
        if (currentManageServer != nextManageServer)
        {
            var leaveSubject = SubjectHelper.GetLeaveManageSubject(playerInfo.ObjectInfo, currentManageServer);
            _natsClient.Publish(leaveSubject, MessagePackSerializer.Serialize((currentPartKey, playerInfo.ObjectInfo.GetGameObjectKey())));
        }

        var moveSubject = SubjectHelper.GetUpdateManageSubject(playerInfo.ObjectInfo, nextManageServer);
        _natsClient.Publish(moveSubject, MessagePackSerializer.Serialize((currentPartKey, objectInfo: playerInfo.ObjectInfo)));
        if (GameMapData.IsCommonMap(playerInfo.ObjectInfo.MapId))
        {
            RequestSpawnInfo(playerInfo.ObjectInfo.MapId, cellsToRemove);
        }

        if (moveRequest.Direction != DirectionType.NONE)
        {
            _mapObjectController.EnqueueUpdateObject(playerInfo.ObjectInfo);
        }

        await Task.Delay((int)(moveElapsedTime * 1000));

        var isArrive = true;
        while (_moveQueue.TryDequeue(out var nextMove))
        {
            isArrive = false;
            // 큐에 있는 모든 이동요청 순차적으로 처리
            await ProcessMoveAsync(nextMove);
        }

        if (!isArrive)
        {
            return;
        }

        await CompleteMovement();
    }

    private async Task CompleteMovement()
    {
        playerInfo.ObjectInfo.CurrentCell = Cell.Clone(playerInfo.ObjectInfo.TargetCell);
        await playerInfo.ObjectInfo.Save(_cacheHelper);

        using var packet = PacketMaker.U_TO_C_MOVE(playerInfo.ObjectInfo.ObjectId, ErrorCode.SUCCESS, playerInfo.ObjectInfo);
        _sendToClient(packet);
    }

    private float GetMoveSpeed(DirectionType direction)
    {
        // if (playerInfo.Boosts.Contains(BoostType.SPEED))
        // {
        //     return 2;
        // }

        var speed = playerInfo.Hp <= 1000 ? 1f : 2f;
        switch (direction)
        {
            case DirectionType.TOP:
            case DirectionType.BOTTOM:
                speed /= 1.3f;
                break;
            
            case DirectionType.LEFT: 
            case DirectionType.RIGHT:
                speed /= 2;
                break;
        }

        return speed;
    }

    private void IncreaseHp(int value)
    {
        playerInfo.Hp = Math.Clamp(playerInfo.Hp + value, 0, 10000);
        
        using var packet = PacketMaker.U_TO_C_PLAYER_INFO([playerInfo]);
        _sendToClient(packet);
    }
    
    private void RequestSpawnInfo(MapId targetMapId, List<Cell> cellsToRemove, bool isAll = false)
    {
        if (!GameMapData.IsCommonMap(targetMapId))
        {
            var serverId = MapHelper.GetManageServerId(playerInfo.ObjectInfo.MapSubId);
            var instanceKey = MapHelper.CreatePartKey(playerInfo.ObjectInfo.MapId, playerInfo.ObjectInfo.MapSubId);
            RequestSpawnObjectList(serverId, [instanceKey], []);
            return;
        }

        RequestCommonMapSpawnList(cellsToRemove, isAll);
    }
    
    private void RequestCommonMapSpawnList(List<Cell> cellsToRemove, bool isAll)
    {
        var currentBoundCellList = isAll ? [] : playerInfo.ObjectInfo.CurrentCell.GetBoundCellList();
        var targetBoundCellList = playerInfo.ObjectInfo.TargetCell.GetBoundCellList();

        var objectSpawnList = targetBoundCellList
            .Except(currentBoundCellList)
            .Select(cell => MapHelper.CreatePartKey(playerInfo.ObjectInfo.MapId, cell))
            .GroupBy(
                MapHelper.GetManageServerId,
                (serverId, positionKeys) => new { serverId, positionKeyList = positionKeys.ToList() }
            )
            .Where(group => group.serverId > 0)
            .ToList();

        var removeCellsByServer = cellsToRemove
            .GroupBy(
                cell => MapHelper.GetManageServerId(MapHelper.CreatePartKey(playerInfo.ObjectInfo.MapId, cell)),
                (serverId, cells) => new { serverId, cells = cells.ToList() }
            )
            .Where(group => group.serverId > 0)
            .ToList();

        var allServers = objectSpawnList
            .Select(x => x.serverId)
            .Union(removeCellsByServer.Select(x => x.serverId))
            .Distinct()
            .ToList();

        foreach (var serverId in allServers)
        {
            var spawnPositions = objectSpawnList
                .FirstOrDefault(x => x.serverId == serverId)?.positionKeyList ?? [];
            var removeCells = removeCellsByServer
                .FirstOrDefault(x => x.serverId == serverId)?.cells ?? [];
       
            RequestSpawnObjectList(serverId, spawnPositions, removeCells);
        }
    }
    
    // 최초 맵 입장 or 이동 시 새로운 영역에 대한 오브젝트 정보 요청
    private void RequestSpawnObjectList(int serverId, List<string>? positionKeyList, List<Cell>? cellsToRemove)
    {
        var subject = SubjectHelper.GetSpawnManageSubject(playerInfo.ObjectInfo, serverId);
        var message = MessagePackSerializer.Serialize((
            playerInfo.ObjectInfo.GetGameObjectKey(), 
            positionKeyList ?? [], 
            cellsToRemove ?? []
        ));
        _natsClient.Publish(subject, message);
        _logger.LogInformation("RequestSpawnObjectList {subject}", subject);
    }

    public void Dispose()
    {
        if (_disposed) return;

        _moveLock.Dispose();
        _moveQueue.Clear();
        
        _disposed = true;
    }
}