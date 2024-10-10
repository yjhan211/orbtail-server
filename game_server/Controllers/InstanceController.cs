using System.Collections.Concurrent;
using StackExchange.Redis;
using MessagePack;
using network.common;
using network.helpers;
using network.infrastructure;
using network.managers;
using network.packets;

namespace game_server.controllers
{
    public class InstanceController
    {
        private readonly LogManager _logManager;
        private readonly ConcurrentDictionary<string, ConcurrentBag<string>> _objectInstanceDict; // Instance_id, [object_key, ..]
        private readonly SemaphoreSlim _positionLock;
        private readonly NatsClient _natsClient;
        private readonly CancellationTokenSource _cts;

        public static string CreateInstanceSubject => MapHelper.GetCreateInstanceSubject(Program.GameServerId);

        public InstanceController(LogManager logManager, NatsClient natsClient, CancellationTokenSource cts)
        {
            _logManager = logManager;
            _natsClient = natsClient;
            _objectInstanceDict = new();
            _positionLock = new(1, 1);
            _cts = cts;
        }

        public void Initialize()
        {
            SubscribeToCreateInstance();
        }

        private void SubscribeToCreateInstance()
        {
            _natsClient.Subscribe(CreateInstanceSubject, (_, msg) =>
            {
                try
                {
                    CreateInstance(msg);
                }
                catch (Exception ex)
                {
                    _logManager.WriteErrorLog(ex);
                }
            });
        }

        private void CreateInstance(RedisValue message)
        {
            var (userSubject, mapId, mapSubId) = MessagePackSerializer.Deserialize<(string, MapID, long)>(message);
            switch (mapId)
            {
                case MapID.LAB_1:
                    var instance_key = MapHelper.GetInstanceKey(mapId, mapSubId);
                    _objectInstanceDict.GetOrAdd(instance_key, _ => new());
                    SubscribeToInstanceEvents(mapId, mapSubId);
                    break;

                default:
                    throw new Exception("Invalid map id");
            }

            using var packet = PacketMaker.G_TO_U_CREATE_INSTANCE_SUCCESS(mapId, mapSubId);
            _natsClient.Publish(userSubject, packet.ToBytes());
        }

        private void SubscribeToInstanceEvents(MapID mapId, long mapSubId)
        {
            var subscriptions = new Dictionary<string, Func<RedisValue, Task>>
            {
                { MapHelper.GetMoveManageSubject(mapId, mapSubId, Program.GameServerId), MoveManageObjectAsync },
                { MapHelper.GetLeaveManageSubject(mapId, mapSubId, Program.GameServerId), LeaveManageObjectAsync },
                { MapHelper.GetSpawnManageSubject(mapId, mapSubId, Program.GameServerId), msg => Task.Run(() => SpawnManageObject(msg)) },
                { MapHelper.GetDestroyObjectSubject(mapId, mapSubId, Program.GameServerId), DestroyManageObjectAsync },
                { MapHelper.GetUpdatePlayerSubject(mapId, mapSubId, Program.GameServerId), msg => Task.Run(() => UpdatePlayerInfo(msg)) }
            };

            foreach (var (subject, handler) in subscriptions)
            {
                SubscribeWithErrorHandling(subject, handler);
            }
        }

        private void SubscribeWithErrorHandling(string subject, Func<RedisValue, Task> handler)
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

        private async Task MoveManageObjectAsync(RedisValue message)
        {
            var (lastPositionKey, objectInfo) = MessagePackSerializer.Deserialize<(string, GameObjectInfo)>(message);
            var objectKey = GameObjectInfo.MakeHashField(ObjectType.PLAYER, objectInfo.ObjectId);
            var lastInstanceKey = MapHelper.ConvertToInstanceKey(lastPositionKey);
            var currentInstanceKey = MapHelper.GetInstanceKey(objectInfo.MapId, objectInfo.MapSubId);

            await UpdateObjectPositionAsync(lastInstanceKey, currentInstanceKey, objectKey);

            using var packet = PacketMaker.G_TO_U_MOVE(objectInfo);
            BroadcastToInstance(currentInstanceKey, packet);
        }

        private async Task UpdateObjectPositionAsync(string lastInstanceKey, string currentInstanceKey, string objectKey)
        {
            await _positionLock.WaitAsync();
            try
            {
                if (_objectInstanceDict.TryGetValue(lastInstanceKey, out var lastInstanceBag))
                {
                    var updatedBag = new ConcurrentBag<string>(lastInstanceBag.Where(x => x != objectKey));
                    _objectInstanceDict[lastInstanceKey] = updatedBag;
                }

                _objectInstanceDict.AddOrUpdate(currentInstanceKey,
                    new ConcurrentBag<string> { objectKey },
                    (_, bag) =>
                    {
                        bag.Add(objectKey);
                        return bag;
                    }
                );
            }
            finally
            {
                _positionLock.Release();
            }
        }

        private async Task LeaveManageObjectAsync(RedisValue message)
        {
            (string positionKey, string objectKey) = MessagePackSerializer.Deserialize<(string, string)>(message);
            var instanceKey = MapHelper.ConvertToInstanceKey(positionKey);

            await _positionLock.WaitAsync();
            try
            {
                if (_objectInstanceDict.TryGetValue(instanceKey, out var instanceBag))
                {
                    var updatedBag = new ConcurrentBag<string>(instanceBag.Where(x => x != objectKey));
                    _objectInstanceDict[instanceKey] = updatedBag;
                }
            }
            finally
            {
                _positionLock.Release();
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

            await _positionLock.WaitAsync();
            try
            {
                if (_objectInstanceDict.TryGetValue(instanceKey, out var instanceBag))
                {
                    var updatedBag = new ConcurrentBag<string>(instanceBag.Where(x => x != objectKey));
                    _objectInstanceDict[instanceKey] = updatedBag;
                }
            }
            finally
            {
                _positionLock.Release();
            }

            using var packet = PacketMaker.G_TO_U_DESTROY(objectKey);
            BroadcastToInstance(instanceKey, packet);
        }

        private void UpdatePlayerInfo(RedisValue message)
        {
            (string instanceKey, PlayerInfo playerInfo) = MessagePackSerializer.Deserialize<(string, PlayerInfo)>(message);

            using var packet = PacketMaker.G_TO_U_PLAYER_INFO(playerInfo);
            BroadcastToInstance(instanceKey, packet);
        }

        private void BroadcastToInstance(string instanceKey, Packet packet)
        {
            if (_objectInstanceDict.TryGetValue(instanceKey, out var channels))
            {
                foreach (var channel in channels)
                {
                    _natsClient.Publish(channel, packet.ToBytes());
                }
            }
        }

        public async Task ShutdownAsync()
        {
            _cts.Cancel();
            await _positionLock.WaitAsync();
            _positionLock.Release();
            _cts.Dispose();
        }
    }
}
