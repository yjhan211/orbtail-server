using log4net.Repository.Hierarchy;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;
using network.interfaces;
using network.packets;
using user_server.controllers;

namespace user_server.players;

public class PlayerMap(GameUser user, PlayerInfo playerInfo)
{
    private readonly INatsClient _natsClient = user.NatsClient;
    private readonly ICacheHelper _cacheHelper = user.CacheHelper;
    private readonly SendPacketDelegate _sendToClient = user.Send;
    private readonly ILogger _logger = user.Logger;

    public (MapId, long, Cell, bool) CurrentMapInfo => 
        (playerInfo.ObjectInfo.MapId, playerInfo.ObjectInfo.MapSubId, playerInfo.ObjectInfo.CurrentCell, playerInfo.ObjectInfo.IsFlip);

    public (MapId, Cell) LastMapInfo => 
        (playerInfo.LastMapId, playerInfo.LastCell);

    public async Task ChangeMap(C_TO_U_CHANGE_MAP body)
    {
        if (body.MapId == MapId.Camp)
        {
            playerInfo.LastMapId = playerInfo.ObjectInfo.MapId;
            playerInfo.LastMapSubId = playerInfo.ObjectInfo.MapSubId;
            playerInfo.LastCell = playerInfo.ObjectInfo.CurrentCell.Clone();
            await playerInfo.Save(_cacheHelper);
            
            var serverId = MapHelper.GetManageServerId(playerInfo.ObjectInfo.MapSubId);
            var subject = SubjectHelper.GetEnterInstanceSubject(serverId);
            var publishObj = MessagePackSerializer.Serialize((playerInfo.ObjectInfo.GetGameObjectKey(), body.MapId, body.MapSubId, false));
            _natsClient.Publish(subject, publishObj);
            
            using var packet = PacketMaker.U_TO_C_CHANGE_MAP(ErrorCode.SUCCESS);
            _sendToClient(packet);
            return;
        }
        
        if (playerInfo.ObjectInfo.MapId == MapId.Camp)
        {
            playerInfo.LastMapId = playerInfo.ObjectInfo.MapId;
            playerInfo.LastMapSubId = playerInfo.ObjectInfo.MapSubId;
            playerInfo.LastCell = playerInfo.ObjectInfo.CurrentCell.Clone();
            
            user.BroadcastObjectDestroy(playerInfo.ObjectInfo);
            await EnterMap(playerInfo.CampInfo.ObjectInfo.MapId, playerInfo.CampInfo.ObjectInfo.CurrentCell, false, false);
            await playerInfo.Save(_cacheHelper);
            
            using var packet = PacketMaker.U_TO_C_CHANGE_MAP(ErrorCode.SUCCESS);
            _sendToClient(packet);
            return;
        }

        var changeMapInfo = GameMapData.GetPortalOrNull(playerInfo.ObjectInfo, playerInfo.IsTutorial);
        if (playerInfo.State != PlayerState.SLEEP)
        {
            if (changeMapInfo == null)
            {
                using var packet = PacketMaker.U_TO_C_CHANGE_MAP(ErrorCode.FATAL);
                _sendToClient(packet);
                return;
            }
        
            if (playerInfo.IsTutorial && !IsAbleChangeMap(playerInfo.ObjectInfo.MapId, changeMapInfo.Value.mapId))
            {
                using var packet = PacketMaker.U_TO_C_CHANGE_MAP(ErrorCode.FATAL);
                _sendToClient(packet);
                return;
            }
        }
        else
        {
            playerInfo.State = PlayerState.IDLE;
            playerInfo.Hp = 1000;
            // changeMapInfo = (MapId.TutorialLibrary, new Cell(92, 99), false);
        }
        
        playerInfo.LastMapId = playerInfo.ObjectInfo.MapId;
        playerInfo.LastMapSubId = playerInfo.ObjectInfo.MapSubId;
        playerInfo.LastCell = playerInfo.ObjectInfo.CurrentCell.Clone();
        
        using var packet2 = PacketMaker.U_TO_C_CHANGE_MAP(ErrorCode.SUCCESS);
        _sendToClient(packet2);
        
        // 기존 맵에 삭제 요청
        user.BroadcastObjectDestroy(playerInfo.ObjectInfo);
        // await PublishDestroy();
        await EnterMap(changeMapInfo.Value.mapId, changeMapInfo.Value.spawnPosition, changeMapInfo.Value.isFlip, false);
        await playerInfo.Save(_cacheHelper);
    }
    
    public async Task EnterMap(MapId mapId, Cell spawnPosition, bool isFlip, bool isLogin)
    {
        playerInfo.ObjectInfo.MapId = mapId;
        playerInfo.ObjectInfo.MapSubId = GameMapData.IsCommonMap(mapId) ? 0 : GetInstanceMapSubId();
        playerInfo.ObjectInfo.CurrentCell = spawnPosition;
        playerInfo.ObjectInfo.TargetCell = spawnPosition;
        playerInfo.ObjectInfo.IsFlip = isFlip;
        await playerInfo.ObjectInfo.Save(_cacheHelper);

        if (GameMapData.IsCommonMap(playerInfo.ObjectInfo.MapId))
        {
            if (!isLogin)
            {
                using var packet = PacketMaker.U_TO_C_CHANGE_MAP_SUCCESS(playerInfo.LastMapId, playerInfo.ObjectInfo.MapId, playerInfo.ObjectInfo.MapSubId, playerInfo.ObjectInfo.CurrentCell, playerInfo.ObjectInfo.IsFlip);
                _sendToClient(packet);
            }
            return;
        }

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
        var (spawnPosition, isFlip) = campMapInfo.GetInitialPosition(MapId.None);
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
        if (playerInfo.PlayerId > 1000)
        {
            return playerInfo.PlayerId - 1000;
        }
        
        // TODO 동아리
        return playerInfo.ObjectInfo.ObjectId;
    }
}