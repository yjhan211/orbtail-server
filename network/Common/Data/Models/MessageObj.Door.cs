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
