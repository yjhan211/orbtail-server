using System.Collections.Concurrent;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.helpers;
using network.interfaces;
using network.packets;

namespace game_server.controllers;

public class CommonMapController : BaseMapController
{
    private readonly ConcurrentDictionary<string, HashSet<string>> _objectPositionDict;
    private readonly MapId _mapId;

 // ReSharper disable once ConvertToPrimaryConstructor
    public CommonMapController(
        ILogger logger,
        INatsClient natsClient,
        CancellationTokenSource cts,
        ICacheHelper cacheHelper,
        IServerConfig serverConfig,
        MapId mapId) 
        : base(logger, natsClient, cts, cacheHelper, serverConfig)
    {
        _mapId = mapId;
        _objectPositionDict = new ConcurrentDictionary<string, HashSet<string>>();
    }

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
        }
    }

    private void SubscribeToMapEvents()
    {
        var subjects = new Dictionary<string, Func<byte[], Task>>
        {
            { SubjectHelper.GetUpdateInfoSubject(_mapId, 0, ServerConfig.ServerId), HandleUpdateInfo },
            { SubjectHelper.GetSocialActionSubject(_mapId, 0, ServerConfig.ServerId), HandleSocialAction },
            { SubjectHelper.GetUpdateManageSubject(_mapId, 0, ServerConfig.ServerId), UpdateManageObjectAsync },
            { SubjectHelper.GetLeaveManageSubject(_mapId, 0, ServerConfig.ServerId), LeaveManageObjectAsync },
            { SubjectHelper.GetSpawnManageSubject(_mapId, 0, ServerConfig.ServerId), SpawnManageObjectAsync },
            { SubjectHelper.GetDestroyObjectSubject(_mapId, 0, ServerConfig.ServerId), DestroyManageObjectAsync }
        };

        foreach (var (subject, handler) in subjects)
        {
            SubscribeWithHandler(subject, handler);
        }

        // 즉시 실행되는 이벤트들
        NatsClient.Subscribe(
            SubjectHelper.GetBroadcastUpdateSubject(_mapId, 0, ServerConfig.ServerId),
            BroadcastUpdateObject);
        
        NatsClient.Subscribe(
            SubjectHelper.GetBroadcastDestroySubject(_mapId, 0, ServerConfig.ServerId),
            BroadcastDestroyObject);
    }

    private async Task UpdateManageObjectAsync(byte[] message)
    {
        var (lastPositionKey, objectInfo) = MessagePackSerializer.Deserialize<(string, GameObjectInfo)>(message);
        var objectKey = GameObjectInfo.MakeObjectKey(objectInfo.ObjectType, objectInfo.ObjectId);
        var currentPositionKey = MapHelper.CreatePartKey(objectInfo.MapId, objectInfo.CurrentCell);
        
        if (lastPositionKey != currentPositionKey)
        {
            await UpdateObjectPositionAsync(lastPositionKey, currentPositionKey, objectKey);
        }
        
        await BroadcastObjectMove(lastPositionKey, objectInfo);
        await BroadcastObjectMove(currentPositionKey, objectInfo);
    }

    private async Task UpdateObjectPositionAsync(string lastPositionKey, string currentPositionKey, string objectKey)
    {
        await MapLock.WaitAsync();
        try
        {
            if (_objectPositionDict.TryGetValue(lastPositionKey, out var lastPositionSet))
            {
                var removed = lastPositionSet.Remove(objectKey);
            }

            if (!_objectPositionDict.TryGetValue(currentPositionKey, out var currentSet))
            {
                throw new Exception($"Invalid position key: {currentPositionKey}");
            }
   
            currentSet.Add(objectKey);
        }
        finally
        {
            MapLock.Release();
        }
    }

    private async Task LeaveManageObjectAsync(byte[] message)
    {
        var (positionKey, objectKey) = MessagePackSerializer.Deserialize<(string, string)>(message);
        await MapLock.WaitAsync();
        try
        {
            if (_objectPositionDict.TryGetValue(positionKey, out var positionSet))
            {
                positionSet.Remove(objectKey);
            }
        }
        finally
        {
            MapLock.Release();
        }
    }

    private async Task SpawnManageObjectAsync(byte[] message)
    {
        var (objectKey, positionKeyList, cellsToRemove) = 
            MessagePackSerializer.Deserialize<(string, List<string>, List<Cell>)>(message);
        
        var spawnList = new HashSet<string>();
        var cellsWithObjects = new List<Cell>();

        await MapLock.WaitAsync();
        try
        {
            foreach (var positionKey in positionKeyList)
            {
                if (_objectPositionDict.TryGetValue(positionKey, out var objects))
                {
                    foreach(var obj in objects)
                    {
                        spawnList.Add(obj);
                    }
                }
            }

            foreach (var cell in cellsToRemove)
            {
                var key = MapHelper.CreatePartKey(_mapId, cell);
                if (_objectPositionDict.TryGetValue(key, out var objects) && objects.Count > 0)
                {
                    cellsWithObjects.Add(cell);
                }
            }
        }
        finally
        {
            MapLock.Release();
        }

        if (spawnList.Count <= 0 && cellsWithObjects.Count == 0)
        {
            return;
        }

        using var packet = PacketMaker.G_TO_U_SPAWN(spawnList.ToList(), cellsWithObjects);
        NatsClient.Publish(objectKey, packet.ToBytes());
    }

    private async Task DestroyManageObjectAsync(byte[] message)
    {
        var (positionKey, objectKey) = MessagePackSerializer.Deserialize<(string, string)>(message);
        await MapLock.WaitAsync();
        try
        {
            foreach (var set in _objectPositionDict.Values)
            {
                set.Remove(objectKey);
            }
        }
        finally
        {
            MapLock.Release();
        }

        var positionCell = MapHelper.CreateCell(positionKey);
        var targetServerList = MapHelper.GetBoundServerList(_mapId, positionCell);

        var destroyMessage = MessagePackSerializer.Serialize((positionKey, objectKey));
        BroadcastObjectDestroy(targetServerList, destroyMessage);

        using var packet = PacketMaker.G_TO_U_DESTROY(objectKey);
        BroadcastPacket(positionKey, packet);
    }

    private void BroadcastObjectDestroy(List<int> targetServerList, byte[] message)
    {
        foreach (var subject in targetServerList.Select(targetServer => 
                     SubjectHelper.GetBroadcastDestroySubject(_mapId, 0, targetServer)))
        {
            NatsClient.Publish(subject, message);
        }
    }

    private async Task BroadcastObjectMove(string positionKey, GameObjectInfo objectInfo)
    {
        var movedCell = objectInfo.CurrentCell;
        var lastCell = MapHelper.CreateCell(positionKey);
        
        var lastBoundCells = lastCell.GetBoundCellList();
        var newBoundCells = movedCell.GetBoundCellList();

        await MapLock.WaitAsync();
        try 
        {
            var allBoundCells = lastBoundCells.Union(newBoundCells);

            foreach(var cell in allBoundCells)
            {
                var key = MapHelper.CreatePartKey(_mapId, cell);
                if(!_objectPositionDict.TryGetValue(key, out var objects))
                    continue;

                foreach(var obj in objects.Where(obj => obj.StartsWith("1_")))
                {
                    if (obj == objectInfo.GetGameObjectKey()) continue;

                    var serverId = MapHelper.GetManageServerId(key);
                    var playerPositionKeys = new List<string>();
                    var playerCellsToRemove = new List<Cell>();

                    if (lastBoundCells.Contains(cell) && !newBoundCells.Contains(cell))
                    {
                        playerCellsToRemove.Add(movedCell);
                    }
                    else if (newBoundCells.Contains(cell) && !lastBoundCells.Contains(cell))
                    {
                        playerPositionKeys.Add(MapHelper.CreatePartKey(_mapId, movedCell));
                    }

                    var spawnMessage = MessagePackSerializer.Serialize((obj, playerPositionKeys, playerCellsToRemove));
                    var spawnSubject = SubjectHelper.GetSpawnManageSubject(_mapId, 0, serverId);
                    NatsClient.Publish(spawnSubject, spawnMessage);
                }
            }
        }
        finally 
        {
            MapLock.Release();
        }

        var affectedServers = newBoundCells
            .Select(cell => {
                var posKey = MapHelper.CreatePartKey(_mapId, cell);
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
            var subject = SubjectHelper.GetBroadcastUpdateSubject(_mapId, 0, serverGroup.serverId);
            var message = MessagePackSerializer.Serialize((positionKey, objectInfo));
            NatsClient.Publish(subject, message);
        }
    }

    protected override void BroadcastPacket(string positionKey, IPacket packet)
    {
        var pivotCell = MapHelper.CreateCell(positionKey);
        var boundCellList = pivotCell.GetBoundCellList();

        foreach (var boundPositionKey in boundCellList.Select(boundCell => MapHelper.CreatePartKey(_mapId, boundCell)))
        {
            if (!_objectPositionDict.TryGetValue(boundPositionKey, out var channelSet) || channelSet.Count <= 0)
            {
                continue;
            }
            foreach (var channel in channelSet)
            {
                NatsClient.Publish(channel, packet.ToBytes());
            }
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
        await MapLock.WaitAsync();
        MapLock.Release();
    }
}