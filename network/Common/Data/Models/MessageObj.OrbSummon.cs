#pragma warning disable CS8618
using MessagePack;
using System;

namespace network.common.data.models
{
    /// <summary>공통 소환석 잔액·소환 횟수·표시 비용. 서버는 갱신 시 새 객체를 만들고, 전송에는 복사본을 사용한다.</summary>
    [MessagePackObject]
    public sealed class SummonStoneStateInfo
    {
        public SummonStoneStateInfo() { }

        public SummonStoneStateInfo(int stoneCount, int successfulSummonCount)
        {
            StoneCount = stoneCount;
            SuccessfulSummonCount = successfulSummonCount;
            NextCost = CostAfter(successfulSummonCount);
        }

        [IgnoreMember] public static SummonStoneStateInfo Empty => new SummonStoneStateInfo(0, 0);

        public SummonStoneStateInfo Copy() => new SummonStoneStateInfo
        {
            StoneCount = StoneCount, SuccessfulSummonCount = SuccessfulSummonCount, NextCost = NextCost
        };

        public static int CostAfter(int successfulSummonCount)
        {
            long summonNumber = (long)Math.Max(0, successfulSummonCount) + 1;
            long cost = Math.Max(2, summonNumber * (summonNumber + 1) / 2);
            return (int)Math.Min(int.MaxValue, cost);
        }

        [Key("stoneCount")] public int StoneCount { get; set; }
        [Key("successfulSummonCount")] public int SuccessfulSummonCount { get; set; }
        [Key("nextCost")] public int NextCost { get; set; }

    }

    [MessagePackObject]
    public sealed class G_TO_C_SUMMON_STONE_STATE : IMessagePackObject
    {
        [Key("state")] public SummonStoneStateInfo State { get; set; }
        [Key("awardedStones")] public int AwardedStones { get; set; }
        [Key("awardSourceX")] public float AwardSourceX { get; set; }
        [Key("awardSourceY")] public float AwardSourceY { get; set; }
    }

    [MessagePackObject]
    public sealed class C_TO_G_SUMMON_ORB : IMessagePackObject
    {
    }

    [MessagePackObject]
    public sealed class G_TO_C_SUMMON_ORB_RESULT : IMessagePackObject
    {
        [Key("success")] public bool Success { get; set; }
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
        [Key("summonedItemId")] public int SummonedItemId { get; set; }
        [Key("summonedItemUid")] public long SummonedItemUid { get; set; }
        [Key("state")] public SummonStoneStateInfo State { get; set; }
    }
}
