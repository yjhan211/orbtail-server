using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.interfaces;
using user_server.infrastructure.network;

namespace user_server.domain.player;

/// <summary>
/// Player Aggregate Root
/// DDD 패턴에 따라 Player 관련 모든 비즈니스 로직을 캡슐화
/// </summary>
public partial class Player
{
    // Dependencies
    private readonly GameSession _session;
    private readonly ICacheHelper _cacheHelper;
    private readonly ILogger _logger;

    // Delegates for broadcasting
    private readonly SendPacketDelegate _sendToClient;
    private readonly BroadcastDelegate<PlayerInfo> _broadcastPlayerInfo;
    private readonly BroadcastSocialActionDelegate _broadcastSocialAction;
    private readonly BroadcastTakeDamageDelegate _broadcastTakeDamage;
    private readonly BroadcastDestroyDelegate _broadcastDestroy;

    // Player State (Aggregate Root)
    public PlayerInfo PlayerInfo { get; }

    // Sub-components
    private readonly PlayerQuest _questManager;
    private readonly PlayerInventory _inventoryManager;
    private readonly PlayerMailBox _mailBoxManager;
    private readonly PlayerMap _mapManager;

    // Properties
    public long PlayerId => PlayerInfo.PlayerId;
    public string PlayerName => PlayerInfo.Name;
    public string ObjectKey => PlayerInfo.ObjectInfo.GetGameObjectKey();

    public (MapId, long, Cell, bool) CurrentMapInfo => _mapManager.CurrentMapInfo;
    public (MapId, Cell) LastMapInfo => _mapManager.LastMapInfo;

    // Action validation
    private static readonly IReadOnlyList<Protocol> ActionProtocol = new List<Protocol>
    {
        // 이동은 GameServer에서 처리 (C_TO_G_MOVE)
        Protocol.C_TO_U_WEAR_ITEM,
    };

    private static readonly IReadOnlyList<PlayerState> ActionState = new List<PlayerState>
    {
        PlayerState.CRAFT_1,
        PlayerState.EXPLORE_1,
    };

    public Player(GameSession session, PlayerInfo playerInfo)
    {
        _session = session;
        _cacheHelper = session.CacheHelper;
        _logger = session.Logger;

        _sendToClient = session.Send;
        _broadcastPlayerInfo = session.BroadcastUpdateInfo;
        _broadcastSocialAction = session.BroadcastSocialAction;
        _broadcastTakeDamage = session.BroadcastTakeDamage;
        _broadcastDestroy = session.BroadcastObjectDestroy;

        PlayerInfo = playerInfo;
        PlayerInfo.ObjectInfo.CurrentCell = PlayerInfo.ObjectInfo.TargetCell;
        PlayerInfo.State = PlayerState.IDLE;

        // Initialize sub-components
        var progressManager = new PlayerProgress(session);
        _questManager = new PlayerQuest(session, PlayerInfo);
        _inventoryManager = new PlayerInventory(session, PlayerInfo, _questManager);
        _mailBoxManager = new PlayerMailBox(session, PlayerInfo);
        _mapManager = new PlayerMap(session, PlayerInfo);
    }

    public bool IsInvalidAction(Protocol protocolId)
    {
        return ActionProtocol.Contains(protocolId) && ActionState.Contains(PlayerInfo.State);
    }

    public async Task Dispose()
    {
        await PlayerInfo.Save(_cacheHelper);
        _logger.LogInformation("[{PlayerId}] Player disposed", PlayerId);
    }
}
