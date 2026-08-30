using game_server.services;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public class MonsterSnapshotBatcherTests
{
    [Fact]
    public void CreateAreaChunks_SeparatesAreasAndRespectsPacketLimit()
    {
        var states = Enumerable.Range(1, 12)
            .Select(id => State(id, AreaType.Classroom4, 48 - id))
            .Append(State(100, AreaType.Gym, 40))
            .Reverse()
            .ToList();

        var chunks = MonsterSnapshotBatcher.CreateAreaChunks(states);

        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, chunk => Assert.InRange(chunk.Monsters.Count, 1,
            MonsterSnapshotBatcher.DefaultChunkSize));
        Assert.All(chunks, chunk => Assert.All(chunk.Monsters,
            monster => Assert.Equal(chunk.Area, monster.AreaType)));
        Assert.Equal(Enumerable.Range(1, 12), chunks
            .Where(chunk => chunk.Area == AreaType.Classroom4)
            .SelectMany(chunk => chunk.Monsters)
            .Select(monster => monster.MonsterId));
    }

    private static MonsterRuntimeInfo State(int monsterId, AreaType area, int health, bool isAlive = true) => new()
    {
        MonsterId = monsterId,
        AreaType = area,
        MaxHealth = 48,
        CurrentHealth = health,
        IsAlive = isAlive
    };
}
