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

    [Fact]
    public void Accumulator_KeepsOnlyFinalStateForEachMonster()
    {
        var accumulator = new MonsterSnapshotAccumulator();
        accumulator.Record(State(2, AreaType.Gym, 30));
        accumulator.Record(State(1, AreaType.Classroom4, 41));
        accumulator.Record(State(1, AreaType.Classroom4, 0, isAlive: false));

        var finalStates = accumulator.GetFinalStates();

        Assert.Equal(2, accumulator.Count);
        Assert.Equal([1, 2], finalStates.Select(state => state.MonsterId));
        Assert.False(finalStates[0].IsAlive);
        Assert.Equal(0, finalStates[0].CurrentHealth);
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
