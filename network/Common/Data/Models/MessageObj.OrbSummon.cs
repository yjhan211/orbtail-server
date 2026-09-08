#pragma warning disable CS8618
using System.Collections.Generic;
using MessagePack;

namespace network.common.data.models
{
    [MessagePackObject]
    public sealed class SummonStoneStateInfo
    {
        [Key("stoneCount")] public int StoneCount { get; set; }
        [Key("successfulSummonCount")] public int SuccessfulSummonCount { get; set; }
        [Key("nextCost")] public int NextCost { get; set; }
        [Key("poolItemIds")] public List<int> PoolItemIds { get; set; } = new();

        /// <summary>
        ///     소환 2택 후보. 다음 소환에서 고를 수 있는 오브 2종이다. 결과가 (매치, 플레이어,
        ///     소환 횟수)에만 결정론적으로 묶여 있어 서버가 미리 공개할 수 있고, 대기 상태나
        ///     만료 타이머 없이 재접속에도 같은 값이 복원된다.
        /// </summary>
        [Key("nextCandidateItemIds")] public List<int> NextCandidateItemIds { get; set; } = new();
    }

    [MessagePackObject]
    public sealed class G_TO_C_SUMMON_STONE_STATE : IMessagePackObject
    {
        [Key("state")] public SummonStoneStateInfo State { get; set; }
        // Only award packets populate these fields. Ordinary reconnect/state snapshots remain visual-free.
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

    [MessagePackObject]
    public sealed class C_TO_G_DESTROY_ORB : IMessagePackObject
    {
        [Key("itemUid")] public long ItemUid { get; set; }
    }

    [MessagePackObject]
    public sealed class G_TO_C_DESTROY_ORB_RESULT : IMessagePackObject
    {
        [Key("success")] public bool Success { get; set; }
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
        [Key("itemUid")] public long ItemUid { get; set; }
        [Key("refundedStones")] public int RefundedStones { get; set; }
        [Key("state")] public SummonStoneStateInfo State { get; set; }
    }
}
