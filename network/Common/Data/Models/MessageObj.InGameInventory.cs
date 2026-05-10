#pragma warning disable CS8618
// ReSharper disable All
using System;
using System.Collections.Generic;
using MessagePack;
using network.common.data.helpers;

namespace network.common.data.models
{
    // 인게임 아이템 정보 (게임 내 배낭용 - 게임 종료 시 초기화)
    [MessagePackObject]
    public class InGameItemInfo
    {
        [Key("itemUid")] public long ItemUid { get; set; }   // MatchingId + Sequence 조합
        [Key("itemId")] public int ItemId { get; set; }     // 아이템 종류
        [Key("count")] public int Count { get; set; }       // 수량
        [Key("giftState")] public GiftState GiftState { get; set; }
    }

    // 인게임 배낭 전체 목록 (게임 시작 시)
    [MessagePackObject]
    public class G_TO_C_INGAME_INVENTORY_LIST : IMessagePackObject
    {
        [Key("items")] public List<InGameItemInfo> Items { get; set; }
    }

    // 인게임 배낭 업데이트 (아이템 획득/사용 시)
    [MessagePackObject]
    public class G_TO_C_INGAME_INVENTORY_UPDATE : IMessagePackObject
    {
        [Key("items")] public List<InGameItemInfo> Items { get; set; }
    }

    // 인게임 아이템 사용 요청
    [MessagePackObject]
    public class C_TO_G_USE_INGAME_ITEM : IMessagePackObject
    {
        [Key("itemUid")] public long ItemUid { get; set; }
        [Key("count")] public int Count { get; set; }
    }

    // 인게임 아이템 사용 결과
    [MessagePackObject]
    public class G_TO_C_USE_INGAME_ITEM_RESULT : IMessagePackObject
    {
        [Key("success")] public bool Success { get; set; }
        [Key("itemUid")] public long ItemUid { get; set; }
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
        [Key("ruleId")] public int RuleId { get; set; } // 행동 수칙 쪽지 아이템(202000003) 사용 시 규칙 ID
    }
}
