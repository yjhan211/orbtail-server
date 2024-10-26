using System.Collections.Concurrent;
using StackExchange.Redis;
using MessagePack;
using network.helpers;
using network.common;
using network.infrastructure;
using network.packets;
using network.managers;
using network.interfaces;
using game_server.handlers;

namespace game_server.controllers
{
    public class CommonMapController
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

        private readonly Dictionary<Type, object> _updateHandlers = new()
        {
            { typeof(PlayerInfo), new UpdateHandler<PlayerInfo>(PacketMaker.G_TO_U_PLAYER_INFO) },
            { typeof(ExploreTargetInfo), new UpdateHandler<ExploreTargetInfo>(PacketMaker.G_TO_U_EXPLORE_TARGET_INFO) },
            { typeof(JobResourceInfo), new UpdateHandler<JobResourceInfo>(PacketMaker.G_TO_U_JOB_RESOURCE_INFO) },
            { typeof(CampInfo), new UpdateHandler<CampInfo>(PacketMaker.G_TO_U_CAMP_INFO) }
        };

        public CommonMapController(LogManager logManager, NatsClient natsClient, CancellationTokenSource cts, MapID mapId)
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
                { SubjectHelper.GetUpdateInfoSubject(_mapId, 0, Program.GameServerId), HandleUpdateInfo },
                { SubjectHelper.GetBroadcastUpdateSubject(_mapId, 0, Program.GameServerId), BroadcastUpdateObject },
                { SubjectHelper.GetBroadcastDestroySubject(_mapId, 0, Program.GameServerId), BroadcastDestroyObject }
            };

            _asyncHandlers = new()
            {
                { SubjectHelper.GetMoveManageSubject(_mapId, 0, Program.GameServerId), MoveManageObjectAsync },
                { SubjectHelper.GetLeaveManageSubject(_mapId, 0, Program.GameServerId), LeaveManageObjectAsync },
                { SubjectHelper.GetSpawnManageSubject(_mapId, 0, Program.GameServerId), SpawnManageObjectAsync },
                { SubjectHelper.GetDestroyObjectSubject(_mapId, 0, Program.GameServerId), DestroyManageObjectAsync },
                { SubjectHelper.GetCreateJobResourceSubject(_mapId, 0, Program.GameServerId), CreateJobResourceInfoAsync }
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
            var managePartList = CommonMapHelper.GetManagePartList(Program.GameServerId);
            var managePositionKeyList = new List<string>();

            foreach (var managePart in managePartList)
            {
                var positionList = CommonMapHelper.GetPositionListByMapByPart(_mapId, managePart);
                managePositionKeyList.AddRange(positionList);
                _exploreTargetDict[managePart] = new();
                _jobResourceDict[managePart] = new();
            }

            foreach (var positionKey in managePositionKeyList)
            {
                _objectPositionDict[positionKey] = new();
                _manageCellList.Add(CommonMapHelper.CreateCell(positionKey));
            }
        }

        private static async Task CleanUpMapResource()
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

                        var objecteField = GameObjectInfo.MakeObjectKey(ObjectType.EXPLORETARGET, exploreTargetId);
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
            var genList = CommonMapHelper.GetGenExlporeIdList(_mapId);
            if (genList == null)
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
                    var createPositionKey = CommonMapHelper.CreatePartKey(_mapId, createCell);
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
                        new HashSet<string> { objectInfo.GetGameObjectKey() },
                        (_, set) =>
                        {
                            set.Add(objectInfo.GetGameObjectKey());
                            return set;
                        }
                    );

                    await exploreTargetInfo.Save();

                    var targetServerList = CommonMapHelper.GetBoundServerList(_mapId, objectInfo.CurrentCell);
                    foreach (var target_server in targetServerList)
                    {
                        var subject = SubjectHelper.GetBroadcastUpdateSubject(_mapId, 0, target_server);
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

        private void HandleUpdateInfo(RedisValue message)
        {
            var (positionKey, info) = MessagePackSerializer.Deserialize<(string, IMessagePackObject)>(message);
            var handler = _updateHandlers[info.GetType()];

            using var packet = ((IUpdateHandler<IMessagePackObject>)handler).MakePacket(info);
            BroadcastPacket(positionKey, packet);
        }

        private async Task MoveManageObjectAsync(RedisValue message)
        {
            var (lastPositionKey, objectInfo) = MessagePackSerializer.Deserialize<(string, GameObjectInfo)>(message);
            var objectKey = GameObjectInfo.MakeObjectKey(objectInfo.ObjectType, objectInfo.ObjectId);
            var currentPositionKey = CommonMapHelper.CreatePartKey(objectInfo.MapId, objectInfo.CurrentCell);

            await UpdateObjectPositionAsync(lastPositionKey, currentPositionKey, objectKey);
            BroadcastObjectMove(lastPositionKey, objectInfo);
            BroadcastObjectMove(currentPositionKey, objectInfo);
        }

        private void BroadcastPacket(string positionKey, IPacket packet)
        {
            var pivotCell = CommonMapHelper.CreateCell(positionKey);
            var boundCellList = pivotCell.GetBoundCellList();

            foreach (var boundCell in boundCellList)
            {
                var boundPositionKey = CommonMapHelper.CreatePartKey(_mapId, boundCell);
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
                var subject = SubjectHelper.GetBroadcastDestroySubject(_mapId, 0, targetServer);
                _natsClient.Publish(subject, message);
            }
        }

        private void BroadcastObjectMove(string positionKey, GameObjectInfo objectInfo)
        {
            var targetServerList = CommonMapHelper.GetBoundServerList(_mapId, objectInfo.CurrentCell);
            var message = MessagePackSerializer.Serialize((positionKey, objectInfo));
            foreach (var targetServer in targetServerList)
            {
                var subject = SubjectHelper.GetBroadcastUpdateSubject(_mapId, 0, targetServer);
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
            (_, string objectKey) = MessagePackSerializer.Deserialize<(string, string)>(message);
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
                    set.RemoveWhere(jobResourceInfo => jobResourceInfo.ObjectInfo.GetGameObjectKey() == objectKey);
                }
            }
            finally
            {
                _mapLock.Release();
            }

            var positionCell = CommonMapHelper.CreateCell(positionKey);
            var targetServerList = CommonMapHelper.GetBoundServerList(_mapId, positionCell);

            var destroyMessage = MessagePackSerializer.Serialize((positionKey, objectKey));
            BroadcastObjectDestroy(targetServerList, destroyMessage);

            using var packet = PacketMaker.G_TO_U_DESTROY(objectKey);
            BroadcastPacket(positionKey, packet);
        }

        private async Task CreateJobResourceInfoAsync(RedisValue message)
        {
            (string createPositionKey, JobResourceInfo jobResourceInfo, GameObjectInfo objectInfo)
                = MessagePackSerializer.Deserialize<(string, JobResourceInfo, GameObjectInfo)>(message);

            var partId = CommonMapHelper.GetManagePartByPositionKey(createPositionKey);

            await _mapLock.WaitAsync();
            try
            {
                _jobResourceDict[partId].Add(jobResourceInfo);
                _objectPositionDict.AddOrUpdate(
                    createPositionKey,
                    new HashSet<string> { objectInfo.GetGameObjectKey() },
                    (_, set) =>
                    {
                        set.Add(objectInfo.GetGameObjectKey());
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
