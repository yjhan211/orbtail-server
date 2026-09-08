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

    // 보유 오브 목록 (입장·강화 후 동기화)
    [MessagePackObject]
    public class G_TO_C_ORB_LIST : IMessagePackObject
    {
        [Key("items")] public List<InGameItemInfo> Items { get; set; }
    }

    // 보유 오브 변경 내역 (Count가 0이면 제거)
    [MessagePackObject]
    public class G_TO_C_ORB_UPDATE : IMessagePackObject
    {
        [Key("items")] public List<InGameItemInfo> Items { get; set; }
    }







}
