using System.Collections.Concurrent;
using MessagePack;
using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;
using network.infrastructure;
using network.interfaces;
using network.managers;
using network.packets;
using StackExchange.Redis;

namespace game_server.controllers;

public class InstanceMapController(LogManager logManager, NatsClient natsClient, CancellationTokenSource cts)
{
    private readonly CancellationTokenSource _cts = cts;

    private readonly SemaphoreSlim _mapLock = new(1, 1);
    private readonly ConcurrentDictionary<string, HashSet<string>> _objectInstanceDict = new();

    private static string EnterInstanceSubject => SubjectHelper.GetEnterInstanceSubject(Program.GameServerId);

    public void Initialize()
    {
        SubscribeToEnterInstance();
    }

    private void SubscribeToEnterInstance()
    {
        natsClient.Subscribe(EnterInstanceSubject, async void (_, msg) => {        
            try 
            {
                await Task.Run(async () => 
                {
                    try
                    {
                        await EnterInstance(msg);
                    }
                    catch (Exception ex)
                    {
                        logManager.WriteErrorLog(ex);
                    }
                });
            }
            catch (Exception ex)
            {
                logManager.WriteErrorLog(ex);
            }
        });
    }

    private void SubscribeToInstanceEvents(MapId mapId, long mapSubId)
    {
        var immediateHandlers = new Dictionary<string, Action<RedisValue>>
        {
            { SubjectHelper.GetUpdateInfoSubject(mapId, mapSubId, Program.GameServerId), HandleUpdateInfo },
            { SubjectHelper.GetSocialActionSubject(mapId, mapSubId, Program.GameServerId), HandleSocialAction },
            { SubjectHelper.GetSpawnManageSubject(mapId, mapSubId, Program.GameServerId), SpawnManageObject },
        };

        var moveSubject = SubjectHelper.GetUpdateManageSubject(mapId, mapSubId, Program.GameServerId);
        var asyncHandlers = new Dictionary<string, Func<RedisValue, Task>>
        {
            { moveSubject, MoveManageObjectAsync },
            { SubjectHelper.GetLeaveManageSubject(mapId, mapSubId, Program.GameServerId), LeaveManageObjectAsync },
            { SubjectHelper.GetDestroyObjectSubject(mapId, mapSubId, Program.GameServerId), DestroyManageObjectAsync }
        };

        foreach (var (subject, handler) in immediateHandlers)
            natsClient.Subscribe(subject, (_, msg) =>
            {
                try
                {
                    handler(msg);
                }
                catch (Exception ex)
                {
                    logManager.WriteErrorLog(ex);
                }
            });

        foreach (var (subject, handler) in asyncHandlers)
        {
            natsClient.Subscribe(subject, (_, msg) => 
            {
                try
                {
                    handler(msg).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    logManager.WriteErrorLog(ex);
                }
            });
        }
    }

    private async Task EnterInstance(RedisValue message)
    {
        var (userSubject, mapId, mapSubId, isLogin)
            = MessagePackSerializer.Deserialize<(string, MapId, long, bool)>(message);
        var instanceKey = MapHelper.CreatePartKey(mapId, mapSubId);

        await _mapLock.WaitAsync();
        try
        {
            var isInit = _objectInstanceDict.TryAdd(instanceKey, []);
            _objectInstanceDict[instanceKey].Add(userSubject);
            
            if (isInit)
            {
                SubscribeToInstanceEvents(mapId, mapSubId);
                var exploreTargetList = GameExploreTargetData.GetListByMap(mapId);
                foreach (var exploreTarget in exploreTargetList)
                {
                    var exploreTargetUid = await CacheHelper.Instance.StringIncrementAsync("temp_explore_target_uid");
                    var objectInfo = new GameObjectInfo
                    {
                        ObjectType = ObjectType.EXPLORETARGET,
                        ObjectId = exploreTargetUid,
                        CurrentCell = exploreTarget.Position,
                        TargetCell = exploreTarget.Position,
                        MapId = mapId,
                        MapSubId = mapSubId,
                    };
                    
                    var exploreTargetInfo = new ExploreTargetInfo(exploreTargetUid, exploreTarget.Id, objectInfo);
                    var partKey = MapHelper.CreatePartKey(mapId, mapSubId);
                    _objectInstanceDict.AddOrUpdate(partKey, [objectInfo.GetGameObjectKey()],
                        (_, set) =>
                        {
                            set.Add(objectInfo.GetGameObjectKey());
                            return set;
                        });
                    
                    await exploreTargetInfo.Save();
                    using var packet = PacketMaker.G_TO_U_UPDATE_OBJECT(objectInfo);
                    BroadcastPacket(partKey, packet);
                }
            }
        }
        catch (Exception e)
        {
            logManager.WriteErrorLog(e);
        }
        finally
        {
            _mapLock.Release();
        }

        if (!isLogin)
        {
            using var packet = PacketMaker.G_TO_U_ENTER_INSTANCE_SUCCESS(mapId, mapSubId);
            natsClient.Publish(userSubject, packet.ToBytes());
            
            logManager.WriteDebugLog($"{userSubject} {mapId} {mapSubId}");
        }
    }

