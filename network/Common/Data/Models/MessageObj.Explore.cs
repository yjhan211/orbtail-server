#pragma warning disable CS8618
// ReSharper disable All
using System;
using System.Collections.Generic;
using MessagePack;
using network.common.data.helpers;

namespace network.common.data.models
{
    // 탐색 시작 요청
    [MessagePackObject]
    public class C_TO_G_EXPLORE_START : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
    }

    // 탐색 시작 브로드캐스트 (다른 플레이어에게 애니메이션 동기화)
    [MessagePackObject]
    public class G_TO_C_EXPLORE_START : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("interactId")] public int InteractId { get; set; }
    }

    // 선택지 선택
    [MessagePackObject]
    public class C_TO_G_EXPLORE_SELECT : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
        [Key("actionId")] public int ActionId { get; set; }
    }

    // 탐색 결과 (요청한 클라이언트에게만)
    [MessagePackObject]
    public class G_TO_C_EXPLORE_RESULT : IMessagePackObject
    {
        [Key("success")] public bool Success { get; set; }
        [Key("interactId")] public int InteractId { get; set; }
        [Key("actionId")] public int ActionId { get; set; }
        [Key("itemId")] public int ItemId { get; set; }  // 획득한 아이템 ID (0이면 없음)
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
        [Key("isViolation")] public bool IsViolation { get; set; }  // 규칙 위반 여부
    }

    // 탐색 종료 요청 (클라이언트 → 서버)
    [MessagePackObject]
    public class C_TO_G_EXPLORE_END : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
    }

    // 탐색 종료 브로드캐스트 (다른 플레이어에게 애니메이션 종료 동기화)
    [MessagePackObject]
    public class G_TO_C_EXPLORE_END : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; }
    }

    // Interactable state 변경 알림 (사보타주 등)
    [MessagePackObject]
    public class G_TO_C_INTERACTABLE_STATE_CHANGE : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
        [Key("newState")] public int NewState { get; set; }
    }
}
