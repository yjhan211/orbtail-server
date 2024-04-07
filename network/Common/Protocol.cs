using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace network
{
    public enum PROTOCOL : int
    {
        HEART_BEAT = 0,
        C_TO_U_LOGIN,
        U_TO_C_LOGIN,
        U_TO_C_INVENTORY_ITEM_LIST,
        C_TO_U_CHAT_MSG,
        U_TO_C_CHAT_MSG,
        C_TO_U_CHAT_LOG,
        C_TO_U_MOVE,
        U_TO_G_MOVE,
        G_TO_U_MOVE,
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
        C_TO_U_JOB_RESOURCE_INFO,
        U_TO_C_JOB_RESOURCE_INFO,
        G_TO_U_JOB_RESOURCE_INFO,
        C_TO_U_USE_SKILL,
        U_TO_C_USE_SKILL,
        U_TO_G_USE_SKILL,
        U_TO_C_USE_SKILL_COMPLETE,
        C_TO_U_GET_JOB,
        U_TO_C_GET_JOB,
        C_TO_U_UPGRADE_JOB,
        U_TO_C_UPGRADE_JOB,
        C_TO_U_WEAR_ITEM,
        U_TO_C_WEAR_ITEM,
        C_TO_U_USE_ITEM,
        U_TO_C_USE_ITEM,
        U_TO_C_CHANGE_MAP,
        C_TO_U_CHANGE_MAP_SUCCESS,
        U_TO_G_LOGOUT,
        END
    }

    public enum MapID : int
    {
        NONE,
        CITY_1,
        FOREST_1,
    }

    public enum ObjectType : int
    {
        NONE,
        PLAYER,
        ITEM,
        JOBRESOURCE,
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
        GEOIOGIST, // 지질학자
        BOTANIST, // 식물학자
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
        FATAL
    }

    public enum PlayerState : short
    {
        NONE = 0,
        GEO_WORK_1,
        BOTAN_WORK_1,
        RESEARCH_1,
    }
}
