// ReSharper disable All
namespace network.common
{
    public enum MapId
    {
        None = 0,
        School,
        Camp,
    }

    /// <summary>
    ///     구역 타입. 넘버링: 층 × 10 + 순번 (층 = value / 10)
    /// </summary>
    public enum AreaType
    {
        None = 0,

        // 0층
        Junkyard = 1,           // 쓰레기장
        Ground = 2,             // 운동장

        // 1층
        AdminOffice = 10,       // 행정실
        Corridor1F = 11,        // 1층복도
        StaffRoom = 12,         // 교무실
        Gym = 13,               // 강당
        Storage = 14,           // 창고

        // 2층
        Classroom2 = 20,        // 교실2
        Corridor2F = 21,        // 2층복도
        Library = 22,           // 도서관

        // 3층
        Classroom3 = 30,        // 교실3
        Corridor3F = 31,        // 3층복도
        ExamRoom = 32,          // 고사실

        // 4층
        Classroom4 = 40,        // 교실4
        Corridor4F = 41,        // 4층복도
        BroadcastRoom = 42,     // 방송실

        // 캠프
        Camp = 100,             // 캠프
    }

    public static class AreaTypeExtensions
    {
        /// <summary>
        ///     복도 구역 여부 (1~4층복도)
        /// </summary>
        public static bool IsCorridor(this AreaType area) =>
            area is AreaType.Corridor1F or AreaType.Corridor2F or AreaType.Corridor3F or AreaType.Corridor4F;

        /// <summary>
        ///     구역의 층 번호 (0~4). None이면 -1
        /// </summary>
        public static int GetFloor(this AreaType area) =>
            area == AreaType.None ? -1 : (int)area / 10;
    }

    /// <summary>
    ///     상호작용 오브젝트 타입 — 공통 선택지 풀 매칭용 (GDD §2.4.2).
    ///     interactable_info.csv 의 object_type 컬럼이 이 enum 값을 사용한다.
    /// </summary>
    public enum InteractableObjectType
    {
        None = 0,
        Chalkboard = 1,        // 칠판
        TeacherDesk = 2,       // 교탁, 방송용 교탁
        StudentDesk = 3,       // 학생 책상, 2인용 책상
        OfficeDesk = 4,        // 사서 책상, 사무용 책상, 서랍장
        Locker = 5,            // 사물함
        Cabinet = 6,           // 캐비닛, 서류함, 보관함, 청소도구함
        Bookshelf = 7,         // 책장, 서가, 회의록류
        Computer = 8,          // 컴퓨터, 노트북, 방송장치
        Printer = 9,           // 복합기, 프린터
        SportsGear = 10,       // 농구골대, 철봉, 공바구니, 뜀틀
        Stage = 11,             // 무대
        Misc = 12,              // 가방류, 공, 시험지, 시계, 벤치 등 단일 narrative 오브젝트
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
        // 6 대역은 사물(열쇠 등) legacy — enum에 정의 X.
        PART_BODY = 7,   // v0.2.0 부품 본체. ItemId = 700000000 + PartId
        PART_CHARGE = 8, // v0.2.0 부품 충전재. ItemId = 800000000 + PartId
        PART_GIFT = 9,   // v0.2.0 충전 완료 선물. ItemId = 900000000 + PartId
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
    ///     마니또 게임 직책 (풀 8개 중 5개 선택, 각 플레이어에 1개 배정)
    /// </summary>
    public enum JobTitle : short
    {
        NONE = 0,
        BROADCAST_MEMBER = 1,   // 방송부원
        DISCIPLINE_MEMBER = 2,  // 선도부원
        LIBRARY_COMMITTEE = 3,  // 도서위원
        SPORTS_CAPTAIN = 4,     // 체육부장
        SCIENCE_MEMBER = 5,     // 과학부원
        CLEANING_MEMBER = 6,    // 미화부원
        STUDENT_PRESIDENT = 7,  // 학생회장
        HEALTH_MEMBER = 8,      // 보건부원
    }

    public static class JobTitleExtensions
    {
        public static string ToKorean(this JobTitle jobTitle) => jobTitle switch
        {
            JobTitle.BROADCAST_MEMBER => "방송부원",
            JobTitle.DISCIPLINE_MEMBER => "선도부원",
            JobTitle.LIBRARY_COMMITTEE => "도서위원",
            JobTitle.SPORTS_CAPTAIN => "체육부장",
            JobTitle.SCIENCE_MEMBER => "과학부원",
            JobTitle.CLEANING_MEMBER => "미화부원",
            JobTitle.STUDENT_PRESIDENT => "학생회장",
            JobTitle.HEALTH_MEMBER => "보건부원",
            _ => ""
        };

        /// <summary>
        ///     직책명을 현재 언어로 반환. system_text.csv 11000~11007 textId 룩업.
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
    ///     탈락 사유
    /// </summary>
    public enum EliminationReason : short
    {
        NONE = 0,
        DETECTED = 1,           // 색출당함
        MENTAL_ZERO = 2,        // 정신력 0
        STAMINA_ZERO = 3,       // 스태미나 0
        RACE_LOST = 4,          // 다른 직책의 race 완주로 패배 (#87)
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
        RNG_COLLECT = 7,    // RNG 채집 — 1.5초 자동 액션 후 RNG 풀 결과 (v0.2.1, #79)
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
        JOB_TITLE = 5,      // 직책명
        INTERROGATION = 6,  // 심문 선택지
        UI = 7,             // 고정 UI 라벨
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
