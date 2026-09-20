using MessagePack;
using network.common;
using network.common.data;
using network.common.data.models;

namespace server_tests;

public sealed class CommonItemStateTests
{
    [Fact]
    public void GroundItemUsesSharedSpatialDataAndPreservesWireKeys()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var item = new GroundItemInfo
        {
            GroundItemUid = 42, ItemId = 107000010,
            PositionX = 12, PositionY = 34, SpawnOriginX = 1, SpawnOriginY = 2, SourcePlayerId = 7
        };
        Assert.Equal(ObjectType.ITEM, item.ObjectInfo.ObjectType);
        Assert.Equal(42, item.ObjectInfo.ObjectId);
        Assert.Equal(network.common.data.GameMapData.GetCurrentArea(item.ObjectInfo.MapId, item.ObjectInfo.Cell), GameMapData.GetCurrentArea(item.ObjectInfo.MapId, item.ObjectInfo.Cell));
        item.PositionX = 56;
        Assert.Equal(56, item.PositionX);
        var bytes = MessagePackSerializer.Serialize(item);
        var fields = MessagePackSerializer.Deserialize<Dictionary<string, object>>(bytes);
        Assert.Equal(8, fields.Count);
        Assert.DoesNotContain("areaType", fields.Keys);
        Assert.Contains("isLanding", fields.Keys);
        Assert.DoesNotContain("objectInfo", fields.Keys);
        var restored = MessagePackSerializer.Deserialize<GroundItemInfo>(bytes);
        Assert.Equal(56, restored.ObjectInfo.Position.X);
        Assert.Equal(34, restored.ObjectInfo.Position.Y);
        Assert.Equal(42, restored.ObjectInfo.ObjectId);
        Assert.Equal(item.AreaType, restored.AreaType);
        restored.PositionX = 99;
        Assert.Equal(56, item.PositionX);
    }

    [Fact]
    public void SummonStoneCopyAndEmptyDoNotShareMutableState()
    {
        var state = new SummonStoneStateInfo(7, 2);
        Assert.Equal(6, state.NextCost);
        var snapshot = state.Copy();
        state.StoneCount = 100;
        Assert.Equal(7, snapshot.StoneCount);
        var empty = SummonStoneStateInfo.Empty;
        empty.StoneCount = 100;
        Assert.Equal(0, SummonStoneStateInfo.Empty.StoneCount);
        var restored = MessagePackSerializer.Deserialize<SummonStoneStateInfo>(
            MessagePackSerializer.Serialize(snapshot));
        Assert.Equivalent(snapshot, restored);
    }
}
