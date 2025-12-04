// ReSharper disable All
namespace network.common
{
    public enum Protocol
    {
        // UserServer 프로토콜
        C_TO_U_HEART_BEAT = 0,
        U_TO_C_HEART_BEAT,
        C_TO_U_LOGIN,
        U_TO_C_LOGIN,
        C_TO_U_SET_NAME,
        U_TO_C_SET_NAME,
        U_TO_C_INVENTORY_ITEM_LIST,
        U_TO_C_INVENTORY_UPDATE,
        C_TO_U_CHAT_MSG,
        U_TO_C_CHAT_MSG,
        C_TO_U_CHAT_LOG,
        C_TO_U_PLAYER_INFO,
        U_TO_C_PLAYER_INFO,
        C_TO_U_WEAR_ITEM,
        U_TO_C_WEAR_ITEM,
        C_TO_U_USE_ITEM,
        U_TO_C_USE_ITEM,
        U_TO_U_DUPLICATE,
        C_TO_U_QUEST_INCREASE,
        U_TO_C_QUEST_UPDATE,
        C_TO_U_QUEST_SUCCESS,
        U_TO_C_QUEST_SUCCESS,
        C_TO_U_MAIL_LIST,
        U_TO_C_MAIL_LIST,
        C_TO_U_MAIL_RECEIVE,
        U_TO_C_MAIL_RECEIVE,
        C_TO_U_MATCHING,
        U_TO_C_MATCHING,
        C_TO_U_MATCHING_CANCEL,
        U_TO_C_MATCHING_CANCEL,
        U_TO_C_MATCHING_SUCCESS,
        U_TO_C_MATCHING_FAILED,

        // GameServer 프로토콜 (세션 기반 실시간 게임)
        C_TO_G_HEART_BEAT,
        G_TO_C_HEART_BEAT,
        C_TO_G_CONNECT,
        G_TO_C_CONNECT_RESULT,
        G_TO_C_PLAYER_INFO,
        C_TO_G_MOVE,
        G_TO_C_MOVE,
        C_TO_G_ATTACK,
        C_TO_G_INTERACT,
        G_TO_C_GAME_TIME_WARNING,
        G_TO_C_GAME_END,
        G_TO_C_AREA_PLAYER_ENTER, // 다른 플레이어가 내 Area에 진입
        G_TO_C_AREA_PLAYER_LEAVE, // 다른 플레이어가 내 Area에서 퇴장
        G_TO_C_INTERACTABLE_LIST, // Area 진입 시 탐색 가능한 오브젝트 목록
        G_TO_C_INTERACTABLE_UPDATE, // 오브젝트 탐색 상태 변경 (누군가 탐색함)

        // 탐색 프로토콜
        C_TO_G_EXPLORE_START, // 탐색 시작 요청
        G_TO_C_EXPLORE_START, // 탐색 시작 브로드캐스트 (애니메이션 동기화)
        C_TO_G_EXPLORE_SELECT, // 선택지 선택
        G_TO_C_EXPLORE_RESULT, // 탐색 결과 (보상 등)
        G_TO_C_EXPLORE_END, // 탐색 종료 브로드캐스트

        END
    }

    public enum ErrorCode
    {
        SUCCESS = 0,
        ALREADY_HAS_JOB,
        ALREADY_ANOTHER_USE_SKILL,
        INVALID_POSITION,
        FATAL
    }
}
