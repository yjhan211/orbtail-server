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
        C_TO_G_MATCH_START_READY,
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
        G_TO_C_EXPLORE_START, // 탐색 시작 브로드캐스트 (애니메이션 동기화)
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

        // 미션 프로토콜 (v0.2.0 — 부품 결합 시스템)
        G_TO_C_PART_COMBINED,         // 부품 결합 결과 (중간재 또는 최종)
        C_TO_G_COMBINE_PARTS,         // 부품 결합 요청

        // 구역 폐쇄 프로토콜
        G_TO_C_AREA_CLOSURE_WARNING,  // 폐쇄 30초 전 경고
        G_TO_C_AREA_CLOSED,           // 구역 폐쇄 확정

        // 타겟 위치 추적 프로토콜
        G_TO_C_TARGET_LOCATION,       // 마니또 → 타겟 구역 위치

        // 흔적 프로토콜

        // 색출 프로토콜
        C_TO_G_SETTLEMENT_NOMINATE,   // 정산 지목 요청
        G_TO_C_SETTLEMENT_NOMINATION_RESULT, // 정산 지목 결과
        G_TO_C_SETTLEMENT_CONTRIBUTION_RESULT, // 정산 기여도 공개 결과
        G_TO_C_CHECKLIST_INFO,        // 라운드 체크리스트 과제 목록
        C_TO_G_CHECKLIST_ACTIVITY_START, // 교내 활동 시작 요청 (RNG 보상 없이 체크리스트 진행)
        G_TO_C_CHECKLIST_ACTIVITY_ACK,   // 교내 활동 시작 승인/거부 응답
        C_TO_G_CHECKLIST_ACTIVITY_FINISH, // 교내 활동 progress 완료
        G_TO_C_CHECKLIST_ACTIVITY_RESULT, // 교내 활동 체크리스트 처리 결과

        // 탈락 & 체인 프로토콜
        G_TO_C_PLAYER_ELIMINATED,     // 플레이어 탈락 알림 (전체 브로드캐스트)
        G_TO_C_CHAIN_BREAK,           // 체인 단절 → 시한부/해방 상태 알림

        // 마니또 전용: 흔적 배치 프로토콜

        // 1:1 상호작용 선택지 프로토콜 (마니또)
        G_TO_C_INTERACTION_CHOICES,   // 대화 수락 시 질문/답변 선택지 전송
        C_TO_G_INTERACTION_ASK,       // 질문자: 질문 선택
        G_TO_C_INTERACTION_ANSWER_CHOICES, // 답변자: 답변 선택지 전송
        C_TO_G_INTERACTION_ANSWER,    // 답변자: 답변 선택
        G_TO_C_INTERACTION_RESULT,    // 양쪽: 상호작용 결과 (주장 직책, 로그 기록 등)

        // 시한부 사보타주 프로토콜

        // 게임 결과 프로토콜
        G_TO_C_GAME_RESULT,           // 게임 종료 시 전체 결과 (체인 공개)

        // 소셜 액션 프로토콜
        C_TO_G_SOCIAL_ACTION,         // 본인 소셜 액션 요청 (LAUGH, SITGROUND 등)
        G_TO_C_SOCIAL_ACTION,         // 같은 area 모든 클라에 broadcast

        // 구역 이동 프로토콜 (GDD v0.0.8: 문/계단 마커 방식)
        C_TO_G_AREA_MOVE,             // 문/계단 마커 클릭 → 구역 이동 요청
        G_TO_C_AREA_MOVE_RESULT,      // 이동 결과 (목적지 구역 + 스폰 셀 + 비용)

        // 범용 에러 프로토콜
        U_TO_C_ERROR, // UserServer 범용 에러 응답
        G_TO_C_ERROR, // GameServer 범용 에러 응답

        // RNG 채집 프로토콜 (v0.2.1, #79 — 단일 패킷, deprecated)
        G_TO_C_RNG_COLLECT_RESULT,     // RNG 결과 (부품/선행/디코이/빈손/소모품) — FINISH 응답
        G_TO_C_RNG_COLLECT_COOLDOWN_BROADCAST, // 인스턴스 단위 쿨타임 broadcast — 매칭 내 모든 클라가 마커 숨김 (#134)
        G_TO_C_INTERACT_COOLDOWN_SNAPSHOT, // 합류/리커넥트 시 현재 InteractObject 쿨타임 snapshot (#137)

        // RNG 채집 2단계 프로토콜 (#134) — START 즉시 stamina 차감 + cooldown 등록, FINISH 완료 후 결과 산출
        C_TO_G_RNG_COLLECT_START,      // 채집 시작 요청 (RippleMarker 클릭 즉시)
        G_TO_C_RNG_COLLECT_ACK,        // 채집 시작 승인/거부 응답
        C_TO_G_RNG_COLLECT_FINISH,     // progress 완료 — 결과 산출 요청

        // 비밀 선물 프로토콜 (#129)


        // 미션 그래프 선택지 프로토콜 (#143)

        // 기척 프로토콜 (프로토 0, #159)
        G_TO_C_PRESENCE_UPDATE,         // 타겟 제외 후보별 최근 25초 조우 강도(0~5) 주기 전송
        G_TO_C_PRESENCE_NOTEBOOK_UPDATE, // 학생수첩용 플레이어별 누적 조우 기록 주기 전송
        C_TO_G_BOOKMARK_PRESENCE,       // 기척 HUD 수동 북마크 상태 전송
        G_TO_C_BOOKMARK_PRESENCE_RESULT, // 기척 HUD 수동 북마크 결과
        G_TO_C_SHARP_GAZE_MARK_UPDATE,  // 내 타겟이 나를 마니또로 북마크했는지 여부

        G_TO_C_ROUND_STATE,
        G_TO_C_ENCOUNTER_REVEAL,
        C_TO_G_ROOM_ENCOUNTER_AVOID,

        // 서버 권위 바닥 아이템
        G_TO_C_GROUND_ITEM_SNAPSHOT,
        G_TO_C_GROUND_ITEM_SPAWN,
        C_TO_G_GROUND_ITEM_PICKUP,
        C_TO_G_DROP_GROUND_ITEM,
        G_TO_C_GROUND_ITEM_REMOVED,
        G_TO_C_GROUND_ITEM_PICKUP_RESULT,
        G_TO_C_MATCH_START_COUNTDOWN,

        // 자동 전투 제3자 관전용 투사체 이펙트
        G_TO_C_PROXIMITY_ATTACK_VFX,
        G_TO_C_SURVIVOR_ORB_EFFECT_STATE,
        G_TO_C_SURVIVOR_AREA_STOCK_STATE,
        G_TO_C_MONSTER_SNAPSHOT,
        G_TO_C_MONSTER_ATTACK_VFX,
        G_TO_C_SUMMON_STONE_STATE,
        C_TO_G_SUMMON_ORB,
        G_TO_C_SUMMON_ORB_RESULT,
        C_TO_G_DESTROY_ORB,
        G_TO_C_DESTROY_ORB_RESULT,

        // 잼 승점 재화 (#222 M3) — 소환석과 분리된 지갑 상태
        G_TO_C_JAM_STATE,
        G_TO_C_JAM_RANKINGS,

        // 열쇠 (#222 M4) — 무료 소환 충전 상태
        G_TO_C_FREE_SUMMON_STATE,

        // 포위 사격 연출 (#226 B) — 완성 순간 포위 링 표시
        G_TO_C_SWARM_ENCIRCLE_VFX,

        // 절단 실험 더미 조종 (#226 실험장, 개발용) — WASD 방향 전송
        C_TO_G_DEV_DUMMY_MOVE,

        // 성장 카드 3택 (#226 단계 C) — 소환석 임계 도달 시 서버가 오퍼를 내리고,
        // 선택은 서버 권위로 적용된다 (증식·강화·철갑, 강화·철갑은 선두 유효 오브 자동 적용)
        G_TO_C_SWARM_GROWTH_OFFER,
        C_TO_G_SWARM_GROWTH_PICK,
        G_TO_C_SWARM_GROWTH_RESULT,

        // 교차사격 예고 (#232 2단계) — 몬스터를 향한 오브 공격이 만드는 모양(직선 등)의
        // 원점·끝·폭·예고/판정 시간. 같은 구역 전원에게 브로드캐스트, 표시 = 판정.
        G_TO_C_SWARM_CROSSFIRE_TELEGRAPH,

        // 6칸 빌드 (#232 4단계) — 계열 공유 레벨(태양·바람·파도 T1~T3)과 직접 강화.
        // 6/6 포화는 소환 불가, 기존 5회 탭 파괴로 빈칸을 만든다.
        G_TO_C_SWARM_FAMILY_LEVELS,
        C_TO_G_SWARM_ORB_DECISION,
        G_TO_C_SWARM_ORB_DECISION_RESULT,

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
        PREREQUISITE_REQUIRED = 753,    // v0.2.0 — 부품 회수 시 선행 아이템 필요
        INVALID_PARAMETER = 754,        // v0.2.0 — 결합 레시피 매칭 실패 등
        INSUFFICIENT_ITEM = 755,        // v0.2.0 — 결합 입력 부품 미보유

        // 색출 (850~899)
        DETECT_TARGET_NOT_FOUND = 851,

        // 사보타주 (860~869)

        // 레거시 호환 (900~)
        ALREADY_HAS_JOB = 900,
        ALREADY_ANOTHER_USE_SKILL = 901,
        FATAL = 999
    }
}
