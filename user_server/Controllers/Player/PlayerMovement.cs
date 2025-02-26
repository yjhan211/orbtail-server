using System.Collections.Concurrent;
using MessagePack;
using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;
using network.infrastructure;
using network.packets;
using user_server.managers;

namespace user_server.controllers.player;

public sealed class PlayerMovement(GameUser user, PlayerInfo playerInfo)
{
    private readonly CacheHelper _cacheHelper = user.CacheHelper;
    private readonly NatsClient _natsClient = user.NatsClient;
    
    private readonly UpdateObjectManager _updateObjectManager = user.UpdateObjectManager;
    
    private readonly BroadcastDelegate<PlayerInfo> _broadcastUpdateInfo = user.BroadcastUpdateInfo;
    private readonly SendPacketDelegate _sendToClient = user.Send;
    
    private readonly SemaphoreSlim _moveLock = new(1, 1);
    private readonly ConcurrentQueue<C_TO_U_MOVE> _moveQueue = new();
    
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

            if (_moveQueue.Count <= Config.MAX_MOVE_QUEUE_SIZE)
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
        var moveSpeed = GetMoveSpeed();
        var moveElapsedTime = GameRuleData.MoveElapsedTime / moveSpeed;
        var consumeHp = (int)(moveSpeed * 2 + moveSpeed - 2);
        IncreaseHp(consumeHp * -1);

        _lastCell ??= Cell.Clone(playerInfo.ObjectInfo.CurrentCell);
        playerInfo.ObjectInfo.CurrentCell = Cell.Clone(playerInfo.ObjectInfo.TargetCell);
        playerInfo.ObjectInfo.TargetCell = playerInfo.ObjectInfo.TargetCell.GetNextCell(moveRequest.Direction);

        // 삭제할 cell 계산
        var lastBoundCells = _lastCell?.GetBoundCellList() ?? [];
        var currentBoundCells = playerInfo.ObjectInfo.CurrentCell.GetBoundCellList();
        var cellsToRemove = lastBoundCells.Except(currentBoundCells).ToList();

        playerInfo.ObjectInfo.SetFlip(moveRequest.Direction);
        playerInfo.ObjectInfo.MoveTimestamp = moveRequest.Direction == DirectionType.NONE ? default : DateTime.UtcNow;
        await playerInfo.ObjectInfo.Save(_cacheHelper);

        var isCommonMap = GameMapData.IsCommonMap(playerInfo.ObjectInfo.MapId);

        var lastPartKey = isCommonMap
            ? MapHelper.CreatePartKey(playerInfo.ObjectInfo.MapId, _lastCell!)
            : MapHelper.CreatePartKey(playerInfo.ObjectInfo.MapId, playerInfo.ObjectInfo.MapSubId);

        var currentPartKey = isCommonMap
            ? MapHelper.CreatePartKey(playerInfo.ObjectInfo.MapId, playerInfo.ObjectInfo.CurrentCell)
            : MapHelper.CreatePartKey(playerInfo.ObjectInfo.MapId, playerInfo.ObjectInfo.MapSubId);

        var lastManageServer = isCommonMap
            ? MapHelper.GetManageServerId(lastPartKey)
            : MapHelper.GetManageServerId(playerInfo.ObjectInfo.MapSubId);

        var currentManageServer = isCommonMap
            ? MapHelper.GetManageServerId(currentPartKey)
            : MapHelper.GetManageServerId(playerInfo.ObjectInfo.MapSubId);

        // 담당 서버가 변경되었을 경우 이전 서버에게 떠났음을 알림
        if (lastManageServer != currentManageServer)
        {
            var leaveSubject = SubjectHelper.GetLeaveManageSubject(playerInfo.ObjectInfo, lastManageServer);
            _natsClient.Publish(leaveSubject, MessagePackSerializer.Serialize((lastPartKey, playerInfo.ObjectInfo.GetGameObjectKey())));
        }

        // 현재 서버에 이동 처리 요청
        var moveSubject = SubjectHelper.GetUpdateManageSubject(playerInfo.ObjectInfo, currentManageServer);
        _natsClient.Publish(moveSubject, MessagePackSerializer.Serialize((lastPartKey, objectInfo: playerInfo.ObjectInfo)));
        
        // 이동 과정에서 새로 스폰되는 오브젝트 정보 전송
        if (GameMapData.IsCommonMap(playerInfo.ObjectInfo.MapId))
        {
            RequestSpawnInfo(playerInfo.ObjectInfo.MapId, cellsToRemove);
        }

        if (moveRequest.Direction != DirectionType.NONE)
        {
            // 자신의 이동이므로 큐에 즉시 넣음
            _updateObjectManager.EnqueueUpdateObject(playerInfo.ObjectInfo);
        }

        await Task.Delay((int)(moveElapsedTime * 1000));

        var isArrive = true;
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
        _lastCell = Cell.Clone(playerInfo.ObjectInfo.CurrentCell);
        playerInfo.ObjectInfo.CurrentCell = Cell.Clone(playerInfo.ObjectInfo.TargetCell);
        await playerInfo.ObjectInfo.Save(_cacheHelper);

        using var packet = PacketMaker.U_TO_C_MOVE(playerInfo.ObjectInfo.ObjectId, ErrorCode.SUCCESS, playerInfo.ObjectInfo);
        _sendToClient(packet);
    }

    private float GetMoveSpeed()
    {
        if (playerInfo.Boosts.Contains(BoostType.SPEED))
        {
            return 2;
        }

        if (playerInfo.Hp <= 0)
        {
            return 0.5f;
        }

        return 1;
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
        var lastBoundCellList = isAll ? [] : _lastCell?.GetBoundCellList();
        var currentBoundCellList = playerInfo.ObjectInfo.CurrentCell.GetBoundCellList();

        var objectSpawnList = currentBoundCellList
            .Except(lastBoundCellList!)
            .Select(cell => MapHelper.CreatePartKey(playerInfo.ObjectInfo.MapId, cell))
            .GroupBy(
                MapHelper.GetManageServerId,
                (serverId, positionKeys) => new { serverId, positionKeyList = positionKeys.ToList() }
            )
            .ToList();

        var removeCellsByServer = cellsToRemove
            .GroupBy(
                cell => MapHelper.GetManageServerId(MapHelper.CreatePartKey(playerInfo.ObjectInfo.MapId, cell)),
                (serverId, cells) => new { serverId, cells = cells.ToList() }
            )
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
    }

    public void Dispose()
    {
        if (_disposed) return;

        _moveLock.Dispose();
        _moveQueue.Clear();
        
        _disposed = true;
    }
}