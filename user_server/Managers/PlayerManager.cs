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
public delegate float GetMoveSpeedDelegate();
public delegate void IncreaseHpDelegate(int value);

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
    
    public PlayerInfo PlayerInfo { get; private set; } = null!;
    public GameObjectInfo ObjectInfo => PlayerInfo.ObjectInfo;
    public long PlayerId => PlayerInfo.PlayerId;
    public MapId MapId => PlayerInfo.ObjectInfo.MapId;
    public long MapSubId => PlayerInfo.ObjectInfo.MapSubId;
    public Cell CurrentCell => PlayerInfo.ObjectInfo.CurrentCell;
    public bool IsFlip => PlayerInfo.ObjectInfo.IsFlip;
    public PlayerState State => PlayerInfo?.State ?? PlayerState.NONE;
    public string ObjectKey => PlayerInfo.ObjectInfo.GetGameObjectKey();

    [MemberNotNull(nameof(PlayerInfo))]
    public void Initialize(PlayerInfo playerInfo, EnvironmentHandler environmentHandler)
    {
        PlayerInfo = playerInfo;
        PlayerInfo.ObjectInfo.CurrentCell = ObjectInfo.TargetCell;
        PlayerInfo.State = PlayerState.IDLE;
        
        _movementHandler = new MovementHandler(_logManager, PlayerInfo.ObjectInfo, natsClient, sendToClient, updateObjectManager, GetMoveSpeed, IncreaseHp);
        _environmentHandler = environmentHandler;
    }

    public async Task ChangeMap(bool isLogin = false)
    {
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
        PlayerInfo.ObjectInfo.MapId = mapId;
        PlayerInfo.ObjectInfo.MapSubId = GameMapData.IsCommonMap(mapId) ? 0 : GetInstanceMapSubId();
        PlayerInfo.ObjectInfo.CurrentCell = spawnPosition;
        PlayerInfo.ObjectInfo.TargetCell = spawnPosition;
        PlayerInfo.ObjectInfo.IsFlip = isFlip;
        await PlayerInfo.ObjectInfo.Save();

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
        
        // TODO 동아리
        return ObjectInfo.ObjectId;
    }

    public async Task Spawn()
    {
        if (_movementHandler == null) return;

        await _movementHandler.Spawn();
    }

    public async Task RequestMove(GameUser user, C_TO_U_MOVE body)
    {
        if (_movementHandler == null) return;

        switch (PlayerInfo.State)
        {
            case PlayerState.SITGROUND:
                PlayerInfo.State = PlayerState.IDLE;
                await PlayerInfo.Save();
                user.BroadcastUpdateInfo(user.PlayerManager.PlayerInfo);
                break;
        }

        if (PlayerInfo.Boosts.Contains(BoostType.SPEED))
        {
            if (PlayerInfo.Hp < 5)
            {
                PlayerInfo.Boosts.Remove(BoostType.SPEED);
                await PlayerInfo.Save();
                user.BroadcastUpdateInfo(user.PlayerManager.PlayerInfo);
            }
        }

        await _movementHandler.ProcessAsync(body);
    }

    public async Task UpdateBoost(GameUser user, C_TO_U_BOOST body)
    {
        if (PlayerInfo.Boosts.TryGetValue(body.BoostType, out var boost))
        {
            PlayerInfo.Boosts.Remove(boost);
        }
        else
        {
            PlayerInfo.Boosts.Add(body.BoostType);
        }

        await PlayerInfo.Save();
        user.BroadcastUpdateInfo(user.PlayerManager.PlayerInfo);
    }

    public async Task SocialAction(GameUser user, C_TO_U_SOCIAL_ACTION body)
    {
        switch (body.SocialActionType)
        {
            case SocialActionType.SITGROUND:
                PlayerInfo.State = PlayerState.SITGROUND;
                await PlayerInfo.Save();
                user.BroadcastUpdateInfo(user.PlayerManager.PlayerInfo);
                break;

            default:
                break;
        }
    }

    public async Task Wear(long itemUid)
    {
        PlayerInfo.WearItem(itemUid);
        await PlayerInfo.Save();
    }

    public async Task SetState(PlayerState newState)
    {
        PlayerInfo.State = newState;
        await PlayerInfo.Save();
    }

    public async Task SetFlip(DirectionType direction)
    {
        if (direction == DirectionType.NONE)
        {
            return;
        }

        ObjectInfo.SetFlip(direction);
        await ObjectInfo.Save();

        var isCommonMap = GameMapData.IsCommonMap(MapId);
        var managePartKey = isCommonMap ? MapHelper.CreatePartKey(MapId, CurrentCell) : MapHelper.CreatePartKey(MapId, MapSubId);
        var manageServer = isCommonMap ? MapHelper.GetManageServerId(managePartKey) : MapHelper.GetManageServerId(MapSubId);
        var subject = SubjectHelper.GetUpdateManageSubject(ObjectInfo, manageServer);

        natsClient.Publish(subject, MessagePackSerializer.Serialize((mamagePartKey: managePartKey, ObjectInfo)));
        updateObjectManager.EnqueueUpdateObject(ObjectInfo);
    }

    private float GetMoveSpeed()
    {
        if (PlayerInfo.Boosts.Contains(BoostType.SPEED))
        {
            return 2;
        }

        if (PlayerInfo.Hp <= 0)
        {
            return 0.5f;
        }

        return 1;
    }

    private void IncreaseHp(int value)
    {
        PlayerInfo.Hp = Math.Clamp(PlayerInfo.Hp + value, 0, 10000);
        
        // 너무 빈번해서 레디스에는 업데이트 안하고 있음
        // TODO 레이드 시 파티 단위로 브로드캐스트
        using var packet = PacketMaker.U_TO_C_PLAYER_INFO([PlayerInfo]);
        sendToClient(packet);
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
        await ObjectInfo.Save();

        var isCommonMap = GameMapData.IsCommonMap(MapId);
        var key = isCommonMap ? MapHelper.CreatePartKey(MapId, CurrentCell) : MapHelper.CreatePartKey(MapId, MapSubId);
        var manageServer = isCommonMap ? MapHelper.GetManageServerId(key) : MapHelper.GetManageServerId(MapSubId);
        var subject = SubjectHelper.GetDestroyObjectSubject(ObjectInfo, manageServer);
        var message = MessagePackSerializer.Serialize((key, ObjectInfo.GetGameObjectKey()));

        natsClient.Publish(subject, message);
    }
}