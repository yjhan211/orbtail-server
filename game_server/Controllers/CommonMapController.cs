using System.Collections.Concurrent;
using game_server.handlers;
using MessagePack;
using network.common;
using network.common.data.models;
using network.helpers;
using network.infrastructure;
using network.interfaces;
using network.managers;
using network.packets;
using StackExchange.Redis;

namespace game_server.controllers;

public class CommonMapController
{
    private readonly Dictionary<string, Func<RedisValue, Task>> _asyncHandlers;
    private readonly CancellationTokenSource _cts;
    private readonly Dictionary<string, Action<RedisValue>> _immediateHandlers;
    private readonly LogManager _logManager;
    private readonly NatsClient _natsClient;
    
    private readonly SemaphoreSlim _mapLock;
    private readonly MapId _mapId;

    private readonly ConcurrentDictionary<string, HashSet<string>> _objectPositionDict;
    
    // private readonly ConcurrentDictionary<int, HashSet<ExploreTargetInfo>> _exploreTargetDict;
    // private readonly ConcurrentDictionary<int, HashSet<JobResourceInfo>> _jobResourceDict;
    // private readonly List<Cell> _manageCellList;

    private readonly Dictionary<Type, object> _updateHandlers = new()
    {
        { typeof(PlayerInfo), new UpdateHandler<PlayerInfo>(PacketMaker.G_TO_U_PLAYER_INFO) },
        { typeof(ExploreTargetInfo), new UpdateHandler<ExploreTargetInfo>(PacketMaker.G_TO_U_EXPLORE_TARGET_INFO) },
        { typeof(JobResourceInfo), new UpdateHandler<JobResourceInfo>(PacketMaker.G_TO_U_JOB_RESOURCE_INFO) },
        { typeof(CampInfo), new UpdateHandler<CampInfo>(PacketMaker.G_TO_U_CAMP_INFO) }
    };

    public CommonMapController(LogManager logManager, NatsClient natsClient, CancellationTokenSource cts, MapId mapId)
    {
        _logManager = logManager;
        _natsClient = natsClient;
        _cts = cts;
        _mapId = mapId;
        _objectPositionDict = new ConcurrentDictionary<string, HashSet<string>>();
        _mapLock = new SemaphoreSlim(1, 1);
        
        // _exploreTargetDict = new ConcurrentDictionary<int, HashSet<ExploreTargetInfo>>();
        // _jobResourceDict = new ConcurrentDictionary<int, HashSet<JobResourceInfo>>();
        // _manageCellList = [];
        _immediateHandlers = new Dictionary<string, Action<RedisValue>>
        {
            { SubjectHelper.GetUpdateInfoSubject(_mapId, 0, Program.GameServerId), HandleUpdateInfo },
            { SubjectHelper.GetSocialActionSubject(mapId, 0, Program.GameServerId), HandleSocialAction },
            { SubjectHelper.GetBroadcastUpdateSubject(_mapId, 0, Program.GameServerId), BroadcastUpdateObject },
            { SubjectHelper.GetBroadcastDestroySubject(_mapId, 0, Program.GameServerId), BroadcastDestroyObject }
        };

        _asyncHandlers = new Dictionary<string, Func<RedisValue, Task>>
        {
            { SubjectHelper.GetUpdateManageSubject(_mapId, 0, Program.GameServerId), MoveManageObjectAsync },
            { SubjectHelper.GetLeaveManageSubject(_mapId, 0, Program.GameServerId), LeaveManageObjectAsync },
            { SubjectHelper.GetSpawnManageSubject(_mapId, 0, Program.GameServerId), SpawnManageObjectAsync },
            { SubjectHelper.GetDestroyObjectSubject(_mapId, 0, Program.GameServerId), DestroyManageObjectAsync },
            // { SubjectHelper.GetCreateJobResourceSubject(_mapId, 0, Program.GameServerId), CreateJobResourceInfoAsync }_
        };
    }

    public void Initialize()
    {
        InitializeManageParts();
        SubscribeToMapEvents();
        
        // TODO 채집 시스템
        // await CleanUpMapResource();
        // _ = Task.Run(CreateExploreTargetTask, _cts.Token);
    }

