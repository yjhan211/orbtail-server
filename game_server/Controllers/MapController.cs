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
        private readonly ConcurrentDictionary<string, HashSet<string>> _objectPositionDict;
        private readonly ConcurrentDictionary<int, HashSet<ExploreTargetInfo>> _exploreTargetDict;
        private readonly ConcurrentDictionary<int, HashSet<JobResourceInfo>> _jobResourceDict;
        private readonly List<Cell> _manageCellList;
        private readonly CancellationTokenSource _cts;
        private readonly SemaphoreSlim _mapLock;
        private Dictionary<string, Action<RedisValue>> _immediateHandlers;
        private Dictionary<string, Func<RedisValue, Task>> _asyncHandlers;

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
            _mapLock = new(1, 1);

            _immediateHandlers = new()
            {
                { MapHelper.GetUpdatePlayerSubject(_mapId, 0, Program.GameServerId), UpdatePlayerInfo },
                { MapHelper.GetUpdateExploreTargetSubject(_mapId, 0, Program.GameServerId), UpdateExploreTargetInfo },
                { MapHelper.GetUpdateJobResourceSubject(_mapId, 0, Program.GameServerId), BroadcastJobResourceInfo },
                { MapHelper.GetUpdateCampSubject(_mapId, 0, Program.GameServerId), BroadcastCampInfo },
                { MapHelper.GetBrodcastMoveSubject(_mapId, 0, Program.GameServerId), BroadcastUpdateObject },
                { MapHelper.GetBrodcastDestroySubject(_mapId, 0, Program.GameServerId), BroadcastDestroyObject }
            };

            _asyncHandlers = new()
            {
                { MapHelper.GetMoveManageSubject(_mapId, 0, Program.GameServerId), MoveManageObjectAsync },
                { MapHelper.GetLeaveManageSubject(_mapId, 0, Program.GameServerId), LeaveManageObjectAsync },
                { MapHelper.GetSpawnManageSubject(_mapId, 0, Program.GameServerId), SpawnManageObjectAsync },
                { MapHelper.GetDestroyObjectSubject(_mapId, 0, Program.GameServerId), DestroyManageObjectAsync },
                { MapHelper.GetCreateJobResourceSubject(_mapId, 0, Program.GameServerId), CreateJobResourceInfoAsync }
            };
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

            await _mapLock.WaitAsync();
            try
            {
                Random random = new();
                foreach (var partExploreTargetInfo in _exploreTargetDict)
                {
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
                        new HashSet<string> { objectInfo.GetHashField() },
                        (_, set) =>
                        {
                            set.Add(objectInfo.GetHashField());
                            return set;
                        }
                    );

                    await exploreTargetInfo.Save();

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
                _mapLock.Release();
            }
        }

        private void SubscribeToMapEvents()
        {
            foreach (var (subject, handler) in _immediateHandlers)
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

            foreach (var (subject, handler) in _asyncHandlers)
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
            var objectKey = GameObjectInfo.MakeHashField(objectInfo.ObjectType, objectInfo.ObjectId);
            var currentPositionKey = MapHelper.GetPositionKey(objectInfo.MapId, 0, objectInfo.CurrentCell);

            await UpdateObjectPositionAsync(lastPositionKey, currentPositionKey, objectKey);
            BroadcastObjectMove(lastPositionKey, objectInfo);
            BroadcastObjectMove(currentPositionKey, objectInfo);
        }

        private void BroadcastPacket(string positionKey, Packet packet)
        {
            var pivotCell = MapHelper.GetCell(positionKey);
            var boundCellList = MapHelper.GetBoundCellList(pivotCell);

            foreach (var boundCell in boundCellList)
            {
                var boundPositionKey = MapHelper.GetPositionKey(_mapId, 0, boundCell);
                if (_objectPositionDict.TryGetValue(boundPositionKey, out var channelSet) && channelSet.Count > 0)
                {
                    foreach (var channel in channelSet)
                    {
                        _natsClient.Publish(channel, packet.ToBytes());
                    }
                }
            }
        }
        private void BroadcastObjectDestroy(List<int> targetServerList, byte[] message)
        {
            foreach (var targetServer in targetServerList)
            {
                var subject = MapHelper.GetBrodcastDestroySubject(_mapId, 0, targetServer);
                _natsClient.Publish(subject, message);
            }
        }

        private void BroadcastObjectMove(string positionKey, GameObjectInfo objectInfo)
        {
            var targetServerList = MapHelper.GetBoundServerList(_mapId, Program.GameServerNum, objectInfo.CurrentCell);
            var message = MessagePackSerializer.Serialize((positionKey, objectInfo));
            foreach (var targetServer in targetServerList)
            {
                var subject = MapHelper.GetBrodcastMoveSubject(_mapId, 0, targetServer);
                _natsClient.Publish(subject, message);
            }
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

                _objectPositionDict.AddOrUpdate(
                    currentPositionKey,
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
            await _mapLock.WaitAsync();
            try
            {
                foreach (var set in _objectPositionDict.Values)
                {
                    set.Remove(objectKey);
                }
            }
            finally
            {
                _mapLock.Release();
            }
        }

        private async Task SpawnManageObjectAsync(RedisValue message)
        {
            (string userSubject, List<string> positionKeyList) = MessagePackSerializer.Deserialize<(string, List<string>)>(message);

            await _mapLock.WaitAsync();
            try
            {
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
            finally
            {
                _mapLock.Release();
            }
        }

        private async Task DestroyManageObjectAsync(RedisValue message)
        {
            var (positionKey, objectKey) = MessagePackSerializer.Deserialize<(string, string)>(message);
            await _mapLock.WaitAsync();
            try
            {
                bool removeSuccess = false;
                foreach (var set in _objectPositionDict.Values)
                {
                    set.Remove(objectKey);
                    removeSuccess = true;
                }

                if (!removeSuccess)
                {
                    _logManager.WriteDebugLog($"remove FAIL {objectKey}");
                }

                foreach (var set in _jobResourceDict.Values)
                {
                    set.RemoveWhere(jobResourceInfo => jobResourceInfo.ObjectInfo.GetHashField() == objectKey);
                }
            }
            finally
            {
                _mapLock.Release();
            }

            var positionCell = MapHelper.GetCell(positionKey);
            var targetServerList = MapHelper.GetBoundServerList(_mapId, Program.GameServerNum, positionCell);

            var destroyMessage = MessagePackSerializer.Serialize((positionKey, objectKey));
            BroadcastObjectDestroy(targetServerList, destroyMessage);

            using var packet = PacketMaker.G_TO_U_DESTROY(objectKey);
            BroadcastPacket(positionKey, packet);
        }

        private void UpdatePlayerInfo(RedisValue message)
        {
            (string positionKey, PlayerInfo playerInfo) = MessagePackSerializer.Deserialize<(string, PlayerInfo)>(message);

            using var packet = PacketMaker.G_TO_U_PLAYER_INFO(playerInfo);
            BroadcastPacket(positionKey, packet);
        }

        private void UpdateExploreTargetInfo(RedisValue message)
        {
            (string positionKey, ExploreTargetInfo exploreTargetInfo) = MessagePackSerializer.Deserialize<(string, ExploreTargetInfo)>(message);

            using var packet = PacketMaker.G_TO_U_EXPLORE_TARGET_INFO(exploreTargetInfo);
            BroadcastPacket(positionKey, packet);
        }

        private async Task CreateJobResourceInfoAsync(RedisValue message)
        {
            (string createPositionKey, JobResourceInfo jobResourceInfo, GameObjectInfo objectInfo)
                = MessagePackSerializer.Deserialize<(string, JobResourceInfo, GameObjectInfo)>(message);

            var partId = MapHelper.GetManagePartByPositionKey(Program.GameServerNum, createPositionKey);

            await _mapLock.WaitAsync();
            try
            {
                _jobResourceDict[partId].Add(jobResourceInfo);
                _objectPositionDict.AddOrUpdate(
                    createPositionKey,
                    new HashSet<string> { objectInfo.GetHashField() },
                    (_, set) =>
                    {
                        set.Add(objectInfo.GetHashField());
                        return set;
                    }
                );
            }
            finally
            {
                _mapLock.Release();
            }

            BroadcastObjectMove(createPositionKey, objectInfo);
        }

        private void BroadcastJobResourceInfo(RedisValue message)
        {
            (string positionKey, JobResourceInfo jobResourceInfo) = MessagePackSerializer.Deserialize<(string, JobResourceInfo)>(message);

            using var packet = PacketMaker.G_TO_U_JOB_RESOURCE_INFO(jobResourceInfo);
            BroadcastPacket(positionKey, packet);
        }

        private void BroadcastCampInfo(RedisValue message)
        {
            (string positionKey, CampInfo campInfo) = MessagePackSerializer.Deserialize<(string, CampInfo)>(message);

            using var packet = PacketMaker.G_TO_U_CAMP_INFO(campInfo);
            BroadcastPacket(positionKey, packet);
        }

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
}
