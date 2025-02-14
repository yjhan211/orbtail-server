// ReSharper disable All
namespace network.common
{
    public enum MapId
    {
        None = 0,
        TutorialLibrary,
        TutorialSchool1,
        TutorialSchool2,
        TutorialClassroom,
        TutorialAdminoffice,
        TutorialGym,
        TutorialGymstorage,
        TutorialSchoolground,
        Library,
        School1,
        School2,
        Classroom,
        Adminoffice,
        Gym,
        Gymstorage,
        Schoolground,
        Camp,
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
        CRAFT_ADD,
    }

    public enum ItemType
    {
        NONE = 0,
        EQUIPMENT,
        CONSUMABLE,
        MATERIAL,
        INSTALLATION,
        MANUAL,
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
        PASSIVE = 108,
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
        CAMP,
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
        BOTTOM_RIGHT
    }

    public enum JobType : byte
    {
        NONE,
        ENGINEER, // 공학자
        CHEMIST // 화학자
    }

    public enum JobGrade : byte
    {
        NONE,
        TRAINEE, // 수습 연구원
        RESEARCHER, // 연구원
        ASSOCIATE, // 주임 연구원
        SENIOR_ASSOCIATE, // 선임 연구원
        PRINCIPAL, // 책임 연구원
        LEAD, // 수석 연구원
        CHIEF // 대가
    }

    public enum LabGrade : byte
    {
        NONE,
        ALONE, // 개인 동아리
        CLUB // 동아리
    }

    public enum PlayerGrade : byte
    {
        NONE,
        RUFFIAN, // 불량배
        LAW_BREAKER, // 위법시민
        COMMONER, // 시민
        LAW_ABIDING, // 준법시민
        RIGHTEOUS_PERSON, // 의인
        HERO, // 영웅
        SAINT // 성자
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
        CRAFT_1,
        CAMPING_1,
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
}