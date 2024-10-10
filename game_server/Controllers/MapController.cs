using System.Collections.Concurrent;
using StackExchange.Redis;
using MessagePack;
using network.helpers;
using network.common;
using network.infrastructure;
using network.packets;
using network.managers;

namespace game_server.controllers
{
    public class MapController
    {
        private readonly LogManager _logManager;
        private readonly MapID _mapId;
        private readonly NatsClient _natsClient;
        private readonly ConcurrentDictionary<string, ConcurrentBag<string>> _objectPositionDict;
        private readonly ConcurrentDictionary<int, ConcurrentBag<ExploreTargetInfo>> _exploreTargetDict;
        private readonly ConcurrentDictionary<int, ConcurrentBag<JobResourceInfo>> _jobResourceDict;
        private readonly SemaphoreSlim _positionLock;
        private readonly SemaphoreSlim _exploreTargetLock;
        private readonly List<Cell> _manageCellList;
        private readonly CancellationTokenSource _cts;

        public MapController(LogManager logManager, NatsClient natsClient, CancellationTokenSource cts, MapID mapId)
        {
            _logManager = logManager;
            _natsClient = natsClient;
            _cts = cts;
            _mapId = mapId;
            _objectPositionDict = new();
            _exploreTargetDict = new();
            _jobResourceDict = new();
            _manageCellList = new();
            _positionLock = new(1, 1);
            _exploreTargetLock = new(1, 1);
            _cts = new();
        }


        public async Task Initialize()
        {
            InitializeManageParts();
            SubscribeToMapEvents();

            await CleanUpMapResource();
            _ = Task.Run(CreateExploreTargetTask, _cts.Token);
        }

        private void InitializeManageParts()
        {
            var managePartList = MapHelper.GetManagePartList(Program.GameServerNum, Program.GameServerId);
            var managePositionKeyList = new List<string>();

            foreach (var managePart in managePartList)
            {
                managePositionKeyList.AddRange(MapHelper.position_list_by_map_part[this._mapId][managePart]);
                _exploreTargetDict[managePart] = new();
                _jobResourceDict[managePart] = new();
            }

            foreach (var position_key in managePositionKeyList)
            {
                _objectPositionDict[position_key] = new();
                _manageCellList.Add(MapHelper.GetCell(position_key));
            }
        }

        public static async Task CleanUpMapResource()
        {
            if (Program.GameServerId == 1)
            {
                var exploreTargetValues = await CacheHelper.Instance.HashGetAllAsync(ExploreTargetInfo.HASH_KEY);
                if (exploreTargetValues != null)
                {
                    foreach (HashEntry entry in exploreTargetValues)
                    {
                        if (!long.TryParse(entry.Name, out long exploreTargetId))
                        {
                            continue;
                        }

                        await ExploreTargetInfo.Delete(exploreTargetId);

                        var objecteField = GameObjectInfo.MakeHashField(ObjectType.EXPLORETARGET, exploreTargetId);
                        await GameObjectInfo.Delete(objecteField);
                    }
                }
            }
            else
            {
                await Task.Delay(10000);
            }
        }

        private async Task CreateExploreTargetTask()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(this._cts.Token))
            {
                try
                {
                    if (!await CreateExploreTarget())
                    {
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _logManager.WriteErrorLog(ex);
                }
            }
        }

