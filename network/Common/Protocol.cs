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
        G_TO_C_AREA_EXIT_BLOCKED, // Area 퇴장 조건 미충족 시 이동 차단 알림

        // 탐색 프로토콜
        C_TO_G_EXPLORE_START, // 탐색 시작 요청
        G_TO_C_EXPLORE_START, // 탐색 시작 브로드캐스트 (애니메이션 동기화)
        C_TO_G_EXPLORE_SELECT, // 선택지 선택
        G_TO_C_EXPLORE_RESULT, // 탐색 결과 (보상 등)
        C_TO_G_EXPLORE_END, // 탐색 종료 요청 (UI 닫기)
        G_TO_C_EXPLORE_END, // 탐색 종료 브로드캐스트
        G_TO_C_INTERACTABLE_STATE_CHANGE, // Interactable state 변경 알림 (사보타주 등)

        // 인게임 인벤토리 프로토콜 (게임 내 배낭 - 게임 종료 시 초기화)
        G_TO_C_INGAME_INVENTORY_LIST, // 게임 시작 시 배낭 전체 목록
        G_TO_C_INGAME_INVENTORY_UPDATE, // 아이템 추가/제거 시 업데이트
        C_TO_G_USE_INGAME_ITEM, // 게임 아이템 사용 요청
        G_TO_C_USE_INGAME_ITEM_RESULT, // 아이템 사용 결과

        // 플레이어 상태 프로토콜
        C_TO_G_PLAYER_STATE, // 플레이어 상태 변경 요청 (MAKE 등)
        G_TO_C_PLAYER_STATE, // 플레이어 상태 브로드캐스트 (애니메이션 동기화)

        // 플레이어 스탯 프로토콜
        G_TO_C_PLAYER_STATS_UPDATE, // 스태미나/정신력 등 스탯 변경 알림

        // 탈출 절차 프로토콜
        G_TO_C_EXIT_STEP_INFO, // 탈출 절차 정보 (게임 접속 시 자동 전송)
        C_TO_G_EXIT_ADVANCE, // 탈출 절차 다음 단계 진행 요청
        G_TO_C_EXIT_ADVANCE_RESULT, // 탈출 절차 진행 결과 (성공/실패/탈출완료)
        G_TO_C_EXIT_STEP_UPDATE, // 탈출 절차 단계 변경 브로드캐스트 (다른 플레이어가 진행시켜도 모두에게 알림)

        // 로비 복귀 프로토콜
        C_TO_G_RETURN_TO_LOBBY, // 로비 복귀 요청 (게임 완료 후)
        G_TO_C_RETURN_TO_LOBBY_RESULT, // 로비 복귀 결과

        // 문 프로토콜
        C_TO_G_DOOR_OPEN_REQUEST, // 문 열기 요청
        G_TO_C_DOOR_STATE_UPDATE, // 문 상태 변경 브로드캐스트
        G_TO_C_DOOR_STATE_LIST, // 입장 시 열린 문 목록

        // 복도 규칙 프로토콜
        G_TO_C_CORRIDOR_BELL, // 복도 종소리 이벤트

        // 플레이어 상호작용 프로토콜
        C_TO_G_PLAYER_INTERACT_REQUEST,   // A→서버: 상호작용 요청
        G_TO_C_PLAYER_INTERACT_REQUEST,   // 서버→A,B: 요청 결과/알림
        C_TO_G_PLAYER_INTERACT_RESPONSE,  // B→서버: 수락/거절
        G_TO_C_PLAYER_INTERACT_RESULT,    // 서버→A,B: 최종 결과
        C_TO_G_PLAYER_INTERACT_END,       // 대화 종료 요청
        G_TO_C_PLAYER_INTERACT_END,       // 대화 종료 알림
        C_TO_G_PLAYER_INTERACT_USE_ITEM,      // 상호작용 중 아이템 사용 요청
        G_TO_C_PLAYER_INTERACT_USE_ITEM_RESULT, // 상호작용 중 아이템 사용 결과

        END
    }

    public enum ErrorCode
    {
        // 공통 (0~99)
        SUCCESS = 0,
        UNKNOWN_ERROR = 1,
        INVALID_REQUEST = 2,
        SERVER_INTERNAL_ERROR = 3,
        TIMEOUT = 4,
        NOT_IMPLEMENTED = 5,

        // 인증/세션 (100~199)
        AUTH_FAILED = 100,
        SESSION_EXPIRED = 101,
        SESSION_NOT_FOUND = 102,
        PLAYER_NOT_FOUND = 103,
        ALREADY_CONNECTED = 104,
        INVALID_PLAYER_ID = 105,

        // 매칭 (200~299)
        MATCHING_ALREADY_IN_QUEUE = 200,
        MATCHING_NOT_IN_QUEUE = 201,
        MATCHING_FAILED = 202,
        MATCHING_TIMEOUT = 203,
        MATCHING_CANCELLED = 204,
        MATCHING_INVALID_MAP = 205,

        // 게임플레이 (300~399)
        GAME_NOT_STARTED = 300,
        GAME_ALREADY_ENDED = 301,
        INVALID_POSITION = 302,
        INVALID_AREA = 303,
        PLAYER_DEAD = 304,
        ACTION_COOLDOWN = 305,
        INVALID_GAME_STATE = 306,

        // 상호작용/탐색 (400~499)
        INTERACTABLE_NOT_FOUND = 400,
        INTERACTABLE_NOT_AVAILABLE = 401,
        INTERACTABLE_ALREADY_USED = 402,
        EXPLORE_ALREADY_IN_PROGRESS = 403,
        EXPLORE_NOT_IN_PROGRESS = 404,
        INVALID_SELECTION = 405,
        ACTION_NOT_FOUND = 406,
        ACTION_ALREADY_EXPLORED = 407,
        AREA_MISMATCH = 408,

        // 아이템/인벤토리 (500~599)
        ITEM_NOT_FOUND = 500,
        ITEM_NOT_OWNED = 501,
        ITEM_NOT_USABLE = 502,
        ITEM_ALREADY_USED = 503,
        INVENTORY_FULL = 504,
        INSUFFICIENT_CURRENCY = 505,
        INVALID_ITEM = 506,
        INVALID_ITEM_TYPE = 507,
        REQUIRED_ITEM_MISSING = 508,  // 액션 수행에 필요한 아이템 미보유
        REQUIRED_ACTION_NOT_COMPLETED = 509,  // 선행 액션 미완료
        INSUFFICIENT_STAMINA = 510,  // 스태미나 부족

        // 탈출 의식 (600~699)
        EXIT_NOT_AVAILABLE = 600,
        EXIT_CONDITION_NOT_MET = 601,
        EXIT_STEP_INVALID = 602,
        EXIT_ITEM_MISSING = 603,
        EXIT_ALREADY_COMPLETED = 604,
        EXIT_SPOT_MISMATCH = 605,

        // 문 (650~659)
        DOOR_NOT_FOUND = 650,
        DOOR_ALREADY_OPEN = 651,
        DOOR_KEY_MISSING = 652,
        DOOR_TOO_FAR = 653,

        // 퀘스트 (700~799)
        QUEST_NOT_FOUND = 700,
        QUEST_ALREADY_COMPLETED = 701,
        QUEST_CONDITION_NOT_MET = 702,

        // 메일 (800~899)
        MAIL_NOT_FOUND = 800,
        MAIL_ALREADY_RECEIVED = 801,
        MAIL_EXPIRED = 802,

        // 레거시 호환 (900~)
        ALREADY_HAS_JOB = 900,
        ALREADY_ANOTHER_USE_SKILL = 901,
        FATAL = 999
    }
}
