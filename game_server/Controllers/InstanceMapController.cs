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

public class InstanceMapController(LogManager logManager, NatsClient natsClient, CancellationTokenSource cts)
{
    // ReSharper disable once UnusedMember.Local
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
        natsClient.Subscribe(EnterInstanceSubject, MessageHandler);
        return;

        async void MessageHandler(string _, byte[] msg)
        {
            try
            {
                await EnterInstance(msg);
            }
            catch (Exception ex)
            {
                logManager.WriteErrorLog(ex);
            }
        }
    }

    private void SubscribeToInstanceEvents(MapId mapId, long mapSubId)
    {
        var immediateHandlers = new Dictionary<string, Action<RedisValue>>
        {
            { SubjectHelper.GetUpdateInfoSubject(mapId, 0, Program.GameServerId), HandleUpdateInfo },
            { SubjectHelper.GetSpawnManageSubject(mapId, mapSubId, Program.GameServerId), SpawnManageObject }
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
            natsClient.Subscribe(subject, MessageHandler);
            continue;

            async void MessageHandler(string _, byte[] msg)
            {
                try
                {
                    await handler(msg);
                }
                catch (Exception ex)
                {
                    logManager.WriteErrorLog(ex);
                }
            }
        }
    }

    private async Task EnterInstance(RedisValue message)
    {
        var (userSubject, mapId, mapSubId) = MessagePackSerializer.Deserialize<(string, MapId, long)>(message);
        await _mapLock.WaitAsync();
        try
        {
            var instanceKey = MapHelper.CreatePartKey(mapId, mapSubId);
            if (_objectInstanceDict.TryAdd(instanceKey, []))
                SubscribeToInstanceEvents(mapId, mapSubId);
            _objectInstanceDict[instanceKey].Add(userSubject);
        }
        finally
        {
            _mapLock.Release();
        }

        using var packet = PacketMaker.G_TO_U_CREATE_INSTANCE_SUCCESS(mapId, mapSubId);
        natsClient.Publish(userSubject, packet.ToBytes());
    }

    private void HandleUpdateInfo(RedisValue message)
    {
        var (positionKey, info) = MessagePackSerializer.Deserialize<(string, IMessagePackObject)>(message);
        var handler = _updateHandlers[info.GetType()];

        using var packet = ((IUpdateHandler<IMessagePackObject>)handler).MakePacket(info);
        BroadcastPacket(positionKey, packet);
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
        if (_objectInstanceDict.TryGetValue(instanceKey, out var channels))
        {
            var channelsCopy = channels.ToList();
            foreach (var channel in channelsCopy) natsClient.Publish(channel, packet.ToBytes());
        }
    }

    public async Task ShutdownAsync()
    {
        await _mapLock.WaitAsync();
        _mapLock.Release();
    }
}