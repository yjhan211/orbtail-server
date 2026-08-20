#pragma warning disable CS8618
// ReSharper disable All
using System;
using System.Collections.Generic;
using MessagePack;
using network.common.data.helpers;

namespace network.common.data.models
{
    // 탐색 시작 요청

    // 탐색 시작 브로드캐스트 (다른 플레이어에게 애니메이션 동기화)
    [MessagePackObject]
    public class G_TO_C_EXPLORE_START : IMessagePackObject
    {
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("interactId")] public int InteractId { get; set; }
    }

    // 선택지 선택

    // 탐색 결과 (요청한 클라이언트에게만)

    // 탐색 종료 요청 (클라이언트 → 서버)

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
