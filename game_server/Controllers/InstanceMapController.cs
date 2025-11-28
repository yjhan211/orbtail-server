using System.Collections.Concurrent;
using game_server.network;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.config;
using network.helpers;
using network.interfaces;
using network.packets;

namespace game_server.controllers;

public sealed class InstanceMapController(
    ILogger logger,
    INatsClient natsClient,
    ICacheHelper cacheHelper,
    ServerConfig serverConfig,
    ConcurrentDictionary<long, GameClientSession> clientSessions)
    : BaseMapController(logger, natsClient, cacheHelper, serverConfig)
{
    private readonly ConcurrentDictionary<string, HashSet<string>> _objectInstanceDict = new();
    private readonly ConcurrentDictionary<string, Timer> _gameTimers = new();
    private readonly ConcurrentDictionary<string, (Timer OneMinuteTimer, Timer ThirtySecondsTimer)> _warningTimers = new();

    private const int GameDurationMinutes = 15;
    private const int GameDurationSeconds = GameDurationMinutes * 60;

    private string EnterInstanceSubject => SubjectHelper.GetEnterInstanceSubject(ServerConfig.ServerId);

    public void Initialize()
    {
        SubscribeWithHandler(EnterInstanceSubject, EnterInstance);
    }

    private void SubscribeToInstanceEvents(MapId mapId, long mapSubId)
    {
        var subjects = new Dictionary<string, Func<byte[], Task>>
       {
           { SubjectHelper.GetLeaveManageSubject(mapId, mapSubId, ServerConfig.ServerId), LeaveManageObjectAsync },
       };

        foreach (var (subject, handler) in subjects)
        {
            SubscribeWithHandler(subject, handler);
        }
    }

    private async Task EnterInstance(byte[] message)
    {
        var (objectKey, mapId, mapSubId, isLogin) =
            MessagePackSerializer.Deserialize<(string, MapId, long, bool)>(message);
        var instanceKey = MapHelper.CreatePartKey(mapId, mapSubId);
        
        Logger.LogInformation("EnterInstance 요청 수신: objectKey={ObjectKey}, mapId={MapId}, mapSubId={MapSubId}, isLogin={IsLogin}", objectKey, mapId, mapSubId, isLogin);

        await MapLock.WaitAsync();
        try
        {
            var isInit = _objectInstanceDict.TryAdd(instanceKey, []);
            _objectInstanceDict[instanceKey].Add(objectKey);

            Logger.LogInformation("인스턴스 {InstanceKey} 초기화 여부: {IsInit}, 현재 인원: {Count}", instanceKey, isInit, _objectInstanceDict[instanceKey].Count);

            if (isInit)
            {
                SubscribeToInstanceEvents(mapId, mapSubId);
                switch (mapId)
                {
                    case MapId.Camp:
                        break;

                    case MapId.School:
                        StartGameTimer(instanceKey, mapSubId);
                        break;
                }

                Logger.LogInformation("인스턴스 {InstanceKey} 초기화 완료", instanceKey);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "인스턴스 진입 실패: objectKey={ObjectKey}, instanceKey={InstanceKey}", objectKey, instanceKey);
            if (_objectInstanceDict.TryGetValue(instanceKey, out var instanceSet))
            {
                instanceSet.Remove(objectKey);
            }
        }
        finally
        {
            MapLock.Release();
        }

        if (isLogin)
        {
            return;
        }

        // MMO 인스턴스 진입 프로토콜 제거됨 - 세션 기반 게임에서는 불필요
        // if (success)
        // {
        //     Logger.LogInformation("G_TO_U_ENTER_INSTANCE_SUCCESS 전송: objectKey={ObjectKey}, mapId={MapId}, mapSubId={MapSubId}", objectKey, mapId, mapSubId);
        //     using var packet = PacketMaker.G_TO_U_ENTER_INSTANCE_SUCCESS(mapId, mapSubId);
        //     ...
        // }
    }

    private void StartGameTimer(string instanceKey, long mapSubId)
    {
        Logger.LogInformation("게임 타이머 시작: {InstanceKey} (15분)", instanceKey);
        var gameTimer = new Timer(_ =>
        {
            EndGame(instanceKey, mapSubId);
            if (_gameTimers.TryRemove(instanceKey, out var t))
            {
                t.Dispose();
            }
            if (!_warningTimers.TryRemove(instanceKey, out var warningTimers))
            {
                return;
            }
            warningTimers.OneMinuteTimer.Dispose();
            warningTimers.ThirtySecondsTimer.Dispose();
        }, null, TimeSpan.FromSeconds(GameDurationSeconds), Timeout.InfiniteTimeSpan);

        _gameTimers[instanceKey] = gameTimer;
        
        var oneMinuteWarningTimer = new Timer(_ =>
        {
            SendGameTimeWarning(instanceKey, 60);
        }, null, TimeSpan.FromSeconds(GameDurationSeconds - 60), Timeout.InfiniteTimeSpan);
        
        var thirtySecondsWarningTimer = new Timer(_ =>
        {
            SendGameTimeWarning(instanceKey, 30);
        }, null, TimeSpan.FromSeconds(GameDurationSeconds - 30), Timeout.InfiniteTimeSpan);

        _warningTimers[instanceKey] = (oneMinuteWarningTimer, thirtySecondsWarningTimer);

        Logger.LogInformation("게임 타이머 및 알림 타이머 설정 완료: {InstanceKey}", instanceKey);
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

    private async void EndGame(string instanceKey, long mapSubId)
    {
        try
        {
            Logger.LogInformation("게임 종료 처리 시작: {InstanceKey}", instanceKey);
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
                        if (clientSessions.TryGetValue(playerId, out var session))
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
                if (clientSessions.TryGetValue(playerId, out var session))
                {
                    session.Send(packet);
                }
            }
        }
    }

    // MMO 오브젝트 업데이트 프로토콜 제거됨
    // private async Task MoveManageObjectAsync(byte[] message) { ... }

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

    // MMO 오브젝트 스폰 프로토콜 제거됨
    // private Task SpawnManageObject(byte[] message) { ... }

    // MMO 오브젝트 파괴 프로토콜 제거됨
    // private async Task DestroyManageObjectAsync(byte[] message) { ... }

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
                if (clientSessions.TryGetValue(playerId, out var session))
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

    public async Task ShutdownAsync()
    {
        await MapLock.WaitAsync();
        try
        {
            // 모든 게임 타이머 정리
            foreach (var timer in _gameTimers.Values)
            {
                await timer.DisposeAsync();
            }
            _gameTimers.Clear();

            // 모든 경고 타이머 정리
            foreach (var (oneMinuteTimer, thirtySecondsTimer) in _warningTimers.Values)
            {
                await oneMinuteTimer.DisposeAsync();
                await thirtySecondsTimer.DisposeAsync();
            }
            _warningTimers.Clear();
            Logger.LogInformation("InstanceMapController 종료: 모든 타이머 정리 완료");
        }
        finally
        {
            MapLock.Release();
        }
    }
}
