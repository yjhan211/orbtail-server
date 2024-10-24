namespace network.common
{
    public enum PROTOCOL : int
    {
        C_TO_U_HEART_BEAT = 0,
        U_TO_C_HEART_BEAT,
        C_TO_U_LOGIN,
        U_TO_C_LOGIN,
        C_TO_U_SET_NAME,
        U_TO_C_SET_NAME,
        U_TO_C_INVENTORY_ITEM_LIST,
        C_TO_U_CHAT_MSG,
        U_TO_C_CHAT_MSG,
        C_TO_U_CHAT_LOG,
        C_TO_U_MOVE,
        U_TO_G_MOVE,
        G_TO_U_UPDATE_OBJECT,
        U_TO_C_MOVE,
        G_TO_U_SPAWN,
        U_TO_C_SPAWN,
        G_TO_U_DESTROY,
        U_TO_C_DESTROY,
        U_TO_C_MAP_UPDATE,
        C_TO_U_PLAYER_INFO,
        U_TO_C_PLAYER_INFO,
        G_TO_U_PLAYER_INFO,
        C_TO_U_OBJECT_INFO,
        C_TO_U_EXPLORE_TARGET_INFO,
        U_TO_C_EXPLORE_TARGET_INFO,
        G_TO_U_EXPLORE_TARGET_INFO,
        C_TO_U_JOB_RESOURCE_INFO,
        U_TO_C_JOB_RESOURCE_INFO,
        G_TO_U_JOB_RESOURCE_INFO,
        C_TO_U_USE_SKILL,
        U_TO_C_USE_SKILL,
        U_TO_G_USE_SKILL,
        U_TO_C_USE_SKILL_COMPLETE,
        C_TO_U_EXPLORE,
        U_TO_C_EXPLORE,
        U_TO_C_EXPLORE_COMPLETE,
        C_TO_U_UPGRADE_JOB,
        U_TO_C_UPGRADE_JOB,
        C_TO_U_WEAR_ITEM,
        U_TO_C_WEAR_ITEM,
        C_TO_U_USE_ITEM,
        U_TO_C_USE_ITEM,
        U_TO_C_CHANGE_MAP,
        C_TO_U_CHANGE_MAP_SUCCESS,
        C_TO_U_CREATE_LAB,
        U_TO_C_CREATE_LAB,
        G_TO_U_CREATE_INSTANCE_SUCCESS,
        C_TO_U_UPGRADE_RESEARCH,
        U_TO_C_UPGRADE_RESEARCH,
        C_TO_U_MAKE,
        U_TO_C_MAKE,
        C_TO_U_WRITE_LAB_HIRE,
        U_TO_C_WRITE_LAB_HIRE,
        C_TO_U_LAB_HIRE_LIST,
        U_TO_C_LAB_HIRE_LIST,
        C_TO_U_JOIN_LAB,
        U_TO_C_LAB_INFO,
        C_TO_U_LAB_INVENTORY,
        C_TO_U_LAB_INVENTORY_ADD_ITEM,
        C_TO_U_LAB_INVENTORY_TAKE_ITEM,
        U_TO_U_LAB_INVENTORY,
        U_TO_C_LAB_INVENTORY,
        C_TO_U_ENCAMP,
        C_TO_U_DECAMP,
        G_TO_U_CAMP_INFO,
        U_TO_C_CAMP_INFO,
        C_TO_U_CAMP_INFO,
        U_TO_C_UPDATE_HP,
        C_TO_U_ADD_SELL_ITEM,
        U_TO_C_ADD_SELL_ITEM,
        C_TO_U_DELETE_SELL_ITEM,
        U_TO_C_DELETE_SELL_ITEM,
        C_TO_U_BUY_ITEM,
        U_TO_C_BUY_ITEM,
        U_TO_U_PLAYER_INFO,
        U_TO_U_DUPLICATE,
        U_TO_G_LOGOUT,
        C_TO_U_UPDATE_TUTORIAL,
        U_TO_C_UPDATE_TUTORIAL,
        END
    }

    public enum MapID : int
    {
        NONE,
        CAMPUS_1,
        FACTORY_1,
        WETLAND_1,
        LAB_1,
        LIBRARY,
    }

    // 주의사항_ 붙이지 말 것
    public enum ObjectType : int
    {
        NONE,
        PLAYER,
        ITEM,
        EXPLORETARGET,
        JOBRESOURCE,
        CAMP,
    }

    public enum InventoryOwnerType : int
    {
        NONE,
        PLAYER,
        LAB,
    }

    public enum TileType : int
    {
        EMPTY,
    }

    public enum DirectionType : byte
    {
        NONE,
        TOP_LEFT,
        TOP_RIGHT,
        BOTTOM_LEFT,
        BOTTOM_RIGHT,
    }

    public enum JobType : byte
    {
        NONE,
        ENGINEER, // 공학자
        CHEMIST, // 화학자
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
        CLUB, // 동아리
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
        SAINT, // 성자
    }

    public enum ChatType : byte
    {
        ALL, // 전체
        NOMAL, // 지역
        GUILD, // 연구소
    }

    public enum LoginType : int
    {
        GUEST,
    }

    public enum ErrorCode
    {
        SUCCESS = 0,
        ALREADY_HAS_JOB,
        ALREADY_ANOTHER_USE_SKILL,
        INVALID_POSITION,
        FATAL
    }

    public enum PlayerState : short
    {
        NONE = 0,
        IDLE,
        ENGINEER_WORK_1,
        CHEMIST_WORK_1,
        EXPLORE_1,
        CAMIPING_1,
    }
}
