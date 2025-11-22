using user_server.infrastructure.network;
﻿using log4net.Repository.Hierarchy;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;
using network.interfaces;
using network.packets;
using user_server.domain.player;

namespace user_server.domain.player;

public class PlayerMap(GameSession user, PlayerInfo playerInfo)
{
    private readonly INatsClient _natsClient = user.NatsClient;
    private readonly ICacheHelper _cacheHelper = user.CacheHelper;
    private readonly SendPacketDelegate _sendToClient = user.Send;
    private readonly ILogger _logger = user.Logger;

    public (MapId, long, Cell, bool) CurrentMapInfo => 
        (playerInfo.ObjectInfo.MapId, playerInfo.ObjectInfo.MapSubId, playerInfo.ObjectInfo.CurrentCell, playerInfo.ObjectInfo.IsFlip);

    public (MapId, Cell) LastMapInfo => 
        (playerInfo.LastMapId, playerInfo.LastCell);

    public async Task ChangeMap(MapId mapId)
    {
        _logger.LogInformation($"ChangeMap 시작: PlayerId={playerInfo.PlayerId}, CurrentMap={playerInfo.ObjectInfo.MapId}, TargetMap={mapId}");

        playerInfo.LastMapId = playerInfo.ObjectInfo.MapId;
        playerInfo.LastMapSubId = playerInfo.ObjectInfo.MapSubId;
        playerInfo.LastCell = playerInfo.ObjectInfo.CurrentCell.Clone();

        using var packet2 = PacketMaker.U_TO_C_CHANGE_MAP(ErrorCode.SUCCESS, mapId);
        _sendToClient(packet2);
        _logger.LogInformation($"U_TO_C_CHANGE_MAP 전송 완료: PlayerId={playerInfo.PlayerId}, MapId={mapId}");

        // 기존 맵에 삭제 요청
        user.BroadcastObjectDestroy(playerInfo.ObjectInfo);

        var changeMapInfo = GameMapData.GetMapInfo(mapId);
        Cell spawnPosition;
        bool isFlip;

        try
        {
            (spawnPosition, isFlip) = changeMapInfo.GetInitialPosition();
        }
        catch (InvalidOperationException)
        {
            // InitCells가 비어있는 경우 기본값 사용
            _logger.LogWarning($"맵 {mapId}의 InitCells가 비어있음. 기본 위치 사용");
            spawnPosition = new Cell(50, 50);
            isFlip = false;
        }

        await EnterMap(mapId, spawnPosition, isFlip, false);
        await playerInfo.Save(_cacheHelper);
    }
    
    public async Task EnterMap(MapId mapId, Cell spawnPosition, bool isFlip, bool isLogin)
    {
        playerInfo.ObjectInfo.MapId = mapId;
        playerInfo.ObjectInfo.MapSubId =  GetInstanceMapSubId();
        playerInfo.ObjectInfo.CurrentCell = spawnPosition;
        playerInfo.ObjectInfo.TargetCell = spawnPosition;
        playerInfo.ObjectInfo.IsFlip = isFlip;
        await playerInfo.ObjectInfo.Save(_cacheHelper);

        var serverId = MapHelper.GetManageServerId(playerInfo.ObjectInfo.MapSubId);
        var subject = SubjectHelper.GetEnterInstanceSubject(serverId);
        var publishObj = MessagePackSerializer.Serialize((playerInfo.ObjectInfo.GetGameObjectKey(), playerInfo.ObjectInfo.MapId, playerInfo.ObjectInfo.MapSubId, isLogin));
        _natsClient.Publish(subject, publishObj);
    }
    
    public async Task EnterCamp(long mapSubId)
    {
        playerInfo.ObjectInfo.MapId = MapId.Camp;
        playerInfo.ObjectInfo.MapSubId = mapSubId;

        var campMapInfo = GameMapData.GetMapInfo(playerInfo.ObjectInfo.MapId);
        var (spawnPosition, isFlip) = campMapInfo.GetInitialPosition();
        playerInfo.ObjectInfo.CurrentCell = spawnPosition;
        playerInfo.ObjectInfo.TargetCell = spawnPosition;
        playerInfo.ObjectInfo.IsFlip = isFlip;

        await playerInfo.Save(_cacheHelper);
    }
    
    private bool IsAbleChangeMap(MapId currentMapId, MapId changeMapId)
    {
        return true;
    }
    
    private long GetInstanceMapSubId()
    {
        if (playerInfo.PlayerId > PlayerConstants.DUMMY_PLAYER_ID_THRESHOLD)
        {
            return playerInfo.PlayerId - PlayerConstants.DUMMY_PLAYER_ID_THRESHOLD;
        }

        // TODO 동아리
        return playerInfo.ObjectInfo.ObjectId;
    }
}
