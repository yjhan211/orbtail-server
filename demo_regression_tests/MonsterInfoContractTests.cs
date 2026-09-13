using MessagePack;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class MonsterInfoContractTests
{
    [Fact]
    public void SpatialPropertiesUseOneStorage()
    {
        var info = new MonsterInfo { MonsterId = 7, PositionX = 3, PositionY = 4 };
        Assert.Equal(ObjectType.MONSTER, info.ObjectInfo.ObjectType);
        Assert.Equal(7, info.ObjectInfo.ObjectId);
        Assert.Equal(3, info.ObjectInfo.Position.X);
        info.ObjectInfo.Position.X = 9;
        Assert.Equal(9, info.PositionX);
    }

    [Fact]
    public void SnapshotKeepsWireKeysAndReconstructsSpatialStorage()
    {
        var info = new MonsterInfo { MonsterId = 7, PositionX = 3, PositionY = 4, CurrentHealth = 12, IsAlive = true };
        var bytes = MessagePackSerializer.Serialize(new G_TO_C_MONSTER_SNAPSHOT { Monsters = [info] });
        string json = MessagePackSerializer.ConvertToJson(bytes);
        Assert.Contains("\"positionX\":3", json);
        Assert.Contains("\"positionY\":4", json);
        Assert.DoesNotContain("objectInfo", json);
        var copy = Assert.Single(MessagePackSerializer.Deserialize<G_TO_C_MONSTER_SNAPSHOT>(bytes).Monsters);
        Assert.Equal(7, copy.ObjectInfo.ObjectId);
        Assert.Equal(3, copy.ObjectInfo.Position.X);
        Assert.Equal(4, copy.ObjectInfo.Position.Y);
        Assert.Equal(12, copy.CurrentHealth);
        Assert.True(copy.IsAlive);
        Assert.NotSame(info.ObjectInfo, copy.ObjectInfo);
    }
}
