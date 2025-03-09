using System.Collections.Concurrent;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.helpers;
using network.interfaces;
using network.packets;

namespace game_server.controllers;

public class CommonMapController(
    ILogger logger,
    INatsClient natsClient,
    CancellationTokenSource cts,
    ICacheHelper cacheHelper,
    IServerConfig serverConfig,
    MapId mapId)
    : BaseMapController(logger, natsClient, cts, cacheHelper, serverConfig)
{
    private readonly ConcurrentDictionary<string, HashSet<string>> _objectPositionDict = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _positionLocks = new();
    private readonly ReaderWriterLockSlim _globalLock = new ReaderWriterLockSlim();

    public void Initialize()
    {
        InitializeManageParts();
        SubscribeToMapEvents();
    }

    private void InitializeManageParts()
    {
        var managePartList = MapHelper.GetManagePartList(ServerConfig.ServerId);
        var managePositionKeyList = new List<string>();
        foreach (var positions in managePartList.Values)
        {
            managePositionKeyList.AddRange(positions);
        }
        foreach (var positionKey in managePositionKeyList)
        {
            _objectPositionDict[positionKey] = [];
            _positionLocks[positionKey] = new SemaphoreSlim(1, 1);
        }
    }

    private SemaphoreSlim GetOrCreatePositionLock(string positionKey)
    {
        return _positionLocks.GetOrAdd(positionKey, _ => new SemaphoreSlim(1, 1));
    }

    private void SubscribeToMapEvents()
    {
        var subjects = new Dictionary<string, Func<byte[], Task>>
        {
            { SubjectHelper.GetUpdateInfoSubject(mapId, 0, ServerConfig.ServerId), HandleUpdateInfo },
            { SubjectHelper.GetSocialActionSubject(mapId, 0, ServerConfig.ServerId), HandleSocialAction },
            { SubjectHelper.GetUpdateManageSubject(mapId, 0, ServerConfig.ServerId), UpdateManageObjectAsync },
            { SubjectHelper.GetLeaveManageSubject(mapId, 0, ServerConfig.ServerId), LeaveManageObjectAsync },
            { SubjectHelper.GetSpawnManageSubject(mapId, 0, ServerConfig.ServerId), SpawnManageObjectAsync },
            { SubjectHelper.GetDestroyObjectSubject(mapId, 0, ServerConfig.ServerId), DestroyManageObjectAsync }
        };

        foreach (var (subject, handler) in subjects)
        {
            SubscribeWithHandler(subject, handler);
        }

        NatsClient.Subscribe(SubjectHelper.GetBroadcastUpdateSubject(mapId, 0, ServerConfig.ServerId), BroadcastUpdateObject);
        NatsClient.Subscribe(SubjectHelper.GetBroadcastDestroySubject(mapId, 0, ServerConfig.ServerId), BroadcastDestroyObject);
    }

    private async Task UpdateManageObjectAsync(byte[] message)
    {
        var (lastPositionKey, objectInfo) = MessagePackSerializer.Deserialize<(string, GameObjectInfo)>(message);
        var objectKey = GameObjectInfo.MakeObjectKey(objectInfo.ObjectType, objectInfo.ObjectId);
        var currentPositionKey = MapHelper.CreatePartKey(objectInfo.MapId, objectInfo.TargetCell);
        
        // 락 획득 순서를 일관되게 정렬하여 데드락 방지
        var firstKey = string.CompareOrdinal(lastPositionKey, currentPositionKey) < 0 ? lastPositionKey : currentPositionKey;
        var secondKey = firstKey == lastPositionKey ? currentPositionKey : lastPositionKey;

        var firstLock = GetOrCreatePositionLock(firstKey);
        await firstLock.WaitAsync();
        try
        {
            // 두 키가 다른 경우에만 두 번째 락 획득
            if (firstKey != secondKey)
            {
                var secondLock = GetOrCreatePositionLock(secondKey);
                await secondLock.WaitAsync();
                try
                {
                    await UpdateObjectPositionCoreAsync(lastPositionKey, currentPositionKey, objectKey);
                }
                finally
                {
                    secondLock.Release();
                }
            }
            else
            {
                await UpdateObjectPositionCoreAsync(lastPositionKey, currentPositionKey, objectKey);
            }
        }
        finally
        {
            firstLock.Release();
        }

        // 락 바깥에서 브로드캐스트 작업 수행
        BroadcastObjectMove(lastPositionKey, objectInfo);
        BroadcastObjectMove(currentPositionKey, objectInfo);
    }

    private Task UpdateObjectPositionCoreAsync(string lastPositionKey, string currentPositionKey, string objectKey)
    {
        // 실제 데이터 수정 부분만 락 내부에서 수행
        if (_objectPositionDict.TryGetValue(lastPositionKey, out var lastPositionSet))
        {
            _ = lastPositionSet.Remove(objectKey);
        }

        if (!_objectPositionDict.TryGetValue(currentPositionKey, out var currentSet))
        {
            throw new Exception($"Invalid position key: {currentPositionKey}");
        }
        currentSet.Add(objectKey);
        
        return Task.CompletedTask;
    }

    private async Task LeaveManageObjectAsync(byte[] message)
    {
        var (positionKey, objectKey) = MessagePackSerializer.Deserialize<(string, string)>(message);
        
        var positionLock = GetOrCreatePositionLock(positionKey);
        await positionLock.WaitAsync();
        try
        {
            if (_objectPositionDict.TryGetValue(positionKey, out var positionSet))
            {
                positionSet.Remove(objectKey);
            }
        }
        finally
        {
            positionLock.Release();
        }
    }

    private Task SpawnManageObjectAsync(byte[] message)
    {
        var (objectKey, positionKeyList, cellsToRemove) = 
            MessagePackSerializer.Deserialize<(string, List<string>, List<Cell>)>(message);
        
        var spawnList = new HashSet<string>();
        var cellsWithObjects = new List<Cell>();

        // 다중 위치 락을 한 번에 가져오기 위해 읽기 전용 락 사용
        _globalLock.EnterReadLock();
        try
        {
            foreach (var positionKey in positionKeyList)
            {
                if (_objectPositionDict.TryGetValue(positionKey, out var objects))
                {
                    foreach (var obj in objects)
                    {
                        spawnList.Add(obj);
                    }
                }
            }

            foreach (var cell in cellsToRemove)
            {
                var key = MapHelper.CreatePartKey(mapId, cell);
                if (_objectPositionDict.TryGetValue(key, out var objects) && objects.Count > 0)
                {
                    cellsWithObjects.Add(cell);
                }
            }
        }
        finally
        {
            _globalLock.ExitReadLock();
        }

        if (spawnList.Count <= 0 && cellsWithObjects.Count <= 0)
        {
            return Task.CompletedTask;
        }
        
        using var packet = PacketMaker.G_TO_U_SPAWN(spawnList.ToList(), cellsWithObjects);
        NatsClient.Publish(objectKey, packet.ToBytes());

        return Task.CompletedTask;
    }

    private Task DestroyManageObjectAsync(byte[] message)
    {
        var (positionKey, objectKey) = MessagePackSerializer.Deserialize<(string, string)>(message);
        
        // 글로벌 정보를 수정하므로 쓰기 락 사용
        _globalLock.EnterWriteLock();
        try
        {
            foreach (var set in _objectPositionDict.Values)
            {
                set.Remove(objectKey);
            }
        }
        finally
        {
            _globalLock.ExitWriteLock();
        }

        var positionCell = MapHelper.CreateCell(positionKey);
        var targetServerList = MapHelper.GetBoundServerList(mapId, positionCell);

        var destroyMessage = MessagePackSerializer.Serialize((positionKey, objectKey));
        BroadcastObjectDestroy(targetServerList, destroyMessage);

        using var packet = PacketMaker.G_TO_U_DESTROY(objectKey);
        BroadcastPacket(positionKey, packet);
        
        return Task.CompletedTask;
    }

    private void BroadcastObjectDestroy(List<int> targetServerList, byte[] message)
    {
        foreach (var subject in targetServerList.Select(targetServer => SubjectHelper.GetBroadcastDestroySubject(mapId, 0, targetServer)))
        {
            NatsClient.Publish(subject, message);
        }
    }

    private void BroadcastObjectMove(string positionKey, GameObjectInfo objectInfo)
    {
        var lastCell = objectInfo.CurrentCell;
        var movedCell = objectInfo.TargetCell;
        
        var lastBoundCells = lastCell.GetBoundCellList();
        var newBoundCells = movedCell.GetBoundCellList();

        var allBoundCells = lastBoundCells.Union(newBoundCells);
        var affectedServers = allBoundCells
            .Select(cell => {
                var posKey = MapHelper.CreatePartKey(mapId, cell);
                var serverId = MapHelper.GetManageServerId(posKey);
                return new { posKey, serverId };
            })
            .Where(item => item.serverId > 0)
            .GroupBy(
                item => item.serverId,
                (serverId, items) => new { serverId, cells = items.Select(i => i.posKey).ToList() }
            )
            .ToList();
        
        foreach (var serverGroup in affectedServers)
        {
            var subject = SubjectHelper.GetBroadcastUpdateSubject(mapId, 0, serverGroup.serverId);
            var message = MessagePackSerializer.Serialize((positionKey, objectInfo));
            NatsClient.Publish(subject, message);
        }
    }

    protected override void BroadcastPacket(string positionKey, IPacket packet)
    {
        var pivotCell = MapHelper.CreateCell(positionKey);
        var boundCellList = pivotCell.GetBoundCellList();
        var channels = new List<string>();

        // 읽기 작업만 수행하므로 읽기 락 사용
        _globalLock.EnterReadLock();
        try
        {
            foreach (var boundPositionKey in boundCellList.Select(boundCell => MapHelper.CreatePartKey(mapId, boundCell)))
            {
                if (!_objectPositionDict.TryGetValue(boundPositionKey, out var channelSet) || channelSet.Count <= 0)
                {
                    continue;
                }
                channels.AddRange(channelSet);
            }
        }
        finally
        {
            _globalLock.ExitReadLock();
        }

        // 락 바깥에서 발행 작업 수행
        var packetBytes = packet.ToBytes();
        foreach (var channel in channels)
        {
            NatsClient.Publish(channel, packetBytes);
        }
    }

    private void BroadcastUpdateObject(string _, byte[] message)
    {
        var (partKey, objectInfo) = MessagePackSerializer.Deserialize<(string, GameObjectInfo)>(message);

        using var packet = PacketMaker.G_TO_U_UPDATE_OBJECT(objectInfo);
        BroadcastPacket(partKey, packet);
    }

    private void BroadcastDestroyObject(string _, byte[] message)
    {
        var (partKey, objectKey) = MessagePackSerializer.Deserialize<(string, string)>(message);

        using var packet = PacketMaker.G_TO_U_DESTROY(objectKey);
        BroadcastPacket(partKey, packet);
    }

    public override async Task ShutdownAsync()
    {
        _globalLock.EnterWriteLock();
        _globalLock.ExitWriteLock();
        
        // 필요한 경우 개별 위치 락도 확인
        foreach (var posLock in _positionLocks.Values)
        {
            await posLock.WaitAsync();
            posLock.Release();
        }
        
        Dispose();
    }

    // 리소스 해제를 위한 Dispose 패턴 구현
    public void Dispose()
    {
        _globalLock.Dispose();
        foreach (var posLock in _positionLocks.Values)
        {
            posLock.Dispose();
        }
    }
}