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
    }

    // ===== 배틀아이템 조합 =====

    /// <summary>
    ///     아이템 조합의 성공·실패와 결과 아이템을 전달한다. 실패 이유는 ErrorCode로 확인한다.
    /// </summary>
    [MessagePackObject]
    public class G_TO_C_ITEMS_COMBINED : IMessagePackObject
    {
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
        [Key("recipeId")] public int RecipeId { get; set; }
        [Key("inputPartA")] public int InputItemA { get; set; }
        [Key("inputPartB")] public int InputItemB { get; set; }
        [Key("outputPartId")] public int OutputItemId { get; set; }
    }

    /// <summary>
    ///     배틀아이템·오브 조합 요청. 같은 match의 조합끼리는 서버 ordered-lane waiter 진입 FIFO를 따른다.
    ///     clientStartUnixMs는 퇴역한 mission-race payload의 key와 Int64 shape를 보존하기 위해서만 남긴다.
    /// </summary>
    [MessagePackObject]
    public class C_TO_G_COMBINE_ITEMS : IMessagePackObject
    {
        [Key("partA")] public int ItemA { get; set; }
        [Key("partB")] public int ItemB { get; set; }
        /// <summary>
        ///     Legacy reserved payload field. 서버는 권위 판정이나 순서 결정에 사용하지 않으며 현행 클라이언트는 0을 보낸다.
        /// </summary>
        [Key("clientStartUnixMs")] public long ClientStartUnixMs { get; set; }
    }
}
