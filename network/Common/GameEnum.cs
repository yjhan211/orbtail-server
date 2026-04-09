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

    /// <summary>
    ///     마니또 게임 직책 (5인 매칭 시 무작위 배정)
    /// </summary>
    public enum JobTitle : short
    {
        NONE = 0,
        HEALTH_COMMITTEE = 1,   // 보건위원
        BROADCAST_MEMBER = 2,   // 방송부원
        DISCIPLINE_MEMBER = 3,  // 선도부원
        LIBRARY_COMMITTEE = 4,  // 도서위원
        SPORTS_CAPTAIN = 5,     // 체육부장
    }

    /// <summary>
    ///     탈락 사유
    /// </summary>
    public enum EliminationReason : short
    {
        NONE = 0,
        DETECTED = 1,           // 색출당함
        MENTAL_ZERO = 2,        // 정신력 0
        STAMINA_ZERO = 3,       // 스태미나 0
    }

    /// <summary>
    ///     마니또 체인 내 플레이어 상태
    /// </summary>
    public enum ManittoStatus : short
    {
        ACTIVE = 0,             // 정상 활동
        ELIMINATED = 1,         // 탈락
        TERMINAL = 2,           // 시한부 (타겟이 탈락하여 마니또 역할 상실)
        FREED = 3,              // 해방 (마니또가 탈락하여 스토커 없음)
        SPECTATING = 4,         // 관전
    }

    /// <summary>
    /// 상호작용 오브젝트 탐색 타입
    /// </summary>
    public enum InteractionType : short
    {
        NONE = 0,
        EXPLORE = 1,    // 전체탐색 - 필수 아이템 획득 전까지 창 유지
        SINGLE = 2,     // 단일선택 - 결과 출력 후 창 즉시 닫힘
        WRITING = 3,    // 과거와의 필담
        SABOTAGE = 4,      // 사보타주
        VENT = 5,       // 벤트
        MEETING = 6,    // 회의 소집
    }

    /// <summary>
    /// 상호작용 액션 결과 타입
    /// </summary>
    public enum ActionResultType : short
    {
        NONE = 0,
        REWARD_POOL = 1,           // 아이템 획득
        DEBUFF_CORRUPTION = 2,     // 정신오염도 증가
        DEBUFF_STAMINA = 3,        // 스태미나 감소
        BUFF_CORRUPTION = 4,       // 정신오염도 감소
        BUFF_STAMINA = 5,          // 스태미나 증가
    }

    /// <summary>
    /// 상호작용 액션 상태 타입 (사보타주 등 동적 상태 변경)
    /// </summary>
    public enum InteractableStateType : short
    {
        DEFAULT = 0,        // 기본 상태
        SABOTAGE = 1,       // 사보타주 상태 (전화벨 울림 등)
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

    /// <summary>
    /// 정신오염 크리피 텍스트 타입
    /// </summary>
    public enum CreepyType : short
    {
        NONE = 0,
        DEFAULT = 1,        // 기본 크리피 텍스트
        MAP = 2,            // 장소 크리피 텍스트
        PLAYER_TITLE = 3,   // 플레이어 칭호 크리피 텍스트
        TIMER = 4,          // 타이머 크리피 텍스트
        PROGRESS = 5,       // 진행 상태 크리피 텍스트
    }
}
