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
        Assert.Equal(3, snapshot.ObjectInfo.Position.X);
        Assert.Equal(12, snapshot.CurrentHealth);
        Assert.True(monster.ApplyDamage(5, DateTime.UtcNow));
        Assert.False(monster.Info.IsAlive);
    }

    [Fact]
    public void SpatialPropertiesUseOneStorage()
    {
        var info = new MonsterInfo { MonsterId = 7, AreaType = AreaType.S2Gym1 };
        Assert.Equal(ObjectType.MONSTER, info.ObjectInfo.ObjectType);
        Assert.Equal(7, info.ObjectInfo.ObjectId);
        Assert.Equal(AreaType.S2Gym1, info.ObjectInfo.Area);
        info.ObjectInfo.ObjectId = 9;
        info.ObjectInfo.Area = AreaType.S2Ground;
        Assert.Equal(9, info.MonsterId);
        Assert.Equal(AreaType.S2Ground, info.AreaType);
    }

    [Fact]
    public void SnapshotSerializesCommonSpatialStorage()
    {
        var info = new MonsterInfo
        {
            ObjectInfo = new GameObjectInfo
            {
                ObjectType = ObjectType.MONSTER,
                ObjectId = 7,
                Position = new Vector3f(3, 4, 0),
                Cell = new Cell(2, 3),
                Velocity = new Vector3f(1, 2, 0),
                Rotation = 180f,
                Area = AreaType.S2Gym1,
                MapId = Config.SWARM_MATCH_MAP
            },
            CurrentHealth = 12,
            IsAlive = true
        };
        var bytes = MessagePackSerializer.Serialize(new G_TO_C_MONSTER_SNAPSHOT { Monsters = [info] });
        string json = MessagePackSerializer.ConvertToJson(bytes);
        Assert.DoesNotContain("\"positionX\"", json);
        Assert.DoesNotContain("\"positionY\"", json);
        Assert.Contains("\"objectInfo\"", json);
        Assert.Contains("\"monsterId\":7", json);
        var copy = Assert.Single(MessagePackSerializer.Deserialize<G_TO_C_MONSTER_SNAPSHOT>(bytes).Monsters);
        Assert.Equal(7, copy.ObjectInfo.ObjectId);
        Assert.Equal(copy.ObjectInfo.ObjectId, copy.MonsterId);
        Assert.Equal(3, copy.ObjectInfo.Position.X);
        Assert.Equal(4, copy.ObjectInfo.Position.Y);
        Assert.Equal(info.ObjectInfo.Cell, copy.ObjectInfo.Cell);
        Assert.Equal(info.ObjectInfo.Velocity, copy.ObjectInfo.Velocity);
        Assert.Equal(180f, copy.ObjectInfo.Rotation);
        Assert.Equal(info.ObjectInfo.MapId, copy.ObjectInfo.MapId);
        Assert.Equal(AreaType.S2Gym1, copy.AreaType);
        Assert.NotSame(info.ObjectInfo.Position, copy.ObjectInfo.Position);
        Assert.Equal(12, copy.CurrentHealth);
        Assert.True(copy.IsAlive);
        Assert.NotSame(info.ObjectInfo, copy.ObjectInfo);
    }
}
