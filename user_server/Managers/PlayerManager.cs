using System.Diagnostics.CodeAnalysis;
using MessagePack;
using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;
using network.infrastructure;
using network.managers;
using network.packets;
using user_server.handlers;

namespace user_server.managers;

public delegate void SendPacketDelegate(Packet msg);

public class PlayerManager(
    LogManager? logManager,
    NatsClient natsClient,
    SendPacketDelegate sendToClient,
    UpdateObjectManager updateObjectManager)
{
    // ReSharper disable once UnusedMember.Local
    private readonly LogManager? _logManager = logManager;
    private EnvironmentHandler? _environmentHandler;
    private MovementHandler? _movementHandler;

    public GameObjectInfo? ObjectInfo { get; private set; }
    public long PlayerId => ObjectInfo?.ObjectId ?? 0;
    public MapId MapId => ObjectInfo?.MapId ?? MapId.NONE;
    public long MapSubId => ObjectInfo?.MapSubId ?? 0;
    public Cell CurrentCell => ObjectInfo?.CurrentCell ?? new Cell(0, 0);
    public bool IsFlip => ObjectInfo?.IsFlip ?? false;
    public PlayerState State { get; private set; }
    public string ObjectKey => ObjectInfo?.GetGameObjectKey() ?? string.Empty;

    [MemberNotNull(nameof(ObjectInfo))]
    public void Initialize(GameObjectInfo objectInfo, MovementHandler movementHandler, EnvironmentHandler environmentHandler)
    {
        ObjectInfo = objectInfo;
        ObjectInfo.CurrentCell = ObjectInfo.TargetCell;
        State = PlayerState.IDLE;

        _movementHandler = movementHandler;
        _environmentHandler = environmentHandler;
    }

    public async Task ChangeMap(bool isLogin = false)
    {
        if (ObjectInfo == null)
        {
            return;
        }
        
        var changeMapInfo = GameMapData.GetPortalOrNull(ObjectInfo);
        if (changeMapInfo == null)
        {
            return;
        }
        
        // 기존 맵에 삭제 요청
        await PublishDestroy();
        await EnterMap(changeMapInfo.Value.mapId, changeMapInfo.Value.spawnPosition, changeMapInfo.Value.isFlip, isLogin);
    }

    public async Task EnterMap(MapId mapId, Cell spawnPosition, bool isFlip, bool isLogin)
    {
        ObjectInfo!.MapId = mapId;
        ObjectInfo.MapSubId = GameMapData.IsCommonMap(mapId) ? 0 : GetInstanceMapSubId();
        ObjectInfo.CurrentCell = spawnPosition;
        ObjectInfo.TargetCell = spawnPosition;
        ObjectInfo.IsFlip = isFlip;
        await ObjectInfo.Save();

        if (GameMapData.IsCommonMap(MapId) && !isLogin)
        {
            using var packet = PacketMaker.U_TO_C_CHANGE_MAP(MapId, MapSubId, CurrentCell, IsFlip);
            sendToClient(packet);
            return;
        }

        var serverId = MapHelper.GetManageServerId(MapSubId);
        var subject = SubjectHelper.GetEnterInstanceSubject(serverId);
        var publishObj = MessagePackSerializer.Serialize((ObjectInfo.GetGameObjectKey(), MapId, MapSubId, isLogin));
        _logManager?.WriteDebugLog($"EnterMap subject: {subject}");
        natsClient.Publish(subject, publishObj);
    }

    private long GetInstanceMapSubId()
    {
        if (ObjectInfo == null)
        {
            throw new Exception("ObjectInfo is null");
        }
        
        return ObjectInfo.MapId switch
        {
            MapId.LIBRARY => // 도서관은 개인맵
                ObjectInfo!.ObjectId,
            _ => throw new ArgumentOutOfRangeException($"Invalid InstanceMap")
        };
    }

    public async Task Spawn()
    {
        if (_movementHandler == null) return;

        await _movementHandler.Spawn();
    }

    public async Task RequestMove(GameUser _, C_TO_U_MOVE body)
    {
        if (_movementHandler == null) return;

        await _movementHandler.ProcessAsync(body);
    }

    public void SetState(PlayerInfo playerInfo, PlayerState newState)
    {
        playerInfo.State = newState;
        State = newState;
    }


    public async Task SetFlip(DirectionType direction)
    {
        if (direction == DirectionType.NONE || ObjectInfo == null) return;

        ObjectInfo.SetFlip(direction);
        await ObjectInfo.Save();

        var isCommonMap = GameMapData.IsCommonMap(MapId);
        var managePartKey = isCommonMap
            ? MapHelper.CreatePartKey(MapId, CurrentCell)
            : MapHelper.CreatePartKey(MapId, MapSubId);
        var manageServer = isCommonMap
            ? MapHelper.GetManageServerId(managePartKey)
            : MapHelper.GetManageServerId(MapSubId);
        var subject = SubjectHelper.GetUpdateManageSubject(ObjectInfo, manageServer);

        natsClient.Publish(subject, MessagePackSerializer.Serialize((mamagePartKey: managePartKey, ObjectInfo)));
        updateObjectManager.EnqueueUpdateObject(ObjectInfo);
    }

    public async Task Dispose()
    {
        await PublishDestroy();

        _movementHandler?.Dispose();
        _environmentHandler?.Dispose();
    }

    // 접속 종료 시 자신의 object_info 삭제 요청 (PublishLeave랑 다른 점 - 후에 Broadcast 처리가 됨)
    private async Task PublishDestroy()
    {
        if (ObjectInfo == null) return;

        await ObjectInfo.Save();

        var isCommonMap = GameMapData.IsCommonMap(MapId);
        var key = isCommonMap
            ? MapHelper.CreatePartKey(MapId, CurrentCell)
            : MapHelper.CreatePartKey(MapId, MapSubId);
        var manageServer = isCommonMap
            ? MapHelper.GetManageServerId(key)
            : MapHelper.GetManageServerId(MapSubId);

        var subject = SubjectHelper.GetDestroyObjectSubject(ObjectInfo, manageServer);
        var message = MessagePackSerializer.Serialize((key, ObjectInfo.GetGameObjectKey()));
        natsClient.Publish(subject, message);
    }
}