    private void HandleUpdateInfo(RedisValue message)
    {
        var (key, type, serializedInfo) = MessagePackSerializer.Deserialize<(string, ObjectType, byte[])>(message);

        switch (type)
        {
            case ObjectType.PLAYER:
            {
                var playerInfo = MessagePackSerializer.Deserialize<PlayerInfo>(serializedInfo);
                using var packet = PacketMaker.G_TO_U_PLAYER_INFO(playerInfo);
                BroadcastPacket(key, packet);
                break;
            }
            
            case ObjectType.EXPLORETARGET:
            {
                var exploreTargetInfo = MessagePackSerializer.Deserialize<ExploreTargetInfo>(serializedInfo);
                using var packet =  PacketMaker.G_TO_U_EXPLORE_TARGET_INFO(exploreTargetInfo);
                BroadcastPacket(key, packet);
                break;
            }
            
            case ObjectType.CAMP:
            {
                var campInfo = MessagePackSerializer.Deserialize<CampInfo>(serializedInfo);
                using var packet =  PacketMaker.G_TO_U_CAMP_INFO(campInfo);
                BroadcastPacket(key, packet);
                break;
            }
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
        var (_, objectInfo) = MessagePackSerializer.Deserialize<(string, GameObjectInfo)>(message);
        var objectKey = GameObjectInfo.MakeObjectKey(objectInfo.ObjectType, objectInfo.ObjectId);
        var currentInstanceKey = MapHelper.CreatePartKey(objectInfo.MapId, objectInfo.MapSubId);
        
        await UpdateObjectPositionAsync(currentInstanceKey, objectKey);

        using var packet = PacketMaker.G_TO_U_UPDATE_OBJECT(objectInfo);
        BroadcastPacket(currentInstanceKey, packet);
    }

    private async Task UpdateObjectPositionAsync(string currentInstanceKey, string objectKey)
    {
        await _mapLock.WaitAsync();
        try
        {
            _objectInstanceDict.AddOrUpdate(
                currentInstanceKey,
                [objectKey],
                (_, set) =>
                {
                    set.Add(objectKey);
                    return set;
                }
            );
        }
        finally
        {
            _mapLock.Release();
        }
    }

    private async Task LeaveManageObjectAsync(RedisValue message)
    {
        var (instanceKey, objectKey) = MessagePackSerializer.Deserialize<(string, string)>(message);
        await _mapLock.WaitAsync();
        try
        {
            if (_objectInstanceDict.TryGetValue(instanceKey, out var instanceSet)) instanceSet.Remove(objectKey);
        }
        finally
        {
            _mapLock.Release();
        }
    }

    private void SpawnManageObject(RedisValue message)
    {
        var (userSubject, instanceKeyList, cellsToRemove) =
            MessagePackSerializer.Deserialize<(string, List<string>, List<Cell>)>(message);

        var spawnList = new List<string>();
        foreach (var instancePartKey in instanceKeyList)
        {
            if (_objectInstanceDict.TryGetValue(instancePartKey, out var objectKeys))
            {
                spawnList.AddRange(objectKeys);
            }
        }
        
        if (spawnList.Count <= 0) return;

        using var packet = PacketMaker.G_TO_U_SPAWN(spawnList, []);
        natsClient.Publish(userSubject, packet.ToBytes());
    }

    private async Task DestroyManageObjectAsync(RedisValue message)
    {
        var (instanceKey, objectKey) = MessagePackSerializer.Deserialize<(string, string)>(message);

        await _mapLock.WaitAsync();
        try
        {
            if (_objectInstanceDict.TryGetValue(instanceKey, out var instanceSet)) instanceSet.Remove(objectKey);
        }
        finally
        {
            _mapLock.Release();
        }

        using var packet = PacketMaker.G_TO_U_DESTROY(objectKey);
        BroadcastPacket(instanceKey, packet);
    }

    // ReSharper disable once UnusedMember.Local
    private void UpdatePlayerInfo(RedisValue message)
    {
        var (instanceKey, playerInfo) = MessagePackSerializer.Deserialize<(string, PlayerInfo)>(message);

        using var packet = PacketMaker.G_TO_U_PLAYER_INFO(playerInfo);
        BroadcastPacket(instanceKey, packet);
    }

    private void BroadcastPacket(string instanceKey, IPacket packet)
    {
        if (!_objectInstanceDict.TryGetValue(instanceKey, out var channels))
        {
            return;
        }

        var channelsCopy = channels.ToList();
        foreach (var channel in channelsCopy)
        {
            natsClient.Publish(channel, packet.ToBytes());
        }
    }

    public async Task ShutdownAsync()
    {
        await _mapLock.WaitAsync();
        _mapLock.Release();
    }
}