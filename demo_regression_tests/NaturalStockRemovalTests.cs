using MessagePack;
using network.common;
using network.common.data.models;
using network.packets;

namespace demo_regression_tests;

public sealed class NaturalStockRemovalTests
{
    [Fact]
    public void GroundItemSnapshotAndSpawnKeepItemsWithoutNaturalStock()
    {
        var items = new List<GroundItemInfo>
        {
            new() { GroundItemUid = 123, ItemId = 456, AreaType = 7, PositionX = 1.5f }
        };
        using var snapshot = PacketMaker.G_TO_C_GROUND_ITEM_SNAPSHOT(7, items);
        using var spawn = PacketMaker.G_TO_C_GROUND_ITEM_SPAWN(7, items);
        var snapshotBody = snapshot.ToBytes().AsMemory(16);
        var spawnBody = spawn.ToBytes().AsMemory(16);
        var snapshotMessage = MessagePackSerializer.Deserialize<G_TO_C_GROUND_ITEM_SNAPSHOT>(snapshotBody);
        var spawnMessage = MessagePackSerializer.Deserialize<G_TO_C_GROUND_ITEM_SPAWN>(spawnBody);

        Assert.Equal(7, snapshotMessage.AreaType);
        Assert.Equal(123L, Assert.Single(snapshotMessage.Items).GroundItemUid);
        Assert.Equal(1.5f, Assert.Single(spawnMessage.Items).PositionX);
        Assert.DoesNotContain("remainingNaturalStock", MessagePackSerializer.ConvertToJson(snapshotBody));
        Assert.DoesNotContain("remainingNaturalStock", MessagePackSerializer.ConvertToJson(spawnBody));
    }

    [Fact]
    public void RetiredStockProtocolDoesNotRenumberFollowingMessages()
    {
        Assert.DoesNotContain("G_TO_C_AREA_STOCK_STATE", Enum.GetNames<Protocol>());
        Assert.Equal((int)Protocol.G_TO_C_ORB_EFFECT_STATE + 2, (int)Protocol.G_TO_C_MONSTER_SNAPSHOT);
    }
}