        private async Task<bool> CreateExploreTarget()
        {
            if (!MapHelper.GenExploreIdList.TryGetValue(_mapId, out var genList))
            {
                return false;
            }

            await _exploreTargetLock.WaitAsync();
            try
            {
                Random random = new();
                foreach (var partExploreTargetInfo in _exploreTargetDict)
                {
                    // TODO Config로 분리
                    if (3 <= partExploreTargetInfo.Value.Count)
                    {
                        continue;
                    }

                    var createCell = _manageCellList[random.Next(0, _manageCellList.Count)];
                    var createPositionKey = MapHelper.GetPositionKey(_mapId, 0, createCell);
                    var exploreTargetUid = await CacheHelper.Instance.StringIncrementAsync("temp_explore_target_uid");

                    var objectInfo = new GameObjectInfo()
                    {
                        ObjectType = ObjectType.EXPLORETARGET,
                        ObjectId = exploreTargetUid,
                        CurrentCell = createCell,
                        TargetCell = createCell,
                        MapId = _mapId,
                    };

                    var exploreTargetInfo = new ExploreTargetInfo(exploreTargetUid, genList[random.Next(0, genList.Count)], objectInfo);
                    partExploreTargetInfo.Value.Add(exploreTargetInfo);

                    _objectPositionDict.AddOrUpdate(
                        createPositionKey,
                        new ConcurrentBag<string> { objectInfo.GetHashField() },
                        (_, bag) =>
                        {
                            bag.Add(objectInfo.GetHashField());
                            return bag;
                        }
                    );

                    await exploreTargetInfo.Save();

                    // bound_cell이 포함된 서버에는 브로드캐스트 명령을 보냄
                    var targetServerList = MapHelper.GetBoundServerList(_mapId, Program.GameServerNum, objectInfo.CurrentCell);
                    foreach (var target_server in targetServerList)
                    {
                        var subject = MapHelper.GetBrodcastMoveSubject(_mapId, 0, target_server);
                        _natsClient.Publish(subject, MessagePackSerializer.Serialize((createPositionKey, objectInfo)));
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logManager.WriteErrorLog(ex);
                return false;
            }
            finally
            {
                _exploreTargetLock.Release();
            }
        }

        private void SubscribeToMapEvents()
        {
            var subscriptions = new Dictionary<string, Func<RedisValue, Task>>
            {
                { MapHelper.GetMoveManageSubject(_mapId, 0, Program.GameServerId), MoveManageObjectAsync },
                { MapHelper.GetLeaveManageSubject(_mapId, 0, Program.GameServerId), LeaveManageObjectAsync },
                { MapHelper.GetSpawnManageSubject(_mapId, 0, Program.GameServerId), (msg) => Task.Run(() => SpawnManageObject(msg)) },
                { MapHelper.GetDestroyObjectSubject(_mapId, 0, Program.GameServerId), DestroyManageObjectAsync },
                { MapHelper.GetUpdatePlayerSubject(_mapId, 0, Program.GameServerId), UpdatePlayerInfoAsync },
                { MapHelper.GetUpdateExploreTargetSubject(_mapId, 0, Program.GameServerId), UpdateExploreTargetInfoAsync },
                { MapHelper.GetCreateJobResourceSubject(_mapId, 0, Program.GameServerId), CreateJobResourceInfoAsync },
                { MapHelper.GetUpdateJobResourceSubject(_mapId, 0, Program.GameServerId), BroadcastJobResourceInfoAsync },
                { MapHelper.GetUpdateCampSubject(_mapId, 0, Program.GameServerId), BroadcastCampInfoAsync },
                { MapHelper.GetBrodcastMoveSubject(_mapId, 0, Program.GameServerId), BroadcastUpdateObjectAsync },
                { MapHelper.GetBrodcastDestroySubject(_mapId, 0, Program.GameServerId), BroadcastDestroyObjectAsync }
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
            var object_key = GameObjectInfo.MakeHashField(objectInfo.ObjectType, objectInfo.ObjectId);
            var current_position_key = MapHelper.GetPositionKey(objectInfo.MapId, 0, objectInfo.CurrentCell);

            await UpdateObjectPositionAsync(lastPositionKey, current_position_key, object_key);
            await BroadcastObjectMoveAsync(current_position_key, objectInfo);
        }

        private Task BroadcastToPositionAsync(string positionKey, Packet packet)
        {
            return Task.Run(() =>
            {
                var pivotCell = MapHelper.GetCell(positionKey);
                var boundCellList = MapHelper.GetBoundCellList(pivotCell);

                foreach (var boundCell in boundCellList)
                {
                    var boundPositionKey = MapHelper.GetPositionKey(_mapId, 0, boundCell);
                    if (_objectPositionDict.TryGetValue(boundPositionKey, out var channelList) && !channelList.IsEmpty)
                    {
                        foreach (var channel in channelList)
                        {
                            _natsClient.Publish(channel, packet.ToBytes());
                        }
                    }
                }
            });
        }

        private Task BroadcastToServersAsync(List<int> targetServerList, string subject, byte[] message)
        {
            return Task.Run(() =>
            {
                foreach (var targetServer in targetServerList)
                {
                    var fullSubject = MapHelper.GetBrodcastMoveSubject(_mapId, 0, targetServer);
                    _natsClient.Publish(fullSubject, message);
                }
            });
        }


        private Task BroadcastObjectMoveAsync(string positionKey, GameObjectInfo objectInfo)
        {
            return Task.Run(() =>
            {
                var targetServerList = MapHelper.GetBoundServerList(_mapId, Program.GameServerNum, objectInfo.CurrentCell);
                var message = MessagePackSerializer.Serialize((positionKey, objectInfo));
                foreach (var targetServer in targetServerList)
                {
                    var subject = MapHelper.GetBrodcastMoveSubject(_mapId, 0, targetServer);
                    _natsClient.Publish(subject, message);
                }
            });
        }

        private async Task UpdateObjectPositionAsync(string lastPositionKey, string currentPositionKey, string objectKey)
        {
            await _positionLock.WaitAsync();
            try
            {
                if (_objectPositionDict.TryGetValue(lastPositionKey, out var lastPositionBag))
                {
                    var updatedBag = new ConcurrentBag<string>(lastPositionBag.Where(x => x != objectKey));
                    _objectPositionDict[lastPositionKey] = updatedBag;
                }

                _objectPositionDict.AddOrUpdate(
                    currentPositionKey,
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
            await _positionLock.WaitAsync();
            try
            {
                (string position_key, string object_key) = MessagePackSerializer.Deserialize<(string, string)>(message);
                foreach (var kvp in _objectPositionDict)
                {
                    var updatedBag = new ConcurrentBag<string>(kvp.Value.Where(x => x != object_key));
                    _objectPositionDict[kvp.Key] = updatedBag;
                }
            }
            finally
            {
                _positionLock.Release();
            }
        }

        private void SpawnManageObject(RedisValue message)
        {
            (string userSubject, List<string> positionKeyList) = MessagePackSerializer.Deserialize<(string, List<string>)>(message);

            var spawnList = new List<string>();
            foreach (var positionKey in positionKeyList)
            {
                if (_objectPositionDict.TryGetValue(positionKey, out var objects))
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
            await _positionLock.WaitAsync();
            try
            {
                var (positionKey, objectKey) = MessagePackSerializer.Deserialize<(string, string)>(message);
                foreach (var kvp in _objectPositionDict)
                {
                    var updatedBag = new ConcurrentBag<string>(kvp.Value.Where(x => x != objectKey));
                    _objectPositionDict[kvp.Key] = updatedBag;
                }

                foreach (var kvp in _jobResourceDict)
                {
                    var updatedBag = new ConcurrentBag<JobResourceInfo>(
                        kvp.Value.Where(jobResourceInfo => jobResourceInfo.ObjectInfo.GetHashField() != objectKey)
                    );
                    _jobResourceDict[kvp.Key] = updatedBag;
                }

                var positionCell = MapHelper.GetCell(positionKey);
                var targetServerList = MapHelper.GetBoundServerList(_mapId, Program.GameServerNum, positionCell);

                var destroyMessage = MessagePackSerializer.Serialize((positionKey, objectKey));
                await BroadcastToServersAsync(targetServerList, MapHelper.GetBrodcastDestroySubject(_mapId, 0, 0), destroyMessage);

                using var packet = PacketMaker.G_TO_U_DESTROY(objectKey);
                await BroadcastToPositionAsync(positionKey, packet);
            }
            finally
            {
                _positionLock.Release();
            }
        }

        private async Task UpdatePlayerInfoAsync(RedisValue message)
        {
            (string positionKey, PlayerInfo playerInfo) = MessagePackSerializer.Deserialize<(string, PlayerInfo)>(message);

            using var packet = PacketMaker.G_TO_U_PLAYER_INFO(playerInfo);
            await BroadcastToPositionAsync(positionKey, packet);
        }

        private async Task UpdateExploreTargetInfoAsync(RedisValue message)
        {
            (string positionKey, ExploreTargetInfo exploreTargetInfo) = MessagePackSerializer.Deserialize<(string, ExploreTargetInfo)>(message);

            using var packet = PacketMaker.G_TO_U_EXPLORE_TARGET_INFO(exploreTargetInfo);
            await BroadcastToPositionAsync(positionKey, packet);
        }

        private async Task CreateJobResourceInfoAsync(RedisValue message)
        {
            (string createPositionKey, JobResourceInfo jobResourceInfo, GameObjectInfo objectInfo)
                = MessagePackSerializer.Deserialize<(string, JobResourceInfo, GameObjectInfo)>(message);

            var partId = MapHelper.GetManagePartByPositionKey(Program.GameServerNum, createPositionKey);

            await _positionLock.WaitAsync();
            try
            {
                _jobResourceDict[partId].Add(jobResourceInfo);
                _objectPositionDict.AddOrUpdate(createPositionKey,
                    new ConcurrentBag<string> { objectInfo.GetHashField() },
                    (_, bag) =>
                    {
                        bag.Add(objectInfo.GetHashField());
                        return bag;
                    }
                );
            }
            finally
            {

                _positionLock.Release();
            }

            await BroadcastObjectMoveAsync(createPositionKey, objectInfo);
        }

        private async Task BroadcastJobResourceInfoAsync(RedisValue message)
        {
            (string positionKey, JobResourceInfo jobResourceInfo) = MessagePackSerializer.Deserialize<(string, JobResourceInfo)>(message);

            using var packet = PacketMaker.G_TO_U_JOB_RESOURCE_INFO(jobResourceInfo);
            await BroadcastToPositionAsync(positionKey, packet);
        }

        private async Task BroadcastCampInfoAsync(RedisValue message)
        {
            (string positionKey, CampInfo campInfo) = MessagePackSerializer.Deserialize<(string, CampInfo)>(message);

            using var packet = PacketMaker.G_TO_U_CAMP_INFO(campInfo);
            await BroadcastToPositionAsync(positionKey, packet);
        }

        private async Task BroadcastUpdateObjectAsync(RedisValue message)
        {
            var (positionKey, objectInfo) = MessagePackSerializer.Deserialize<(string, GameObjectInfo)>(message);

            using var packet = PacketMaker.G_TO_U_MOVE(objectInfo);
            await BroadcastToPositionAsync(positionKey, packet);
        }

        private async Task BroadcastDestroyObjectAsync(RedisValue message)
        {
            var (positionKey, objectKey) = MessagePackSerializer.Deserialize<(string, string)>(message);

            using var packet = PacketMaker.G_TO_U_DESTROY(objectKey);
            await BroadcastToPositionAsync(positionKey, packet);
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
