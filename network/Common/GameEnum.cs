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
    ///     援ъ뿭 ??? ?섎쾭留? 痢?횞 10 + ?쒕쾲 (痢?= value / 10)
    /// </summary>
    public enum AreaType
    {
        None = 0,
        Junkyard = 1,
        Ground = 2,
        Corridor = 7,
        Storage2 = 9,
        AdminOffice = 10,
        Corridor1F = 11,
        StaffRoom = 12,
        Gym = 13,
        Storage = 14,
        Junkyard2 = 15,
        Classroom2 = 20,
        Corridor2F = 21,
        Library = 22,
        Classroom3 = 30,
        Corridor3F = 31,
        ExamRoom = 32,
        Classroom4 = 40,
        Corridor4F = 41,
        BroadcastRoom = 42,
        Camp = 100,
    }

    public enum PersonaType
    {
        None = 0,
        SecretCollector = 1,
        Coward = 2,
        GuardianAngel = 3,
        PhysicalSolver = 4,
        Nocturnal = 5,
    }

    public static class AreaTypeExtensions
    {
        /// <summary>
        ///     蹂듬룄 援ъ뿭 ?щ? (1~4痢듬났??
        /// </summary>
        public static bool IsCorridor(this AreaType area) =>
            area is AreaType.Corridor or AreaType.Corridor1F or AreaType.Corridor2F or AreaType.Corridor3F or
                AreaType.Corridor4F;

        /// <summary>
        ///     援ъ뿭??痢?踰덊샇 (0~4). None?대㈃ -1
        /// </summary>
        public static int GetFloor(this AreaType area) =>
            area == AreaType.None ? -1 : (int)area / 10;
    }

    /// <summary>
    ///     ?곹샇?묒슜 ?ㅻ툕?앺듃 ?????怨듯넻 ?좏깮吏 ? 留ㅼ묶??(GDD 짠2.4.2).
    ///     interactable_info.csv ??object_type 而щ읆????enum 媛믪쓣 ?ъ슜?쒕떎.
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
        PASSIVE = 3,
    }

    public enum BuffSubType
    {
        NONE = 0,
        CONDITION_ADD,
        CORRUPTION_DOWN,
        DURABILITY_ADD,
        CORRUPTION_ADD,
        ITEM_GAIN_CHANCE_ADD = 5,
        RISK_EVENT_CHANCE_DOWN = 6,
        RECOVERY_ITEM_EFFECT_ADD = 7,
        ENCOUNTER_ESCAPE_CHANCE_ADD = 8,
        ISOLATION_CORRUPTION_GAIN_DOWN = 9,
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
        // 6 ???? ?щЪ(?댁뇿 ?? legacy ??enum???뺤쓽 X.
        PART_BODY = 7,   // v0.2.0 遺??蹂몄껜. ItemId = 700000000 + PartId
        PART_CHARGE = 8, // v0.2.0 遺??異⑹쟾?? ItemId = 800000000 + PartId
        PART_GIFT = 9,   // v0.2.0 異⑹쟾 ?꾨즺 ?좊Ъ. ItemId = 900000000 + PartId
    }

    public enum GiftDiscoveryType
    {
        None = 0,
        Target = 1,
        Other = 2
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

    public enum InstallationType
    {
        NONE = 0,
        TENT,
        SHOP
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
        ALONE,
        CLUB
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

        // #229 7단계: 성장(제작·강화) 자세. 애니메이터 character_basic_Controller의 tool 상태와
        // 짝이다 — 값을 바꾸면 컨트롤러 전이 조건(PlayerState Equals 7)도 함께 고쳐야 한다.
        TOOL,
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
    ///     留덈땲??寃뚯엫 吏곸콉 (? 8媛?以?5媛??좏깮, 媛??뚮젅?댁뼱??1媛?諛곗젙)
    /// </summary>
    public enum JobTitle : short
    {
        NONE = 0,
        BROADCAST_MEMBER = 1,
        DISCIPLINE_MEMBER = 2,
        LIBRARY_COMMITTEE = 3,
        SPORTS_CAPTAIN = 4,
        SCIENCE_MEMBER = 5,
        CLEANING_MEMBER = 6,
        STUDENT_PRESIDENT = 7,
        HEALTH_MEMBER = 8,
    }

    public static class JobTitleExtensions
    {
        public static string ToKorean(this JobTitle jobTitle) => jobTitle.ToString();

        /// <summary>
        ///     吏곸콉紐낆쓣 ?꾩옱 ?몄뼱濡?諛섑솚. system_text.csv 11000~11007 textId 猷⑹뾽.
        /// </summary>
        public static string ToLocalized(this JobTitle jobTitle, string lang)
        {
            int textId = jobTitle switch
            {
                JobTitle.BROADCAST_MEMBER => 11000,
                JobTitle.DISCIPLINE_MEMBER => 11001,
                JobTitle.LIBRARY_COMMITTEE => 11002,
                JobTitle.SPORTS_CAPTAIN => 11003,
                JobTitle.SCIENCE_MEMBER => 11004,
                JobTitle.CLEANING_MEMBER => 11005,
                JobTitle.STUDENT_PRESIDENT => 11006,
                JobTitle.HEALTH_MEMBER => 11007,
                _ => 0
            };
            if (textId == 0) return "";
            var data = network.common.data.GameSystemTextData.Get(textId);
            return data?.Text?.Get(lang) ?? jobTitle.ToKorean();
        }
    }

    /// <summary>
    ///     ?덈씫 ?ъ쑀
    /// </summary>
    public enum EliminationReason : short
    {
        NONE = 0,
        DETECTED = 1,           // ?됱텧?뱁븿
        MENTAL_ZERO = 2,        // ?뺤떊??0
        STAMINA_ZERO = 3,       // ?ㅽ깭誘몃굹 0
        RACE_LOST = 4,          // ?ㅻⅨ 吏곸콉??race ?꾩＜濡??⑤같 (#87)
        SETTLEMENT_LOW_CONTRIBUTION = 5, // ?뺤궛 湲곗뿬??理쒗븯?꾨줈 ?덈씫
    }

    public enum PlayerMatchStatus : short
    {
        ACTIVE = 0,
        ELIMINATED = 1,
        SPECTATING = 4,
    }

    /// <summary>
    /// ?곹샇?묒슜 ?ㅻ툕?앺듃 ?먯깋 ???    /// </summary>
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

    /// <summary>
    /// ?곹샇?묒슜 ?≪뀡 寃곌낵 ???    /// </summary>
    public enum ActionResultType : short
    {
        NONE = 0,
        REWARD_POOL = 1,           // ?꾩씠???띾뱷
        DEBUFF_CORRUPTION = 2,     // ?뺤떊?ㅼ뿼??利앷?
        DEBUFF_STAMINA = 3,        // ?ㅽ깭誘몃굹 媛먯냼
        BUFF_CORRUPTION = 4,       // ?뺤떊?ㅼ뿼??媛먯냼
        BUFF_STAMINA = 5,          // ?ㅽ깭誘몃굹 利앷?
    }

    /// <summary>
    /// ?곹샇?묒슜 ?≪뀡 ?곹깭 ???(?щ낫?二????숈쟻 ?곹깭 蹂寃?
    /// </summary>
    public enum InteractableStateType : short
    {
        DEFAULT = 0,        // 湲곕낯 ?곹깭
    }

    /// <summary>
    /// ?쒖뒪???띿뒪??移댄뀒怨좊━
    /// </summary>
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

    /// <summary>
    /// ?뺤떊?ㅼ뿼 ?щ━???띿뒪?????    /// </summary>
    public enum CreepyType : short
    {
        NONE = 0,
        DEFAULT = 1,
        MAP = 2,
        PLAYER_TITLE = 3,
        TIMER = 4,
        PROGRESS = 5,
    }
}
