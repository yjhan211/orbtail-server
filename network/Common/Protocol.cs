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
        C_TO_U_CHAT_MSG,
        U_TO_C_CHAT_MSG,
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
        C_TO_U_OBJECT_INFO,
        C_TO_U_GET_JOB,
        U_TO_C_GET_JOB,
        C_TO_U_WEAR_ITEM,
        U_TO_C_WEAR_ITEM,
        U_TO_G_LOGOUT,
        END
    }

    public enum ObjectType : int
    {
        NONE,
        PLAYER,
        ITEM
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
        GEOIOGIST,
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
        CHIEF // 최고 연구원
    }

    public enum PlayerGrade : byte
    {
        NONE,
        RUFFIAN, // 불량배
        LAW_BREAKER, // 위법시민
        COMMONER, // 시민
        LAW_ABIDING, // 준법시민
        RIGHTEOUS_PERSON, // 의인
        PHILANTHROPIST, // 박애주의자
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
        FATAL
    }
}
