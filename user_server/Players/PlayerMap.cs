using MessagePack;
using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;
using network.interfaces;
using network.packets;

namespace user_server.players;

public class PlayerMap(GameUser user, PlayerInfo playerInfo)
{
    private readonly INatsClient _natsClient = user.NatsClient;
    private readonly ICacheHelper _cacheHelper = user.CacheHelper;
    private readonly SendPacketDelegate _sendToClient = user.Send;

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
            return;
        }
        
        if (playerInfo.ObjectInfo.MapId == MapId.Camp)
        {
            playerInfo.LastMapId = playerInfo.ObjectInfo.MapId;
            playerInfo.LastMapSubId = playerInfo.ObjectInfo.MapSubId;
            playerInfo.LastCell = playerInfo.ObjectInfo.CurrentCell.Clone();
            
            await PublishDestroy();
            await EnterMap(playerInfo.CampInfo.ObjectInfo.MapId, playerInfo.CampInfo.ObjectInfo.CurrentCell, false, false);
            await playerInfo.Save(_cacheHelper);
            return;
        }
        
        var changeMapInfo = GameMapData.GetPortalOrNull(playerInfo.ObjectInfo, playerInfo.IsTutorial);
        if (changeMapInfo == null)
        {
            return;
        }
        
        playerInfo.LastMapId = playerInfo.ObjectInfo.MapId;
        playerInfo.LastMapSubId = playerInfo.ObjectInfo.MapSubId;
        playerInfo.LastCell = playerInfo.ObjectInfo.CurrentCell.Clone();
        
        // 기존 맵에 삭제 요청
        await PublishDestroy();
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

        if (GameMapData.IsCommonMap(playerInfo.ObjectInfo.MapId) && !isLogin)
        {
            using var packet = PacketMaker.U_TO_C_CHANGE_MAP(playerInfo.LastMapId, playerInfo.ObjectInfo.MapId, playerInfo.ObjectInfo.MapSubId, playerInfo.ObjectInfo.CurrentCell, playerInfo.ObjectInfo.IsFlip);
            _sendToClient(packet);
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

    private long GetInstanceMapSubId()
    {
        // TODO 동아리
        return playerInfo.ObjectInfo.ObjectId;
    }

    // 접속 종료 시 자신의 object_info 삭제 요청 (PublishLeave랑 다른 점 - 후에 Broadcast 처리가 됨)
    public async Task PublishDestroy()
    {
        await playerInfo.ObjectInfo.Save(_cacheHelper);

        var isCommonMap = GameMapData.IsCommonMap(playerInfo.ObjectInfo.MapId);
        var key = isCommonMap ? MapHelper.CreatePartKey(playerInfo.ObjectInfo.MapId, playerInfo.ObjectInfo.CurrentCell) : MapHelper.CreatePartKey(playerInfo.ObjectInfo.MapId, playerInfo.ObjectInfo.MapSubId);
        var manageServer = isCommonMap ? MapHelper.GetManageServerId(key) : MapHelper.GetManageServerId(playerInfo.ObjectInfo.MapSubId);
        var subject = SubjectHelper.GetDestroyObjectSubject(playerInfo.ObjectInfo, manageServer);
        var message = MessagePackSerializer.Serialize((key, playerInfo.ObjectInfo.GetGameObjectKey()));

        _natsClient.Publish(subject, message);
    }
}