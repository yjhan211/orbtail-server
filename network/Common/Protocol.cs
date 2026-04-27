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
        C_TO_G_PLAYER_INTERACT_SHARE_RULE,        // 상호작용 중 수칙 공유 요청
        G_TO_C_PLAYER_INTERACT_SHARE_RULE_RESULT,  // 상호작용 중 수칙 공유 결과

        // 미션 프로토콜
        G_TO_C_MISSION_INFO,          // 게임 시작 시 미션 정보 전달
        G_TO_C_MISSION_STEP_COMPLETE, // 미션 단계 완료 알림
        G_TO_C_MISSION_ALL_COMPLETE,  // 미션 전체 완료 알림

        // 구역 폐쇄 프로토콜
        G_TO_C_AREA_CLOSURE_WARNING,  // 폐쇄 30초 전 경고
        G_TO_C_AREA_CLOSED,           // 구역 폐쇄 확정

        // 타겟 위치 추적 프로토콜
        G_TO_C_TARGET_LOCATION,       // 마니또 → 타겟 구역 위치

        // 흔적 프로토콜
        G_TO_C_TRACE_CREATED,         // 흔적 생성 알림
        G_TO_C_TRACE_LIST,            // 현재 구역 흔적 목록

        // 색출 프로토콜
        C_TO_G_DETECT_MANITTO,        // 마니또 지목 요청
        G_TO_C_DETECT_RESULT,         // 지목 결과 (성공/실패)

        // 탈락 & 체인 프로토콜
        G_TO_C_PLAYER_ELIMINATED,     // 플레이어 탈락 알림 (전체 브로드캐스트)
        G_TO_C_CHAIN_BREAK,           // 체인 단절 → 시한부/해방 상태 알림
        G_TO_C_PLAYER_STATUS_CHANGE,  // 플레이어 상태 변경 (시한부, 해방 등)

        // 마니또 전용: 흔적 배치 프로토콜
        C_TO_G_PLACE_TRACE,           // 흔적 배치 요청
        G_TO_C_PLACE_TRACE_RESULT,    // 흔적 배치 결과

        // 1:1 상호작용 선택지 프로토콜 (마니또)
        G_TO_C_INTERACTION_CHOICES,   // 대화 수락 시 질문/답변 선택지 전송
        C_TO_G_INTERACTION_ASK,       // 질문자: 질문 선택
        G_TO_C_INTERACTION_ANSWER_CHOICES, // 답변자: 답변 선택지 전송
        C_TO_G_INTERACTION_ANSWER,    // 답변자: 답변 선택
        G_TO_C_INTERACTION_RESULT,    // 양쪽: 상호작용 결과 (주장 직책, 로그 기록 등)

        // 시한부 사보타주 프로토콜
        C_TO_G_SABOTAGE_MISSION,      // 시한부: 미션 오브젝트 훼손 요청
        G_TO_C_SABOTAGE_RESULT,       // 훼손 결과 (성공/실패)
        G_TO_C_MISSION_REDIRECTED,    // 미션 목적지 재설정 알림 (훼손 피해자)
        G_TO_C_SABOTAGE_TARGET_EXPOSED, // 4B: 사보타주 발동 시 ▓▓ 위치 5초 공개 (전체 브로드캐스트, 패키지 Y #24)

        // 게임 결과 프로토콜
        G_TO_C_GAME_RESULT,           // 게임 종료 시 전체 결과 (체인 공개)

        // 구역 이동 프로토콜 (GDD v0.0.8: 문/계단 마커 방식)
        C_TO_G_AREA_MOVE,             // 문/계단 마커 클릭 → 구역 이동 요청
        G_TO_C_AREA_MOVE_RESULT,      // 이동 결과 (목적지 구역 + 스폰 셀 + 비용)

        // 범용 에러 프로토콜
        U_TO_C_ERROR, // UserServer 범용 에러 응답
        G_TO_C_ERROR, // GameServer 범용 에러 응답

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

        // 미션 (750~799)
        MISSION_NOT_AVAILABLE = 750,
        MISSION_ALREADY_COMPLETED = 751,
        MISSION_AREA_CLOSED = 752,

        // 색출 (850~899)
        DETECT_ALREADY_USED = 850,
        DETECT_TARGET_NOT_FOUND = 851,
        DETECT_NOT_AVAILABLE = 852,  // 최종 2인 등 비활성 상황

        // 사보타주 (860~869)
        SABOTAGE_NOT_TERMINAL = 860,       // 시한부가 아님
        SABOTAGE_INVALID_TARGET = 861,     // 훼손 대상 없음
        SABOTAGE_AREA_MISMATCH = 862,      // 해당 구역에 없음

        // 레거시 호환 (900~)
        ALREADY_HAS_JOB = 900,
        ALREADY_ANOTHER_USE_SKILL = 901,
        FATAL = 999
    }
}
