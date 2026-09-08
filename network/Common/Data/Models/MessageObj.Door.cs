#pragma warning disable CS8618
// ReSharper disable All
using System;
using System.Collections.Generic;
using MessagePack;
using network.common.data.helpers;

namespace network.common.data.models
{
    // 문 열기 요청
    [MessagePackObject]
    public class C_TO_G_DOOR_OPEN_REQUEST : IMessagePackObject
    {
        [Key("doorId")] public int DoorId { get; set; }
    }

    // InteractId는 문 양쪽의 상호작용 위치를 구분한다. DoorId는 서버 데이터에서 찾는다.
    [MessagePackObject]
    public class C_TO_G_DOOR_OPEN_START : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
    }

    [MessagePackObject]
    public class C_TO_G_DOOR_OPEN_FINISH : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_DOOR_OPEN_ACK : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
        [Key("completed")] public bool Completed { get; set; }
    }

    // 문 상태 변경 브로드캐스트
    [MessagePackObject]
    public class G_TO_C_DOOR_STATE_UPDATE : IMessagePackObject
    {
        [Key("doorId")] public int DoorId { get; set; }
        [Key("isOpen")] public bool IsOpen { get; set; }
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; } // 실패 시 에러코드
        [Key("openerPlayerId")] public long OpenerPlayerId { get; set; } // 문을 연 플레이어 ID (0이면 없음)
    }

    // 입장 시 열린 문 목록
    [MessagePackObject]
    public class G_TO_C_DOOR_STATE_LIST : IMessagePackObject
    {
        [Key("openDoorIds")] public List<int> OpenDoorIds { get; set; } = new();
    }
}
