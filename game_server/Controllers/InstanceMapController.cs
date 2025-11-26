using System.Collections.Concurrent;
using game_server.network;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.config;
using network.helpers;
using network.interfaces;
using network.packets;

namespace game_server.controllers;

public class InstanceMapController : BaseMapController
{
    private readonly Dictionary<Protocol, Func<long, byte[], Task>> _protocolHandlers;
    private readonly ConcurrentDictionary<string, HashSet<string>> _objectInstanceDict = new();
    private readonly ConcurrentDictionary<string, Timer> _damageTimers = new();
    private readonly ConcurrentDictionary<string, Timer> _gameTimers = new();
    private readonly ConcurrentDictionary<string, (Timer OneMinuteTimer, Timer ThirtySecondsTimer)> _warningTimers = new();
    private readonly ConcurrentDictionary<long, GameClientSession> _clientSessions;

    private const int GameDurationMinutes = 15;
    private const int GameDurationSeconds = GameDurationMinutes * 60;

    private string EnterInstanceSubject =>
        SubjectHelper.GetEnterInstanceSubject(ServerConfig.ServerId);

    public InstanceMapController(
        ILogger logger,
        INatsClient natsClient,
        CancellationTokenSource cts,
        ICacheHelper cacheHelper,
        ServerConfig serverConfig,
        ConcurrentDictionary<long, GameClientSession> clientSessions)
        : base(logger, natsClient, cts, cacheHelper, serverConfig)
    {
        _clientSessions = clientSessions;
        _protocolHandlers = new Dictionary<Protocol, Func<long, byte[], Task>>
       {
           { Protocol.U_TO_G_LOGOUT, HandleLogout }
       };
    }

    public void Initialize()
    {
        SubscribeWithHandler(EnterInstanceSubject, EnterInstance);
    }

    private void SubscribeToInstanceEvents(MapId mapId, long mapSubId)
    {
        var subjects = new Dictionary<string, Func<byte[], Task>>
       {
           { SubjectHelper.GetUpdateInfoSubject(mapId, mapSubId, ServerConfig.ServerId), HandleUpdateInfo },
           { SubjectHelper.GetSocialActionSubject(mapId, mapSubId, ServerConfig.ServerId), HandleSocialAction },
           { SubjectHelper.GetTakeDamageSubject(mapId, mapSubId, ServerConfig.ServerId), HandleTakeDamage },
           { SubjectHelper.GetSpawnManageSubject(mapId, mapSubId, ServerConfig.ServerId), SpawnManageObject },
           { SubjectHelper.GetUpdateManageSubject(mapId, mapSubId, ServerConfig.ServerId), MoveManageObjectAsync },
           { SubjectHelper.GetLeaveManageSubject(mapId, mapSubId, ServerConfig.ServerId), LeaveManageObjectAsync },
           { SubjectHelper.GetDestroyObjectSubject(mapId, mapSubId, ServerConfig.ServerId), DestroyManageObjectAsync }
       };

        foreach (var (subject, handler) in subjects)
        {
            SubscribeWithHandler(subject, handler);
        }
    }

    private async Task EnterInstance(byte[] message)
    {
        var (objectKey, mapId, mapSubId, isLogin) = MessagePackSerializer.Deserialize<(string, MapId, long, bool)>(message);
        var instanceKey = MapHelper.CreatePartKey(mapId, mapSubId);

        Logger.LogInformation($"EnterInstance 요청 수신: objectKey={objectKey}, mapId={mapId}, mapSubId={mapSubId}, isLogin={isLogin}");

        var success = false;
        await MapLock.WaitAsync();
        try
        {
            var isInit = _objectInstanceDict.TryAdd(instanceKey, []);
            _objectInstanceDict[instanceKey].Add(objectKey);

            Logger.LogInformation($"인스턴스 {instanceKey} 초기화 여부: {isInit}, 현재 인원: {_objectInstanceDict[instanceKey].Count}");

            if (isInit)
            {
                SubscribeToInstanceEvents(mapId, mapSubId);
                switch (mapId)
                {
                    case MapId.Camp:
                        break;

                    case MapId.School:
                        // 매칭 맵: 15분 게임 타이머 시작
                        StartGameTimer(instanceKey, mapId, mapSubId);
                        break;

                    default:
                        break;
                }
                // await InitializeExploreTargets(mapId, mapSubId);
                Logger.LogInformation($"인스턴스 {instanceKey} 초기화 완료");
            }

            success = true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, $"인스턴스 진입 실패: objectKey={objectKey}, instanceKey={instanceKey}");

            // 실패 시 인스턴스에서 제거
            if (_objectInstanceDict.TryGetValue(instanceKey, out var instanceSet))
            {
                instanceSet.Remove(objectKey);
            }
        }
        finally
        {
            MapLock.Release();
        }

