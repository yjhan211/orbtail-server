#pragma warning disable CS8618
using MessagePack;

namespace network.common.data.models
{
    [MessagePackObject]
    public sealed class SummonStoneStateInfo
    {
        [Key("stoneCount")] public int StoneCount { get; set; }
        [Key("successfulSummonCount")] public int SuccessfulSummonCount { get; set; }
        [Key("nextCost")] public int NextCost { get; set; }

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




}
