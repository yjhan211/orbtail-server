using MessagePack;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class MonsterInfoContractTests
{
    [Fact]
    public void ServerMonsterUsesCommonStorageAndCopiesSnapshot()
    {
        var monster = new game_server.matches.monsters.Monster
        {
            MonsterId = 7,
            Position = new Vector3f(3, 4, 0),
            Health = 12,
            MaxHealthValue = 20,
            Alive = true,
            Insignia = MonsterInsignia.Wind,
            SummonStoneReward = 2,
            Kind = MonsterKind.Bowler,
            PhaseTier = 3,
            ChaseTargetPlayerId = 42,
            Area = AreaType.S2Gym1
        };
        Assert.Same(monster.Position, monster.Info.ObjectInfo.Position);
        Assert.Equal(12, monster.Info.CurrentHealth);
        var snapshot = monster.ToMonsterInfo();
        Assert.Equal(MessagePackSerializer.Serialize(monster.Info), MessagePackSerializer.Serialize(snapshot));
        Assert.NotSame(monster.Info, snapshot);
        Assert.NotSame(monster.Position, snapshot.ObjectInfo.Position);

        monster.Info.ObjectInfo.Position.X = 9;
        monster.Info.CurrentHealth = 8;
        Assert.Equal(9, monster.Position.X);
        Assert.Equal(8, monster.Health);
        Assert.False(monster.ApplyDamage(3, DateTime.UtcNow));
        Assert.Equal(5, monster.Info.CurrentHealth);
        Assert.Equal(3, snapshot.PositionX);
        Assert.Equal(12, snapshot.CurrentHealth);
        Assert.True(monster.ApplyDamage(5, DateTime.UtcNow));
        Assert.False(monster.Info.IsAlive);
    }

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