    private void InitializeManageParts()
    {
        var managePartList = MapHelper.GetManagePartList(Program.GameServerId);
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
    
    // TODO 채집 시스템
    // private static async Task CleanUpMapResource()
    // {
    //     if (Program.GameServerId == 1)
    //     {
    //         var exploreTargetValues = await CacheHelper.Instance.HashGetAllAsync(ExploreTargetInfo.HashKey);
    //         foreach (var entry in exploreTargetValues)
    //         {
    //             if (!long.TryParse(entry.Name, out var exploreTargetId)) continue;
    //
    //             await ExploreTargetInfo.Delete(exploreTargetId);
    //
    //             var objectField = GameObjectInfo.MakeObjectKey(ObjectType.EXPLORETARGET, exploreTargetId);
    //             await GameObjectInfo.Delete(objectField);
    //         }
    //     }
    //     else
    //     {
    //         await Task.Delay(10000);
    //     }
    // }
    
    // private async Task CreateExploreTargetTask()
    // {
    //     using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
    //     while (await timer.WaitForNextTickAsync(_cts.Token))
    //         try
    //         {
    //             if (!await CreateExploreTarget()) break;
    //         }
    //         catch (Exception ex)
    //         {
    //             _logManager.WriteErrorLog(ex);
    //         }
    // }
    
    // private async Task<bool> CreateExploreTarget()
    // {
        // var genList = CommonMapData.GetGenExploreIdList(_mapId);
        // if (genList == null) return false;
        //
        // await _mapLock.WaitAsync();
        // try
        // {
        //     Random random = new();
        //     foreach (var partExploreTargetInfo in _exploreTargetDict)
        //     {
        //         if (3 <= partExploreTargetInfo.Value.Count) continue;
        //
        //         var createCell = _manageCellList[random.Next(0, _manageCellList.Count)];
        //         var createPositionKey = CommonMapData.CreatePartKey(_mapId, createCell);
        //         var exploreTargetUid = await CacheHelper.Instance.StringIncrementAsync("temp_explore_target_uid");
        //
        //         var objectInfo = new GameObjectInfo
        //         {
        //             ObjectType = ObjectType.EXPLORETARGET,
        //             ObjectId = exploreTargetUid,
        //             CurrentCell = createCell,
        //             TargetCell = createCell,
        //             MapId = _mapId
        //         };
        //
        //         var exploreTargetInfo =
        //             new ExploreTargetInfo(exploreTargetUid, genList[random.Next(0, genList.Count)], objectInfo);
        //         partExploreTargetInfo.Value.Add(exploreTargetInfo);
        //
        //         _objectPositionDict.AddOrUpdate(
        //             createPositionKey,
        //             [objectInfo.GetGameObjectKey()],
        //             (_, set) =>
        //             {
        //                 set.Add(objectInfo.GetGameObjectKey());
        //                 return set;
        //             }
        //         );
        //
        //         await exploreTargetInfo.Save();
        //
        //         var targetServerList = CommonMapData.GetBoundServerList(_mapId, objectInfo.CurrentCell);
        //         var targetSubjectList = targetServerList.Select(targetServer =>
        //             SubjectHelper.GetBroadcastUpdateSubject(_mapId, 0, targetServer));
        //         foreach (var subject in targetSubjectList)
        //             _natsClient.Publish(subject, MessagePackSerializer.Serialize((createPositionKey, objectInfo)));
        //     }
        //
        //     return true;
        // }
        // catch (Exception ex)
        // {
        //     _logManager.WriteErrorLog(ex);
        //     return false;
        // }
        // finally
        // {
        //     _mapLock.Release();
        // }
    // }

    private void SubscribeToMapEvents()
    {
        foreach (var (subject, handler) in _immediateHandlers)
            _natsClient.Subscribe(subject, (_, msg) =>
            {
                try
                {
                    handler(msg);
                }
                catch (Exception ex)
                {
                    _logManager.WriteErrorLog(ex);
                }
            });

        foreach (var (subject, handler) in _asyncHandlers)
        {
            _natsClient.Subscribe(subject, MessageHandler);
            continue;

            async void MessageHandler(string _, byte[] msg)
            {
                try
                {
                    await handler(msg);
                }
                catch (Exception ex)
                {
                    _logManager.WriteErrorLog(ex);
                }
            }
        }
    }

    private void HandleUpdateInfo(RedisValue message)
    {
        if (TryDeserialize<PlayerInfo>(message, out var key1, out var playerInfo))
        {
            var handler = _updateHandlers[typeof(PlayerInfo)];
            using var packet = ((IUpdateHandler<PlayerInfo>)handler).MakePacket(playerInfo);
            BroadcastPacket(key1, packet);
            return;
        }
    
        if (TryDeserialize<ExploreTargetInfo>(message, out var key2, out var exploreInfo))
        {
            var handler = _updateHandlers[typeof(ExploreTargetInfo)];
            using var packet = ((IUpdateHandler<ExploreTargetInfo>)handler).MakePacket(exploreInfo);
            BroadcastPacket(key2, packet);
            return;
        }
    
        if (TryDeserialize<JobResourceInfo>(message, out var key3, out var jobInfo))
        {
            var handler = _updateHandlers[typeof(JobResourceInfo)];
            using var packet = ((IUpdateHandler<JobResourceInfo>)handler).MakePacket(jobInfo);
            BroadcastPacket(key3, packet);
            return;
        }
    
        if (TryDeserialize<CampInfo>(message, out var key4, out var campInfo))
        {
            var handler = _updateHandlers[typeof(CampInfo)];
            using var packet = ((IUpdateHandler<CampInfo>)handler).MakePacket(campInfo);
            BroadcastPacket(key4, packet);
            return;
        }
    }
    
    private bool TryDeserialize<T>(RedisValue message, out string key, out T info) where T : IMessagePackObject
    {
        try
        {
            (key, info) = MessagePackSerializer.Deserialize<(string, T)>(message);
            return true;
        }
        catch
        {
            key = null;
            info = default;
            return false;
        }
    }
    
    private void HandleSocialAction(RedisValue message)
    {
        var (partKey, (playerId, socialActionType)) = MessagePackSerializer.Deserialize<(string, (long, SocialActionType))>(message);

        using var packet = PacketMaker.G_TO_U_SOCIAL_ACTION(playerId, socialActionType);
        BroadcastPacket(partKey, packet);
    }

    private async Task MoveManageObjectAsync(RedisValue message)
    {
        var (lastPositionKey, objectInfo) = MessagePackSerializer.Deserialize<(string, GameObjectInfo)>(message);
        var objectKey = GameObjectInfo.MakeObjectKey(objectInfo.ObjectType, objectInfo.ObjectId);
        var currentPositionKey = MapHelper.CreatePartKey(objectInfo.MapId, objectInfo.CurrentCell);

        await UpdateObjectPositionAsync(lastPositionKey, currentPositionKey, objectKey);
        await BroadcastObjectMove(lastPositionKey, objectInfo);
        await BroadcastObjectMove(currentPositionKey, objectInfo);
    }

    private void BroadcastPacket(string positionKey, IPacket packet)
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
                _natsClient.Publish(channel, packet.ToBytes());
            }
        }
    }

    private void BroadcastObjectDestroy(List<int> targetServerList, byte[] message)
    {
        foreach (var subject in targetServerList.Select(targetServer => SubjectHelper.GetBroadcastDestroySubject(_mapId, 0, targetServer)))
        {
            _natsClient.Publish(subject, message);
        }
    }

    private async Task BroadcastObjectMove(string positionKey, GameObjectInfo objectInfo)
    {
        var movedCell = objectInfo.CurrentCell;
        var lastCell = MapHelper.CreateCell(positionKey);
        
        _logManager.WriteDebugLog($"=== BroadcastObjectMove Start ===");
        _logManager.WriteDebugLog($"Object: {objectInfo.GetGameObjectKey()}, LastPosition: {positionKey}, NewPosition: {MapHelper.CreatePartKey(_mapId, movedCell)}");

        var lastBoundCells = lastCell.GetBoundCellList();
        var newBoundCells = movedCell.GetBoundCellList();

        // 움직이는 유저의 bound 영역 안에 있는 가만히 있는 유저들에게도 스폰 체크 메시지 전송
        await _mapLock.WaitAsync();
        try 
        {
            var allBoundCells = lastBoundCells.Union(newBoundCells);

            foreach(var cell in allBoundCells)
            {
                var key = MapHelper.CreatePartKey(_mapId, cell);
                if(!_objectPositionDict.TryGetValue(key, out var objects))
                    continue;

                // 이 셀에 있는 플레이어들에게도 스폰 체크 메시지 전송
                foreach(var obj in objects.Where(obj => obj.StartsWith("1_")))
                {
                    if (obj == objectInfo.GetGameObjectKey()) continue;  // 자기 자신 제외

                    var serverId = MapHelper.GetManageServerId(key);
                    var playerPositionKeys = new List<string>();
                    var playerCellsToRemove = new List<Cell>();

                    // 이전 bound에 있었고 새 bound에는 없는 경우
                    if (lastBoundCells.Contains(cell) && !newBoundCells.Contains(cell))
                    {
                        playerCellsToRemove.Add(movedCell);
                    }
                    // 새 bound에 있고 이전 bound에는 없는 경우
                    else if (newBoundCells.Contains(cell) && !lastBoundCells.Contains(cell))
                    {
                        playerPositionKeys.Add(MapHelper.CreatePartKey(_mapId, movedCell));
                    }

                    var spawnMessage = MessagePackSerializer.Serialize((obj, playerPositionKeys, playerCellsToRemove));
                    var spawnSubject = SubjectHelper.GetSpawnManageSubject(_mapId, 0, serverId);
                    _natsClient.Publish(spawnSubject, spawnMessage);
                }
            }
        }
        finally 
        {
            _mapLock.Release();
        }

        // 서버간 broadcast 메시지 전송
        var affectedServers = newBoundCells
            .Select(cell => MapHelper.CreatePartKey(_mapId, cell))
            .GroupBy(
                MapHelper.GetManageServerId,
                (serverId, cells) => new { serverId, cells = cells.ToList() }
            );

        foreach (var serverGroup in affectedServers)
        {
            _logManager.WriteDebugLog($"Server {serverGroup.serverId}: {serverGroup.cells.Count} cells");
            var subject = SubjectHelper.GetBroadcastUpdateSubject(_mapId, 0, serverGroup.serverId);
            var message = MessagePackSerializer.Serialize((positionKey, objectInfo));
            _natsClient.Publish(subject, message);
        }
        
        _logManager.WriteDebugLog("=== BroadcastObjectMove End ===");
    }
    
    private async Task UpdateObjectPositionAsync(string lastPositionKey, string currentPositionKey, string objectKey)
    {
        await _mapLock.WaitAsync();
        try
        {
            if (_objectPositionDict.TryGetValue(lastPositionKey, out var lastPositionSet))
            {
                lastPositionSet.Remove(objectKey);
            }

            if (!_objectPositionDict.TryGetValue(currentPositionKey, out var currentSet))
            {
                throw new Exception($"Invalid position key: {currentPositionKey}");
            }
        
            currentSet.Add(objectKey);
        }
        finally
        {
            _mapLock.Release();
        }
    }
    private async Task LeaveManageObjectAsync(RedisValue message)
    {
        var (positionKey, objectKey) = MessagePackSerializer.Deserialize<(string, string)>(message);
        await _mapLock.WaitAsync();
        try
        {
            if (_objectPositionDict.TryGetValue(positionKey, out var positionSet))
            {
                positionSet.Remove(objectKey);
            }
        }
        finally
        {
            _mapLock.Release();
        }
    }

    private async Task SpawnManageObjectAsync(RedisValue message)
    {
        var (userSubject, positionKeyList, cellsToRemove) = MessagePackSerializer.Deserialize<(string, List<string>, List<Cell>)>(message);
           
        _logManager.WriteDebugLog($"=== SpawnManage Start ===");
        _logManager.WriteDebugLog($"User: {userSubject}, PositionKeys: {string.Join(",", positionKeyList)}");
        _logManager.WriteDebugLog($"CellsToRemove: {string.Join(",", cellsToRemove.Select(c => $"{c.X},{c.Y}"))}");

        var spawnList = new HashSet<string>();
        var cellsWithObjects = new List<Cell>();

        await _mapLock.WaitAsync();
        try
        {
            // 새로운 bound 영역의 오브젝트들 수집
            foreach (var positionKey in positionKeyList)
            {
                if (_objectPositionDict.TryGetValue(positionKey, out var objects))
                {
                    foreach(var obj in objects)
                    {
                        spawnList.Add(obj);
                    }
                    _logManager.WriteDebugLog($"Position {positionKey} objects: {string.Join(",", objects)}");
                }
            }

            // cellsToRemove에서 현재 오브젝트가 있는 셀 확인
            if (cellsToRemove != null)
            {
                foreach(var cell in cellsToRemove)
                {
                    var key = MapHelper.CreatePartKey(_mapId, cell);
                    if (_objectPositionDict.TryGetValue(key, out var objects) && objects.Count > 0)
                    {
                        cellsWithObjects.Add(cell);
                        _logManager.WriteDebugLog($"Cell {cell.X},{cell.Y} has objects: {string.Join(",", objects)}");
                    }
                }
            }
        }
        finally
        {
            _mapLock.Release();
        }

        if (spawnList.Count <= 0 && cellsWithObjects.Count == 0)
        {
            _logManager.WriteDebugLog("No objects to spawn or remove");
            return;
        }

        _logManager.WriteDebugLog($"Sending packet - userSubject: {userSubject}, Spawn count: {spawnList.Count}, Remove cells: {cellsWithObjects.Count}");
        using var packet = PacketMaker.G_TO_U_SPAWN(spawnList.ToList(), cellsWithObjects);
        _natsClient.Publish(userSubject, packet.ToBytes());
        
        _logManager.WriteDebugLog("=== SpawnManage End ===");
    }

    private async Task DestroyManageObjectAsync(RedisValue message)
    {
        var (positionKey, objectKey) = MessagePackSerializer.Deserialize<(string, string)>(message);
        await _mapLock.WaitAsync();
        try
        {
            foreach (var set in _objectPositionDict.Values)
            {
                set.Remove(objectKey);
            }
            // foreach (var set in _jobResourceDict.Values)
                // set.RemoveWhere(jobResourceInfo => jobResourceInfo.ObjectInfo.GetGameObjectKey() == objectKey);
        }
        finally
        {
            _mapLock.Release();
        }

        var positionCell = MapHelper.CreateCell(positionKey);
        var targetServerList = MapHelper.GetBoundServerList(_mapId, positionCell);

        var destroyMessage = MessagePackSerializer.Serialize((positionKey, objectKey));
        BroadcastObjectDestroy(targetServerList, destroyMessage);

        using var packet = PacketMaker.G_TO_U_DESTROY(objectKey);
        BroadcastPacket(positionKey, packet);
    }

    // private async Task CreateJobResourceInfoAsync(RedisValue message)
    // {
    //     var (createPositionKey, jobResourceInfo, objectInfo)
    //         = MessagePackSerializer.Deserialize<(string, JobResourceInfo, GameObjectInfo)>(message);
    //
    //     var partId = MapHelper.GetManagePartByPositionKey(createPositionKey);
    //
    //     await _mapLock.WaitAsync();
    //     try
    //     {
    //         _jobResourceDict[partId].Add(jobResourceInfo);
    //         _objectPositionDict.AddOrUpdate(
    //             createPositionKey,
    //             [objectInfo.GetGameObjectKey()],
    //             (_, set) =>
    //             {
    //                 set.Add(objectInfo.GetGameObjectKey());
    //                 return set;
    //             }
    //         );
    //     }
    //     finally
    //     {
    //         _mapLock.Release();
    //     }
    //
    //     BroadcastObjectMove(createPositionKey, objectInfo);
    // }

    private void BroadcastUpdateObject(RedisValue message)
    {
        var (positionKey, objectInfo) = MessagePackSerializer.Deserialize<(string, GameObjectInfo)>(message);

        using var packet = PacketMaker.G_TO_U_MOVE(objectInfo);
        BroadcastPacket(positionKey, packet);
    }

    private void BroadcastDestroyObject(RedisValue message)
    {
        var (positionKey, objectKey) = MessagePackSerializer.Deserialize<(string, string)>(message);

        using var packet = PacketMaker.G_TO_U_DESTROY(objectKey);
        BroadcastPacket(positionKey, packet);
    }

    public async Task ShutdownAsync()
    {
        await _mapLock.WaitAsync();
        _mapLock.Release();
    }
}