using System.Collections.Concurrent;
using StackExchange.Redis;
using MessagePack;
using network.common;
using network.helpers;
using network.infrastructure;
using network.managers;
using network.packets;
using System.Diagnostics;

namespace game_server.controllers
{
    public class InstanceController
    {
        private readonly LogManager _logManager;
        private readonly ConcurrentDictionary<string, HashSet<string>> _objectInstanceDict;
        private readonly SemaphoreSlim _mapLock;
        private readonly NatsClient _natsClient;
        private readonly CancellationTokenSource _cts;
        public static string CreateInstanceSubject => MapHelper.GetCreateInstanceSubject(Program.GameServerId);

        public InstanceController(LogManager logManager, NatsClient natsClient, CancellationTokenSource cts)
        {
            _logManager = logManager;
            _natsClient = natsClient;
            _cts = cts;
            _objectInstanceDict = new();
            _mapLock = new(1, 1);
        }

        public void Initialize()
        {
            SubscribeToCreateInstance();
        }

        private void SubscribeToCreateInstance()
        {
            _natsClient.Subscribe(CreateInstanceSubject, async (_, msg) =>
            {
                try
                {
                    await CreateInstance(msg);
                }
                catch (Exception ex)
                {
                    _logManager.WriteErrorLog(ex);
                }
            });
        }

        private async Task CreateInstance(RedisValue message)
        {
            var (userSubject, mapId, mapSubId) = MessagePackSerializer.Deserialize<(string, MapID, long)>(message);
            await _mapLock.WaitAsync();
            try
            {
                switch (mapId)
                {
                    case MapID.LAB_1:
                    case MapID.LIBRARY:
                        var instanceKey = MapHelper.GetInstanceKey(mapId, mapSubId);
                        _objectInstanceDict.GetOrAdd(instanceKey, _ => new());
                        SubscribeToInstanceEvents(mapId, mapSubId);
                        break;

                    default:
                        throw new Exception("Invalid map id");
                }
            }
            finally
            {
                _mapLock.Release();
            }

            using var packet = PacketMaker.G_TO_U_CREATE_INSTANCE_SUCCESS(mapId, mapSubId);
            _natsClient.Publish(userSubject, packet.ToBytes());
        }

        private void SubscribeToInstanceEvents(MapID mapId, long mapSubId)
        {
            var immediateHandlers = new Dictionary<string, Action<RedisValue>>
            {
                { MapHelper.GetSpawnManageSubject(mapId, mapSubId, Program.GameServerId), SpawnManageObject },
                { MapHelper.GetUpdatePlayerSubject(mapId, mapSubId, Program.GameServerId), UpdatePlayerInfo }
            };

            var moveSubject = MapHelper.GetMoveManageSubject(mapId, mapSubId, Program.GameServerId);
            var asyncHandlers = new Dictionary<string, Func<RedisValue, Task>>
            {
                { moveSubject, MoveManageObjectAsync },
                { MapHelper.GetLeaveManageSubject(mapId, mapSubId, Program.GameServerId), LeaveManageObjectAsync },
                { MapHelper.GetDestroyObjectSubject(mapId, mapSubId, Program.GameServerId), DestroyManageObjectAsync }
            };

            foreach (var (subject, handler) in immediateHandlers)
            {
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
            }

            foreach (var (subject, handler) in asyncHandlers)
            {
                _natsClient.Subscribe(subject, async (_, msg) =>
                {
                    try
                    {
                        await handler(msg);
                    }
                    catch (Exception ex)
                    {
                        _logManager.WriteErrorLog(ex);
                    }
                });
            }
        }
        private async Task MoveManageObjectAsync(RedisValue message)
        {
            var (lastPositionKey, objectInfo) = MessagePackSerializer.Deserialize<(string, GameObjectInfo)>(message);
            var objectKey = GameObjectInfo.MakeHashField(ObjectType.PLAYER, objectInfo.ObjectId);
            var lastInstanceKey = MapHelper.ConvertToInstanceKey(lastPositionKey);
            var currentInstanceKey = MapHelper.GetInstanceKey(objectInfo.MapId, objectInfo.MapSubId);

            await UpdateObjectPositionAsync(lastInstanceKey, currentInstanceKey, objectKey);

            using var packet = PacketMaker.G_TO_U_MOVE(objectInfo);
            BroadcastPacket(currentInstanceKey, packet);
        }

        private async Task UpdateObjectPositionAsync(string lastInstanceKey, string currentInstanceKey, string objectKey)
        {
            await _mapLock.WaitAsync();
            try
            {
                if (_objectInstanceDict.TryGetValue(lastInstanceKey, out var lastInstanceSet))
                {
                    lastInstanceSet.Remove(objectKey);
                }

                _objectInstanceDict.AddOrUpdate(
                    currentInstanceKey,
                    new HashSet<string> { objectKey },
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
            (string positionKey, string objectKey) = MessagePackSerializer.Deserialize<(string, string)>(message);
            var instanceKey = MapHelper.ConvertToInstanceKey(positionKey);

            await _mapLock.WaitAsync();
            try
            {
                if (_objectInstanceDict.TryGetValue(instanceKey, out var instanceSet))
                {
                    instanceSet.Remove(objectKey);
                }
            }
            finally
            {
                _mapLock.Release();
            }
        }

        private void SpawnManageObject(RedisValue message)
        {
            (string userSubject, List<string> instanceKeyList) = MessagePackSerializer.Deserialize<(string, List<string>)>(message);

            var spawnList = new List<string>();
            foreach (var instance_key in instanceKeyList)
            {
                if (_objectInstanceDict.TryGetValue(instance_key, out var objects))
                {
                    spawnList.AddRange(objects);
                }
            }

            if (spawnList.Count <= 0)
            {
                return;
            }

            using var packet = PacketMaker.G_TO_U_SPAWN(spawnList);
            _natsClient.Publish(userSubject, packet.ToBytes());
        }

        private async Task DestroyManageObjectAsync(RedisValue message)
        {
            var (instanceKey, objectKey) = MessagePackSerializer.Deserialize<(string, string)>(message);

            await _mapLock.WaitAsync();
            try
            {
                if (_objectInstanceDict.TryGetValue(instanceKey, out var instanceSet))
                {
                    instanceSet.Remove(objectKey);
                }
            }
            finally
            {
                _mapLock.Release();
            }

            using var packet = PacketMaker.G_TO_U_DESTROY(objectKey);
            BroadcastPacket(instanceKey, packet);
        }

        private void UpdatePlayerInfo(RedisValue message)
        {
            (string instanceKey, PlayerInfo playerInfo) = MessagePackSerializer.Deserialize<(string, PlayerInfo)>(message);

            using var packet = PacketMaker.G_TO_U_PLAYER_INFO(playerInfo);
            BroadcastPacket(instanceKey, packet);
        }

        private void BroadcastPacket(string instanceKey, Packet packet)
        {
            if (_objectInstanceDict.TryGetValue(instanceKey, out var channels))
            {
                var channelsCopy = channels.ToList();
                foreach (var channel in channelsCopy)
                {
                    _natsClient.Publish(channel, packet.ToBytes());
                }
            }
        }

        public async Task ShutdownAsync()
        {
            await _mapLock.WaitAsync();
            _mapLock.Release();
        }
    }
}
