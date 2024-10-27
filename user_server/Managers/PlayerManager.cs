using System.Diagnostics.CodeAnalysis;
using MessagePack;
using network.common;
using network.helpers;
using network.infrastructure;
using network.managers;
using network.packets;
using user_server.handlers;

namespace user_server.managers;

public delegate void SendPacketDelegate(Packet msg);

public class PlayerManager(
    LogManager logManager,
    NatsClient natsClient,
    SendPacketDelegate sendToClient,
    UpdateObjectManager updateObjectManager)
{
    // ReSharper disable once UnusedMember.Local
    private readonly LogManager _logManager = logManager;
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
    public void Initialize(GameObjectInfo objectInfo, MovementHandler movementHandler,
        EnvironmentHandler environmentHandler)
    {
        ObjectInfo = objectInfo;
        ObjectInfo.CurrentCell = ObjectInfo.TargetCell;
        State = PlayerState.IDLE;

        _movementHandler = movementHandler;
        _environmentHandler = environmentHandler;
    }

    public async Task ChangeMap(ChangeMapInfo? changeMapInfo = null)
    {
        // 기존 맵에 삭제 요청
        await PublishDestroy();

        if (changeMapInfo != null)
        {
            ObjectInfo!.MapId = changeMapInfo.MapId;
            ObjectInfo.MapSubId = changeMapInfo.MapSubId;
            ObjectInfo.CurrentCell = changeMapInfo.SpawnCell;
            ObjectInfo.TargetCell = changeMapInfo.SpawnCell;
            ObjectInfo.IsFlip = changeMapInfo.IsFlip;
            await ObjectInfo.Save();
        }

        if (CommonMapHelper.IsCommonMap(MapId))
        {
            using var packet = PacketMaker.U_TO_C_CHANGE_MAP(MapId, MapSubId, CurrentCell, IsFlip);
            sendToClient(packet);
            return;
        }

        var serverId = InstanceMapHelper.GetManageServerId(MapSubId);
        var subject = SubjectHelper.GetEnterInstanceSubject(serverId);
        var publishObj = MessagePackSerializer.Serialize((ObjectInfo!.GetGameObjectKey(), MapId, MapSubId));
        natsClient.Publish(subject, publishObj);
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

        var isCommonMap = CommonMapHelper.IsCommonMap(MapId);
        var managePartKey = isCommonMap
            ? CommonMapHelper.CreatePartKey(MapId, CurrentCell)
            : InstanceMapHelper.CreatePartKey(MapId, MapSubId);
        var manageServer = isCommonMap
            ? CommonMapHelper.GetManageServerId(managePartKey)
            : InstanceMapHelper.GetManageServerId(MapSubId);
        var subject = SubjectHelper.GetUpdateManageSubject(ObjectInfo, manageServer);

        natsClient.Publish(subject, MessagePackSerializer.Serialize((mamagePartKey: managePartKey, ObjectInfo)));
        updateObjectManager.EnqueueUpdateObject(ObjectInfo);
    }

    public async Task Dispose()
    {
        await PublishDestroy();

        if (_movementHandler != null) _movementHandler.Dispose();

        if (_environmentHandler != null) _environmentHandler.Dispose();
    }

    // 접속 종료 시 자신의 object_info 삭제 요청 (PublishLeave랑 다른 점 - 후에 Broadcast 처리가 됨)
    private async Task PublishDestroy()
    {
        if (ObjectInfo == null) return;

        await ObjectInfo.Save();

        var isCommonMap = CommonMapHelper.IsCommonMap(MapId);
        var key = isCommonMap
            ? CommonMapHelper.CreatePartKey(MapId, CurrentCell)
            : InstanceMapHelper.CreatePartKey(MapId, MapSubId);
        var manageServer = isCommonMap
            ? CommonMapHelper.GetManageServerId(key)
            : InstanceMapHelper.GetManageServerId(MapSubId);

        var subject = SubjectHelper.GetDestroyObjectSubject(ObjectInfo, manageServer);
        var message = MessagePackSerializer.Serialize((key, ObjectInfo.GetGameObjectKey()));
        natsClient.Publish(subject, message);
    }
}