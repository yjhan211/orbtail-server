using System.Collections.Concurrent;
using game_server.handlers;
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

    private readonly Dictionary<Type, object> _updateHandlers = new()
    {
        { typeof(PlayerInfo), new UpdateHandler<PlayerInfo>(PacketMaker.G_TO_U_PLAYER_INFO) },
        { typeof(ExploreTargetInfo), new UpdateHandler<ExploreTargetInfo>(PacketMaker.G_TO_U_EXPLORE_TARGET_INFO) },
        { typeof(JobResourceInfo), new UpdateHandler<JobResourceInfo>(PacketMaker.G_TO_U_JOB_RESOURCE_INFO) },
        { typeof(CampInfo), new UpdateHandler<CampInfo>(PacketMaker.G_TO_U_CAMP_INFO) }
    };

    private static string EnterInstanceSubject => SubjectHelper.GetEnterInstanceSubject(Program.GameServerId);

    public void Initialize()
    {
        SubscribeToCreateInstance();
    }

    private void SubscribeToCreateInstance()
    {
        logManager.WriteDebugLog($"EnterInstanceSubject: {EnterInstanceSubject}");
        natsClient.Subscribe(EnterInstanceSubject, (_, msg) => {        
            try
            {
                EnterInstance(msg).GetAwaiter().GetResult();
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
        var (userSubject, mapId, mapSubId, isLogin) = MessagePackSerializer.Deserialize<(string, MapId, long, bool)>(message);
        await _mapLock.WaitAsync();
        try
        {
            var instanceKey = MapHelper.CreatePartKey(mapId, mapSubId);
            if (_objectInstanceDict.TryAdd(instanceKey, []))
            {
                SubscribeToInstanceEvents(mapId, mapSubId);
            }
            _objectInstanceDict[instanceKey].Add(userSubject);

            var playerId = long.Parse(userSubject.Split("_")[1]);
            var exploreTargetList = ExploreTargetData.GetListByMap(mapId);
            foreach (var exploreTarget in exploreTargetList)
            {
                var exploreTargetUid = 900000000 + playerId;
                var objectInfo = new GameObjectInfo
                {
                    ObjectType = ObjectType.EXPLORETARGET,
                    ObjectId = exploreTargetUid,
                    CurrentCell = exploreTarget.Position,
                    TargetCell = exploreTarget.Position,
                    MapId = mapId
                };

                var exploreTargetInfo = new ExploreTargetInfo(exploreTargetUid, 1, objectInfo);
                var partKey = MapHelper.CreatePartKey(mapId, mapSubId);
                _objectInstanceDict.AddOrUpdate(partKey, [objectInfo.GetGameObjectKey()],
                    (_, set) =>
                    {
                        set.Add(objectInfo.GetGameObjectKey());
                        return set;
                    });
                
                await exploreTargetInfo.Save();
                
                using var packet = PacketMaker.G_TO_U_MOVE(objectInfo);
                BroadcastPacket(partKey, packet);            
            }
            
            logManager.WriteDebugLog($"EnterInstance: {instanceKey}");
        }
        finally
        {
            _mapLock.Release();
        }

        if (!isLogin)
        {
            using var packet = PacketMaker.G_TO_U_CREATE_INSTANCE_SUCCESS(mapId, mapSubId);
            natsClient.Publish(userSubject, packet.ToBytes());
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
        var (_, objectInfo) = MessagePackSerializer.Deserialize<(string, GameObjectInfo)>(message);
        var objectKey = GameObjectInfo.MakeObjectKey(ObjectType.PLAYER, objectInfo.ObjectId);
        var currentInstanceKey = MapHelper.CreatePartKey(objectInfo.MapId, objectInfo.MapSubId);

        await UpdateObjectPositionAsync(currentInstanceKey, objectKey);

        using var packet = PacketMaker.G_TO_U_MOVE(objectInfo);
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
            if (_objectInstanceDict.TryGetValue(instancePartKey, out var objectKeys))
                spawnList.AddRange(objectKeys);

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