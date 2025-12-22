// ReSharper disable All
namespace network.common
{
    public enum MapId
    {
        None = 0,
        School,
        Camp,
    }

    public enum AreaType
    {
        None = 0,
        Library = 1,
        Classroom1 = 2, // 고사실
        Classroom2 = 3, // 방송실
        Classroom3 = 4, // 보건실
        Classroom4 = 5, // 3-1
        Classroom5 = 6, // 3-2
        Corridor = 7,
        Storage1 = 8,
        Storage2 = 9,
        AdminOffice1 = 10,
        AdminOffice2 = 11,
        Gym = 12,
        Ground = 13,
        Terrace1 = 14,
        Terrace2 = 15,
    }

    public enum SocialActionType
    {
        NONE = 0,
        SITGROUND,
        LAUGH,
        THUMBSUP,
    }

    public enum BoostType
    {
        NONE = 0,
        SPEED = 1
    }

    public enum BuffType
    {
        NONE = 0,
        INSTANT,
        PERIODIC,
    }

    public enum BuffSubType
    {
        NONE = 0,
        CONDITION_ADD,
        CORRUPTION_DOWN,
    }

    public enum ItemType
    {
        NONE = 0,
        EQUIPMENT,
        CONSUMABLE,
        MATERIAL,
        INSTALLATION,
        PUTABLE,
    }

    public enum EquipType
    {
        NONE = 0,
        HEAD = 101,
        FACE = 102,
        HAT = 103,
        TOP = 104,
        BOTTOM = 105,
        SHOES = 106,
        TOOL = 107,
        PILLOW = 108,
        BEDDING = 109,
    }

    public enum InstallationType
    {
        NONE = 0,
        TENT,
        SHOP
    }

    // 주의사항: _ 붙이지 말 것
    public enum ObjectType
    {
        NONE,
        PLAYER,
        ITEM,
        EXPLORETARGET,
        INTERACTPROP,
    }

    public enum InventoryOwnerType
    {
        NONE,
        PLAYER,
        LAB
    }

    public enum DirectionType : byte
    {
        NONE,
        TOP_LEFT,
        TOP_RIGHT,
        BOTTOM_LEFT,
        BOTTOM_RIGHT,
        TOP,
        LEFT,
        RIGHT,
        BOTTOM,
    }

    public enum LabGrade : byte
    {
        NONE,
        ALONE, // 개인 동아리
        CLUB // 동아리
    }

    public enum ChatType : byte
    {
        ALL, // 전체
        NORMAL, // 지역
        GUILD // 연구소
    }

    public enum LoginType
    {
        GUEST
    }

    public enum PlayerState : short
    {
        NONE = 0,
        IDLE,
        SITGROUND,
        SITCHAIR,
        EXPLORE_1,
        MAKE,
        SLEEP,
    }

    public enum QuestType : short
    {
        NONE = 0,
        MAIN,
        SUB
    }

    public enum QuestState : short
    {
        NONE = 0,
        SUCCESS,
        END
    }

    public enum MailState : short
    {
        NONE = 0,
        REWARDED = 1,
    }

    public enum DamageType : short
    {
        NONE = 0,
        DARK,
    }

    public enum RewardType : short
    {
        NONE = 0,
        ITEM,
        CONDITION_RANDOM,
        CORRUPTION_RANDOM,
        RULE,
        RULE_RANDOM,
    }

    public enum ExitTemplateType : short
    {
        NONE = 0,
        BROADCAST = 1,  // 비상 방송 프로토콜
        DISPOSE = 2,    // 특수 개체 격리 및 처리 절차
    }

    // ExitItemType은 제거됨 - exit_scenario.csv에서 item_id를 직접 참조

    public enum ExitSpotType : short
    {
        NONE = 0,
        BROADCAST = 1,      // 방송실 송출 장비
        INCINERATOR = 2,    // 쓰레기장 소각로
        GUTTER = 3,         // 쓰레기장 빗물받이
        SAFE = 4,           // 교무실 금고
        PROJECTOR = 5,      // 시청각실 영사기
    }

    public enum ExitDebuffType : short
    {
        NONE = 0,
        SONG = 1,       // 노랫소리
        WHISPER = 2,    // 속삭임
        HEAVY = 3,      // 무거움
        SHADOW = 4,     // 그림자
    }

    public enum ExitConditionType : short
    {
        NONE = 0,
        SANITY = 1,     // 정신이 온전한 인원
        SOLO = 2,       // 혼자
        TOGETHER = 3,   // 두 명이 동시에
    }

    public enum ExitActionType : short
    {
        NONE = 0,
        GET = 1,        // 아이템 획득
        CARRY = 2,      // 운반
        USE = 3,        // 사용
        ASSIGN = 4,     // 운반자 지정
        FINALE = 5,     // 최종 행동
    }

    public enum ExitConstraintType : short
    {
        NONE = 0,
        POOL = 1,       // 허용 목록
        REQUIRE = 2,    // 필수 조합
        EXCLUDE = 3,    // 금지 조합
    }

    public enum ExitSlotType : short
    {
        NONE = 0,
        ITEM = 1,
        SPOT = 2,
        DEBUFF = 3,
        CONDITION = 4,
    }

    public enum ExitCheckType : short
    {
        NONE = 0,
        SANITY_HIGH = 1,        // 정신력 확인
        NEARBY_PLAYERS = 2,     // 근처 플레이어 수 확인
    }

    public enum ExitFinaleType : short
    {
        NONE = 0,
        TYPING = 1,         // 타이핑 미니게임
        BUTTON_MASH = 2,    // 버튼 연타
        SYNC_TOUCH = 3,     // 동시 터치
    }

    public enum ExitEffectType : short
    {
        NONE = 0,
        AUDIO_HUMMING = 1,      // 흥얼거림 오디오
        AUDIO_WHISPER = 2,      // 속삭임 오디오
        MOVE_SPEED_DOWN = 3,    // 이동속도 감소
        VISUAL_DARKEN = 4,      // 시야 어두워짐
    }

    /// <summary>
    /// 상호작용 오브젝트 탐색 타입
    /// </summary>
    public enum InteractionType : short
    {
        NONE = 0,
        EXPLORE = 1,    // 전체탐색 - 필수 아이템 획득 전까지 창 유지
        SINGLE = 2,     // 단일선택 - 결과 출력 후 창 즉시 닫힘
    }

    /// <summary>
    /// 상호작용 액션 결과 타입
    /// </summary>
    public enum ActionResultType : short
    {
        NONE = 0,
        ITEM = 1,           // 아이템 획득
        RULE = 2,           // 규칙 쪽지 획득
        CORRUPTION = 3,     // 정신오염도 변화
        STAMINA = 4,        // 스태미나 변화
        PORTAL = 5,         // 포탈 활성화
    }

    /// <summary>
    /// 시스템 텍스트 카테고리
    /// </summary>
    public enum SystemTextCategory : short
    {
        NONE = 0,
        PORTAL = 1,         // 포탈 관련 메시지
        ALERT = 2,          // 알럿 메시지
        EXIT_STEP = 3,      // 탈출 절차 관련
        INTERACTION = 4,    // 상호작용 관련
    }
}