        // 응답 전송
        if (!isLogin)
        {
            if (success)
            {
                Logger.LogInformation($"G_TO_U_ENTER_INSTANCE_SUCCESS 전송: objectKey={objectKey}, mapId={mapId}, mapSubId={mapSubId}");
                using var packet = PacketMaker.G_TO_U_ENTER_INSTANCE_SUCCESS(mapId, mapSubId);

                // TCP로 직접 전송
                if (TryExtractPlayerId(objectKey, out var playerId))
                {
                    if (_clientSessions.TryGetValue(playerId, out var session))
                    {
                        session.Send(packet);
                    }
                    else
                    {
                        // 폴백: NATS
                        NatsClient.Publish(objectKey, packet.ToBytes());
                    }
                }
            }
            else
            {
                Logger.LogError($"인스턴스 진입 실패 - 에러 응답 전송: objectKey={objectKey}");
                // TODO: 실패 응답 패킷 정의 필요
            }
        }
        else
        {
            Logger.LogInformation($"isLogin=true이므로 G_TO_U_ENTER_INSTANCE_SUCCESS 전송 생략");
        }
    }

    private async Task InitializeExploreTargets(MapId mapId, long mapSubId)
    {
        var exploreTargetList = GameExploreTargetData.GetListByMap(mapId);
        foreach (var exploreTarget in exploreTargetList)
        {
            var exploreTargetUid = await CacheHelper.StringIncrementAsync("temp_explore_target_uid");
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

            await exploreTargetInfo.Save(CacheHelper);
            using var packet = PacketMaker.G_TO_U_UPDATE_OBJECT(objectInfo);
            BroadcastPacket(partKey, packet);
        }
    }

    private void StartEnvironmentNotificationTimer(string instanceKey, MapId mapId, long mapSubId)
    {
        var timer = new Timer(state => SendEnvironmentNotification(instanceKey, mapId, mapSubId), null,
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        _damageTimers[instanceKey] = timer;
        Logger.LogInformation($"School1 맵 데미지 알림 타이머 시작: {instanceKey}");
    }

    private void SendEnvironmentNotification(string instanceKey, MapId mapId, long mapSubId)
    {
        try
        {
            if (!_objectInstanceDict.TryGetValue(instanceKey, out var userKeys) || userKeys.Count == 0)
            {
                return;
            }

            using var packet = PacketMaker.G_TO_C_ENVIRONMENT(DamageType.DARK);
            BroadcastPacket(instanceKey, packet);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, $"School1 맵 데미지 알림 전송 중 오류 발생: {instanceKey}");
        }
    }

    private void StartGameTimer(string instanceKey, MapId mapId, long mapSubId)
    {
        Logger.LogInformation($"게임 타이머 시작: {instanceKey} (15분)");

        // 15분 후 게임 종료 타이머
        var gameTimer = new Timer(state =>
        {
            EndGame(instanceKey, mapId, mapSubId);

            // 타이머 정리
            if (_gameTimers.TryRemove(instanceKey, out var t))
            {
                t.Dispose();
            }

            if (_warningTimers.TryRemove(instanceKey, out var warningTimers))
            {
                warningTimers.OneMinuteTimer?.Dispose();
                warningTimers.ThirtySecondsTimer?.Dispose();
            }
        }, null, TimeSpan.FromSeconds(GameDurationSeconds), Timeout.InfiniteTimeSpan);

        _gameTimers[instanceKey] = gameTimer;

        // 1분 남았을 때 알림 (14분 후)
        var oneMinuteWarningTimer = new Timer(state =>
        {
            SendGameTimeWarning(instanceKey, 60);
        }, null, TimeSpan.FromSeconds(GameDurationSeconds - 60), Timeout.InfiniteTimeSpan);

        // 30초 남았을 때 알림 (14분 30초 후)
        var thirtySecondsWarningTimer = new Timer(state =>
        {
            SendGameTimeWarning(instanceKey, 30);
        }, null, TimeSpan.FromSeconds(GameDurationSeconds - 30), Timeout.InfiniteTimeSpan);

        _warningTimers[instanceKey] = (oneMinuteWarningTimer, thirtySecondsWarningTimer);

        Logger.LogInformation($"게임 타이머 및 알림 타이머 설정 완료: {instanceKey}");
    }

    private void SendGameTimeWarning(string instanceKey, int remainingSeconds)
    {
        try
        {
            if (!_objectInstanceDict.TryGetValue(instanceKey, out var userKeys) || userKeys.Count == 0)
            {
                return;
            }

            Logger.LogInformation($"게임 시간 알림 전송: {instanceKey}, 남은 시간: {remainingSeconds}초");

            using var packet = PacketMaker.G_TO_C_GAME_TIME_WARNING(remainingSeconds);
            BroadcastPacketDirect(instanceKey, packet);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, $"게임 시간 알림 전송 중 오류 발생: {instanceKey}");
        }
    }

    private async void EndGame(string instanceKey, MapId mapId, long mapSubId)
    {
        try
        {
            Logger.LogInformation($"게임 종료 처리 시작: {instanceKey}");

            await MapLock.WaitAsync();
            try
            {
                if (!_objectInstanceDict.TryGetValue(instanceKey, out var userKeys))
                {
                    Logger.LogWarning($"게임 종료 시 인스턴스를 찾을 수 없음: {instanceKey}");
                    return;
                }

                var userKeysCopy = userKeys.ToList();

                Logger.LogInformation($"게임 종료 알림 전송: {instanceKey}, 플레이어 수: {userKeysCopy.Count}");

                // 게임 종료 패킷 전송
                using var packet = PacketMaker.G_TO_C_GAME_END(mapSubId);
                foreach (var objectKey in userKeysCopy)
                {
                    if (TryExtractPlayerId(objectKey, out var playerId))
                    {
                        if (_clientSessions.TryGetValue(playerId, out var session))
                        {
                            session.Send(packet);
                        }
                        else
                        {
                            NatsClient.Publish(objectKey, packet.ToBytes());
                        }
                    }
                }

                // 인스턴스 정리
                _objectInstanceDict.TryRemove(instanceKey, out _);
                Logger.LogInformation($"게임 종료 완료 및 인스턴스 제거: {instanceKey}");
            }
            finally
            {
                MapLock.Release();
            }

            // 데미지 타이머도 정리
            if (_damageTimers.TryRemove(instanceKey, out var damageTimer))
            {
                damageTimer.Dispose();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, $"게임 종료 처리 중 오류 발생: {instanceKey}");
        }
    }

    private void BroadcastPacketDirect(string instanceKey, IPacket packet)
    {
        if (!_objectInstanceDict.TryGetValue(instanceKey, out var objectKeys))
        {
            return;
        }

        var objectKeysCopy = objectKeys.ToList();
        foreach (var objectKey in objectKeysCopy)
        {
            if (TryExtractPlayerId(objectKey, out var playerId))
            {
                if (_clientSessions.TryGetValue(playerId, out var session))
                {
                    session.Send(packet);
                }
            }
        }
    }

    private async Task HandleLogout(long playerId, byte[] body)
    {
        var msg = MessagePackSerializer.Deserialize<U_TO_G_LOGOUT>(body);

        await using var playerLock = await PlayerInfo.Lock(CacheHelper.GetRedLockFactory(), playerId);
        var playerInfo = await PlayerInfo.Load(CacheHelper, msg.PlayerId);
        if (playerInfo == null)
        {
            Logger.LogWarning($"플레이어 {playerId} 정보를 찾을 수 없음 (로그아웃)");
            return;
        }

        // 플레이어가 속한 인스턴스에서 제거
        var objectKey = playerInfo.ObjectInfo.GetGameObjectKey();
        var instanceKey = MapHelper.CreatePartKey(playerInfo.ObjectInfo.MapId, playerInfo.ObjectInfo.MapSubId);

        await MapLock.WaitAsync();
        try
        {
            if (_objectInstanceDict.TryGetValue(instanceKey, out var instanceSet))
            {
                instanceSet.Remove(objectKey);
                Logger.LogInformation($"플레이어 {playerId} 인스턴스 {instanceKey}에서 제거됨");

                // 인스턴스가 비어있으면 정리
                if (instanceSet.Count == 0)
                {
                    _objectInstanceDict.TryRemove(instanceKey, out _);
                    Logger.LogInformation($"빈 인스턴스 {instanceKey} 제거됨");
                }
            }
        }
        finally
        {
            MapLock.Release();
        }

        // 다른 플레이어에게 로그아웃 알림
        using var packet = PacketMaker.G_TO_U_DESTROY(objectKey);
        BroadcastPacket(instanceKey, packet);
    }

    private async Task MoveManageObjectAsync(byte[] message)
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
        await MapLock.WaitAsync();
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
            MapLock.Release();
        }
    }

    private async Task LeaveManageObjectAsync(byte[] message)
    {
        var (instanceKey, objectKey) = MessagePackSerializer.Deserialize<(string, string)>(message);
        await MapLock.WaitAsync();
        try
        {
            if (_objectInstanceDict.TryGetValue(instanceKey, out var instanceSet))
                instanceSet.Remove(objectKey);
        }
        finally
        {
            MapLock.Release();
        }
    }

    private Task SpawnManageObject(byte[] message)
    {
        var (objectKey, instanceKeyList, cellsToRemove) =
            MessagePackSerializer.Deserialize<(string, List<string>, List<Cell>)>(message);

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
            return Task.CompletedTask;
        }

        using var packet = PacketMaker.G_TO_U_SPAWN(spawnList, []);

        // TCP로 직접 전송
        if (TryExtractPlayerId(objectKey, out var playerId))
        {
            if (_clientSessions.TryGetValue(playerId, out var session))
            {
                session.Send(packet);
            }
            else
            {
                // 폴백: NATS
                NatsClient.Publish(objectKey, packet.ToBytes());
            }
        }

        return Task.CompletedTask;
    }

    private async Task DestroyManageObjectAsync(byte[] message)
    {
        var (instanceKey, objectType, serializedInfo) = MessagePackSerializer.Deserialize<(string, ObjectType, byte[])>(message);
        var objectKey = MessagePackSerializer.Deserialize<string>(serializedInfo);

        await MapLock.WaitAsync();
        try
        {
            if (_objectInstanceDict.TryGetValue(instanceKey, out var instanceSet))
            {
                instanceSet.Remove(objectKey);
            }
        }
        finally
        {
            MapLock.Release();
        }

        using var packet = PacketMaker.G_TO_U_DESTROY(objectKey);
        BroadcastPacket(instanceKey, packet);
    }

    protected override void BroadcastPacket(string instanceKey, IPacket packet)
    {
        if (!_objectInstanceDict.TryGetValue(instanceKey, out var objectKeys))
        {
            return;
        }

        var objectKeysCopy = objectKeys.ToList();
        foreach (var objectKey in objectKeysCopy)
        {
            // objectKey 형식: "ObjectType_ObjectId" (예: "PLAYER_123")
            if (TryExtractPlayerId(objectKey, out var playerId))
            {
                if (_clientSessions.TryGetValue(playerId, out var session))
                {
                    session.Send(packet);
                }
                else
                {
                    // 클라이언트가 아직 연결되지 않았으면 NATS로 폴백 (임시)
                    NatsClient.Publish(objectKey, packet.ToBytes());
                }
            }
        }
    }

    private bool TryExtractPlayerId(string objectKey, out long playerId)
    {
        playerId = 0;
        var parts = objectKey.Split('_');
        if (parts.Length != 2)
        {
            return false;
        }

        if (parts[0] == "PLAYER" && long.TryParse(parts[1], out playerId))
        {
            return true;
        }

        return false;
    }

    public override async Task ShutdownAsync()
    {
        await MapLock.WaitAsync();
        try
        {
            // 모든 게임 타이머 정리
            foreach (var timer in _gameTimers.Values)
            {
                timer?.Dispose();
            }
            _gameTimers.Clear();

            // 모든 경고 타이머 정리
            foreach (var (oneMinuteTimer, thirtySecondsTimer) in _warningTimers.Values)
            {
                oneMinuteTimer?.Dispose();
                thirtySecondsTimer?.Dispose();
            }
            _warningTimers.Clear();

            // 모든 데미지 타이머 정리
            foreach (var timer in _damageTimers.Values)
            {
                timer?.Dispose();
            }
            _damageTimers.Clear();

            Logger.LogInformation("InstanceMapController 종료: 모든 타이머 정리 완료");
        }
        finally
        {
            MapLock.Release();
        }
    }
}
