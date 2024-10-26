using System.Collections.Concurrent;
using StackExchange.Redis;
using MessagePack;
using network.common;
using network.helpers;
using network.infrastructure;
using network.managers;
using network.packets;
using network.interfaces;
using game_server.handlers;

namespace game_server.controllers
{
    public class InstanceMapController
    {
        private readonly LogManager _logManager;
        private readonly ConcurrentDictionary<string, HashSet<string>> _objectInstanceDict;
        private readonly SemaphoreSlim _mapLock;
        private readonly NatsClient _natsClient;
        private readonly CancellationTokenSource _cts;
        public static string EnterInstanceSubject => SubjectHelper.GetEnterInstanceSubject(Program.GameServerId);

        private readonly Dictionary<Type, object> _updateHandlers = new()
        {
            { typeof(PlayerInfo), new UpdateHandler<PlayerInfo>(PacketMaker.G_TO_U_PLAYER_INFO) },
            { typeof(ExploreTargetInfo), new UpdateHandler<ExploreTargetInfo>(PacketMaker.G_TO_U_EXPLORE_TARGET_INFO) },
            { typeof(JobResourceInfo), new UpdateHandler<JobResourceInfo>(PacketMaker.G_TO_U_JOB_RESOURCE_INFO) },
            { typeof(CampInfo), new UpdateHandler<CampInfo>(PacketMaker.G_TO_U_CAMP_INFO) }
        };

        public InstanceMapController(LogManager logManager, NatsClient natsClient, CancellationTokenSource cts)
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
            _natsClient.Subscribe(EnterInstanceSubject, async (_, msg) =>
            {
                try
                {
                    await EnterInstance(msg);
                }
                catch (Exception ex)
                {
                    _logManager.WriteErrorLog(ex);
                }
            });
        }

        private void SubscribeToInstanceEvents(MapID mapId, long mapSubId)
        {
            var immediateHandlers = new Dictionary<string, Action<RedisValue>>
            {
                { SubjectHelper.GetUpdateInfoSubject(mapId, 0, Program.GameServerId), HandleUpdateInfo },
                { SubjectHelper.GetSpawnManageSubject(mapId, mapSubId, Program.GameServerId), SpawnManageObject },
            };

            var moveSubject = SubjectHelper.GetMoveManageSubject(mapId, mapSubId, Program.GameServerId);
            var asyncHandlers = new Dictionary<string, Func<RedisValue, Task>>
            {
                { moveSubject, MoveManageObjectAsync },
                { SubjectHelper.GetLeaveManageSubject(mapId, mapSubId, Program.GameServerId), LeaveManageObjectAsync },
                { SubjectHelper.GetDestroyObjectSubject(mapId, mapSubId, Program.GameServerId), DestroyManageObjectAsync }
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

        private async Task EnterInstance(RedisValue message)
        {
            var (userSubject, mapId, mapSubId) = MessagePackSerializer.Deserialize<(string, MapID, long)>(message);
            await _mapLock.WaitAsync();
            try
            {
                var instanceKey = InstanceMapHelper.CreatePartKey(mapId, mapSubId);
                if (_objectInstanceDict.TryAdd(instanceKey, new()))
                {
                    SubscribeToInstanceEvents(mapId, mapSubId);
                }
                _objectInstanceDict[instanceKey].Add(userSubject);
            }
            finally
            {
                _mapLock.Release();
            }

            using var packet = PacketMaker.G_TO_U_CREATE_INSTANCE_SUCCESS(mapId, mapSubId);
            _natsClient.Publish(userSubject, packet.ToBytes());
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
            var currentInstanceKey = InstanceMapHelper.CreatePartKey(objectInfo.MapId, objectInfo.MapSubId);

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
            (string instanceKey, string objectKey) = MessagePackSerializer.Deserialize<(string, string)>(message);
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
            foreach (var instancePartKey in instanceKeyList)
            {
                if (_objectInstanceDict.TryGetValue(instancePartKey, out var objectKeys))
                {
                    spawnList.AddRange(objectKeys);
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

        private void BroadcastPacket(string instanceKey, IPacket packet)
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
