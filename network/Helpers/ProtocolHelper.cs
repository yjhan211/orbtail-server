using network.common;

namespace network.helpers;

/// <summary>
/// 프로토콜 분류 및 라우팅 헬퍼
/// </summary>
public static class ProtocolHelper
{
    /// <summary>
    /// GameServer가 처리해야 하는 실시간 프로토콜
    /// </summary>
    private static readonly HashSet<Protocol> GameServerProtocols = new()
    {
        // 실시간 이동 및 동기화
        Protocol.C_TO_U_MOVE,
        Protocol.U_TO_C_MOVE,
        Protocol.U_TO_C_SPAWN,
        Protocol.U_TO_C_DESTROY,

        // 오브젝트 정보
        Protocol.C_TO_U_OBJECT_INFO,
        Protocol.C_TO_U_PLAYER_INFO,
        Protocol.U_TO_C_PLAYER_INFO,

        // 탐험 (실시간 상호작용)
        Protocol.C_TO_U_EXPLORE,
        Protocol.U_TO_C_EXPLORE,
        Protocol.U_TO_C_EXPLORE_COMPLETE,
        Protocol.C_TO_U_EXPLORE_TARGET_INFO,
        Protocol.U_TO_C_EXPLORE_TARGET_INFO,

        // 소셜 액션 (실시간)
        Protocol.C_TO_U_SOCIAL_ACTION,
        Protocol.U_TO_C_SOCIAL_ACTION,

        // 전투 관련
        Protocol.U_TO_C_TAKE_DAMAGE,
        Protocol.U_TO_C_UPDATE_HP,

        // GameServer 전용 프로토콜
        Protocol.C_TO_G_MOVE,
        Protocol.G_TO_C_MOVE,
        Protocol.C_TO_G_ATTACK,
        Protocol.G_TO_C_ATTACK,
        Protocol.C_TO_G_INTERACT,
        Protocol.G_TO_C_INTERACT,
    };

    /// <summary>
    /// UserServer가 처리해야 하는 상태 관리 프로토콜
    /// </summary>
    private static readonly HashSet<Protocol> UserServerProtocols = new()
    {
        // 인증 및 세션
        Protocol.C_TO_U_HEART_BEAT,
        Protocol.U_TO_C_HEART_BEAT,
        Protocol.C_TO_U_LOGIN,
        Protocol.U_TO_C_LOGIN,
        Protocol.C_TO_U_SET_NAME,
        Protocol.U_TO_C_SET_NAME,

        // 인벤토리
        Protocol.U_TO_C_INVENTORY_ITEM_LIST,
        Protocol.U_TO_C_INVENTORY_UPDATE,
        Protocol.C_TO_U_WEAR_ITEM,
        Protocol.U_TO_C_WEAR_ITEM,
        Protocol.C_TO_U_USE_ITEM,
        Protocol.U_TO_C_USE_ITEM,
        Protocol.C_TO_U_BUY_ITEM,
        Protocol.U_TO_C_BUY_ITEM,
        Protocol.C_TO_U_ITEM_PUT,
        Protocol.U_TO_C_ITEM_PUT,

        // 맵 변경 (상태 변경)
        Protocol.C_TO_U_CHANGE_MAP,
        Protocol.U_TO_C_CHANGE_MAP,
        Protocol.U_TO_C_CHANGE_MAP_SUCCESS,
        Protocol.C_TO_U_CHANGE_MAP_SUCCESS,
        Protocol.C_TO_U_ENCAMP,
        Protocol.C_TO_U_DECAMP,
        Protocol.U_TO_C_CAMP_INFO,
        Protocol.C_TO_U_CAMP_INFO,

        // 퀘스트
        Protocol.U_TO_C_QUEST_LIST,
        Protocol.C_TO_U_QUEST_INCREASE,
        Protocol.U_TO_C_QUEST_UPDATE,
        Protocol.C_TO_U_QUEST_SUCCESS,
        Protocol.U_TO_C_QUEST_SUCCESS,

        // 우편함
        Protocol.C_TO_U_MAIL_LIST,
        Protocol.U_TO_C_MAIL_LIST,
        Protocol.C_TO_U_MAIL_RECEIVE,
        Protocol.U_TO_C_MAIL_RECEIVE,

        // 매칭
        Protocol.C_TO_U_MATCHING,
        Protocol.U_TO_C_MATCHING,
        Protocol.C_TO_U_MATCHING_CANCEL,
        Protocol.U_TO_C_MATCHING_CANCEL,
        Protocol.U_TO_C_MATCHING_SUCCESS,
        Protocol.U_TO_C_MATCHING_FAILED,

        // 채팅
        Protocol.C_TO_U_CHAT_MSG,
        Protocol.U_TO_C_CHAT_MSG,
        Protocol.C_TO_U_CHAT_LOG,

        // 맵 업데이트 알림
        Protocol.U_TO_C_MAP_UPDATE,
    };

    /// <summary>
    /// 프로토콜이 GameServer로 가야 하는지 확인
    /// </summary>
    public static bool IsGameServerProtocol(Protocol protocol)
    {
        return GameServerProtocols.Contains(protocol);
    }

    /// <summary>
    /// 프로토콜이 UserServer로 가야 하는지 확인
    /// </summary>
    public static bool IsUserServerProtocol(Protocol protocol)
    {
        return UserServerProtocols.Contains(protocol);
    }

    /// <summary>
    /// 프로토콜이 실시간 처리가 필요한지 확인
    /// </summary>
    public static bool IsRealtimeProtocol(Protocol protocol)
    {
        return protocol switch
        {
            Protocol.C_TO_U_MOVE => true,
            Protocol.U_TO_C_MOVE => true,
            Protocol.U_TO_C_SPAWN => true,
            Protocol.U_TO_C_DESTROY => true,
            Protocol.C_TO_G_MOVE => true,
            Protocol.G_TO_C_MOVE => true,
            Protocol.C_TO_G_ATTACK => true,
            Protocol.G_TO_C_ATTACK => true,
            Protocol.C_TO_G_INTERACT => true,
            Protocol.G_TO_C_INTERACT => true,
            Protocol.U_TO_C_TAKE_DAMAGE => true,
            _ => false
        };
    }

    /// <summary>
    /// 클라이언트가 보내는 프로토콜인지 확인
    /// </summary>
    public static bool IsClientToServerProtocol(Protocol protocol)
    {
        var protocolName = protocol.ToString();
        return protocolName.StartsWith("C_TO_U_") || protocolName.StartsWith("C_TO_G_");
    }

    /// <summary>
    /// 서버가 보내는 프로토콜인지 확인
    /// </summary>
    public static bool IsServerToClientProtocol(Protocol protocol)
    {
        var protocolName = protocol.ToString();
        return protocolName.StartsWith("U_TO_C_") || protocolName.StartsWith("G_TO_C_");
    }
}
