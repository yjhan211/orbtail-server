// ReSharper disable All
namespace network.common
{
    public enum MapId
    {
        None = 0,
        School,
        Camp,
        // #272 8인 4세트 신맵 (2026-08-27): 구역 데이터는 map_region_school2.csv 별도 파일 —
        // 씬 내보내기가 기존 School 행을 덮지 않게 분리한다.
        School2,
    }

    /// <summary>
    ///     구역 식별자. 구 School 맵 값(1~42)과 Camp(100)는 #310에서 제거 — S2 블록만 남는다.
    /// </summary>
    public enum AreaType
    {
        None = 0,

        // #272 School2 (8인 4세트 신맵, 2026-08-27) — 50번대 블록. 기존 School 이름과 겹쳐
        // S2 접두어를 쓴다 (층×10 규약은 이 맵에 해당 없음). 씬 Structure 루트 이름과 1:1.
        // 시작방 8
        S2Classroom1 = 50,
        S2Classroom2 = 51,
        S2ExamRoom = 52,
        S2BroadcastRoom = 53,
        S2Storage = 54,
        S2NurseOffice = 55,
        S2AdminOffice1 = 56,
        S2AdminOffice2 = 57,
        // 합류 구역 4
        S2Library1 = 60,
        S2Library2 = 61,
        S2Gym1 = 62,
        S2Gym2 = 63,
        // 복도 8: 시작방↔합류 1:1 연결 (2026-08-27 유저 정의)
        // 1=교실1↔도서관1, 2=교실2↔도서관1, 3=고사실↔강당1, 4=방송실↔강당1,
        // 5=창고↔도서관2, 6=행정실2↔도서관2, 7=행정실1↔강당2, 8=보건실↔강당2
        S2Corridor1 = 64,
        S2Corridor2 = 65,
        S2Corridor3 = 66,
        S2Corridor4 = 67,
        S2Corridor5 = 68,
        S2Corridor6 = 69,
        S2Corridor7 = 70,
        S2Corridor8 = 71,
        // 1차 연결 통로 (씬 Corridor9)
        S2Corridor9 = 72,
        // 테라스 링 · 중앙 운동장
        S2Terrace = 73,
        S2Ground = 74,
    }

    public static class AreaTypeExtensions
    {
        /// <summary>복도 구역인지 확인한다.</summary>
        // S2Corridor9는 #272 가운데 병합(테라스·운동장 흡수) 후 광장 정체성이라 복도가 아니다.
        public static bool IsCorridor(this AreaType area) =>
            area >= AreaType.S2Corridor1 && area <= AreaType.S2Corridor8;

        /// <summary>구역의 10번대 블록 번호를 반환한다 (S2: 시작방 5, 합류·복도 6, 광장 7). None이면 -1이다.</summary>
        public static int GetFloor(this AreaType area) =>
            area == AreaType.None ? -1 : (int)area / 10;
    }

    /// <summary>
    ///     상호작용 오브젝트의 공통 분류. interactable_info.csv의 object_type과 같은 값을 쓴다.
    /// </summary>
    public enum InteractableObjectType
    {
        None = 0,
        Chalkboard = 1,
        TeacherDesk = 2,
        StudentDesk = 3,
        OfficeDesk = 4,
        Locker = 5,
        Cabinet = 6,
        Bookshelf = 7,
        Computer = 8,
        Printer = 9,
        SportsGear = 10,
        Stage = 11,
        Misc = 12,
    }

    public enum SocialActionType
    {
        NONE = 0,
        SITGROUND,
        LAUGH,
        THUMBSUP,
    }

    public enum BuffType
    {
        NONE = 0,
        INSTANT,
        PERIODIC,
        PASSIVE = 3,
    }

    public enum BuffSubType
    {
        NONE = 0,
        HEALTH_ADD = 2,
        DURABILITY_ADD,
        HEALTH_DOWN,
        ITEM_GAIN_CHANCE_ADD = 5,
        RISK_EVENT_CHANCE_DOWN = 6,
        RECOVERY_ITEM_EFFECT_ADD = 7,
        ENCOUNTER_ESCAPE_CHANCE_ADD = 8,
        ISOLATION_DAMAGE_DOWN = 9,
        UNFAVORABLE_SUCCESS_CHANCE_ADD = 10,
    }

    public enum ItemType
    {
        NONE = 0,
        EQUIPMENT,
        CONSUMABLE,
        MATERIAL,
        INSTALLATION,
        PUTABLE,
        // 값 6은 레거시 예약 번호라 enum 멤버로 정의하지 않는다.
        PART_BODY = 7,   // v0.2.0 부품 본체. ItemId = 700000000 + PartId
        PART_CHARGE = 8, // v0.2.0 부품 충전재. ItemId = 800000000 + PartId
        PART_GIFT = 9,   // v0.2.0 충전 완료 선물. ItemId = 900000000 + PartId
    }

    public enum GiftState
    {
        None = 0,
        Prepared = 1,
        Received = 2
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

    // Object type
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

    public enum PlayerState : short
    {
        NONE = 0,
        IDLE,
        SITGROUND,
        SITCHAIR,
        EXPLORE_1,
        MAKE,
        SLEEP,

        // #229 7단계: 성장(제작·강화) 자세. 애니메이터 character_basic_Controller의 tool 상태와
        // 짝이다 — 값을 바꾸면 컨트롤러 전이 조건(PlayerState Equals 7)도 함께 고쳐야 한다.
        TOOL,
    }

    /// <summary>플레이어 탈락 사유.</summary>
    public enum EliminationReason : short
    {
        NONE = 0,
        DETECTED = 1,
        HEALTH_ZERO = 2,
        RACE_LOST = 4,
        SETTLEMENT_LOW_CONTRIBUTION = 5,
    }

    public enum PlayerMatchStatus : short
    {
        ACTIVE = 0,
        ELIMINATED = 1,
        SPECTATING = 4,
    }

    /// <summary>상호작용 오브젝트 탐색 유형.</summary>
    public enum InteractionType : short
    {
        NONE = 0,
        EXPLORE = 1,
        SINGLE = 2,
        WRITING = 3,
        VENT = 5,
        MEETING = 6,
        RNG_COLLECT = 7,
    }

    /// <summary>상호작용 행동 결과 유형.</summary>
    public enum ActionResultType : short
    {
        NONE = 0,
        REWARD_POOL = 1,
        DEBUFF_HEALTH = 2,
        BUFF_HEALTH = 4,
    }

    /// <summary>시스템 텍스트 카테고리.</summary>
    public enum SystemTextCategory : short
    {
        NONE = 0,
        PORTAL = 1,
        ALERT = 2,
        EXIT_STEP = 3,
        INTERACTION = 4,
        JOB_TITLE = 5,
        INTERROGATION = 6,
        UI = 7,
    }
}
