using game_server.services;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public class EmotionAfterimageMonsterManagerTests
{
    [Fact]
    public void InitialSpawn_UsesThirtyTwoOfTheThirtyEightFixedNodes()
    {
        var manager = new EmotionAfterimageMonsterManager();
        manager.InitializeMatching(202);

        var snapshot = manager.GetSnapshot(202);
        Assert.Equal(38, snapshot.Count);
        Assert.Equal(32, snapshot.Count(info => info.IsAlive));
        Assert.DoesNotContain(snapshot, info => info.AreaType == AreaType.Corridor && info.IsAlive);
    }

    [Fact]
    public void AreaSnapshot_ReturnsOnlyNodesForRequestedArea()
    {
        var manager = new EmotionAfterimageMonsterManager();
        manager.InitializeMatching(202);

        var snapshot = manager.GetSnapshot(202, AreaType.Classroom4);

        Assert.Equal(3, snapshot.Count);
        Assert.All(snapshot, info => Assert.Equal(AreaType.Classroom4, info.AreaType));
    }

    [Fact]
    public void RemoveMatchingState_InvokesRuntimeCleanupCallback()
    {
        var manager = new EmotionAfterimageMonsterManager();
        long removedMatchingId = 0;
        manager.SetMatchingStateRemovedCallback(matchingId => removedMatchingId = matchingId);
        manager.InitializeMatching(202);

        manager.RemoveMatchingState(202);

        Assert.Equal(202, removedMatchingId);
        Assert.Empty(manager.GetSnapshot(202));
    }

    [Fact]
    public void Damage_ResetsAfterLeashDelay_WhenNoPlayerRemainsInRange()
    {
        var manager = new EmotionAfterimageMonsterManager();
        var now = new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc);
        manager.InitializeMatching(202);

        var hit = manager.ApplyDamage(202, EmotionAfterimageMonsterManager.FirstMonsterId, 10, 7, now);

        Assert.True(hit.StateChanged);
        Assert.False(hit.Killed);
        Assert.Equal(41, hit.State!.CurrentHealth);

        var tick = manager.Tick(202, [], now.AddSeconds(6));

        var reset = Assert.Single(tick.ChangedStates);
        Assert.True(reset.IsAlive);
        Assert.Equal(reset.MaxHealth, reset.CurrentHealth);
    }

    [Fact]
    public void LethalDamage_DespawnsWithoutTimeBasedRespawn()
    {
        var manager = new EmotionAfterimageMonsterManager();
        var now = new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc);
        manager.InitializeMatching(202);

        var lethal = manager.ApplyDamage(202, EmotionAfterimageMonsterManager.FirstMonsterId, 10, 999, now);

        Assert.True(lethal.Killed);
        Assert.Equal(SummonStoneManager.NormalMonsterReward, lethal.SummonStoneReward);
        Assert.False(lethal.State!.IsAlive);

        var duplicateHit = manager.ApplyDamage(
            202, EmotionAfterimageMonsterManager.FirstMonsterId, 20, 999, now.AddMilliseconds(1));

        Assert.False(duplicateHit.StateChanged);
        Assert.False(duplicateHit.Killed);
        Assert.Equal(0, duplicateHit.SummonStoneReward);
        Assert.Empty(manager.Tick(202, [], now.AddSeconds(60)).ChangedStates);
    }

    [Fact]
    public void AreaClosure_RemovesClosedMonstersAndFillsOneFiniteWaveInOpenAreas()
    {
        var manager = new EmotionAfterimageMonsterManager();
        manager.InitializeMatching(202);

        Assert.True(manager.ApplyAreaClosureAndSpawnWave(202,
            [AreaType.ExamRoom, AreaType.BroadcastRoom, AreaType.Classroom2]));

        var snapshot = manager.GetSnapshot(202);
        Assert.All(snapshot.Where(info => info.AreaType is AreaType.ExamRoom or AreaType.BroadcastRoom or AreaType.Classroom2),
            info => Assert.False(info.IsAlive));
        Assert.Equal(32, snapshot.Count(info => info.IsAlive));
        Assert.False(manager.ApplyAreaClosureAndSpawnWave(202, [AreaType.ExamRoom]));
    }

    [Fact]
    public void Tick_ChasesSameAreaTargetBeyondFormerLeashRange()
    {
        var manager = new EmotionAfterimageMonsterManager();
        var now = new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc);
        manager.InitializeMatching(202);
        float initialX = manager.GetSnapshot(202)
            .Single(info => info.MonsterId == EmotionAfterimageMonsterManager.FirstMonsterId).PositionX;

        var tick = manager.Tick(202,
        [
            new MonsterSpatialTarget(10, MapId.School, AreaType.Classroom4, new Vector3f(19f, 55.68081f, 0f))
        ], now.AddMilliseconds(50));

        var moved = tick.ChangedStates.Single(info => info.MonsterId == EmotionAfterimageMonsterManager.FirstMonsterId);
        Assert.True(moved.PositionX > initialX);
        Assert.Empty(tick.Attacks);
    }

    [Fact]
    public void Tick_PrefersHighestTotalDamageWhenSeveralPlayersAreInTheSameArea()
    {
        var manager = new EmotionAfterimageMonsterManager();
        var now = new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc);
        manager.InitializeMatching(202);
        manager.ApplyDamage(202, EmotionAfterimageMonsterManager.FirstMonsterId, 20, 1, now);
        manager.ApplyDamage(202, EmotionAfterimageMonsterManager.FirstMonsterId, 10, 3, now);

        var attacks = manager.Tick(202,
        [
            new MonsterSpatialTarget(10, MapId.School, AreaType.Classroom4, new Vector3f(16.4f, 55.7f, 0f)),
            new MonsterSpatialTarget(20, MapId.School, AreaType.Classroom4, new Vector3f(16.5f, 55.8f, 0f))
        ], now).Attacks;

        Assert.Equal(10, Assert.Single(attacks).TargetPlayerId);
    }

    [Fact]
    public void Tick_BucketsTargetsByMapAndAreaBeforeSelecting()
    {
        var manager = new EmotionAfterimageMonsterManager();
        var now = new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc);
        manager.InitializeMatching(202);

        var attacks = manager.Tick(202,
        [
            new MonsterSpatialTarget(99, MapId.None, AreaType.Classroom4, new Vector3f(16f, 55.5f, 0f)),
            new MonsterSpatialTarget(10, MapId.School, AreaType.Classroom4, new Vector3f(16f, 55.5f, 0f))
        ], now).Attacks;

        Assert.Equal(10, Assert.Single(attacks).TargetPlayerId);
    }

    [Fact]
    public void SpawnGroups_AssignFullAndCompactFormationMetadata()
    {
        var manager = new EmotionAfterimageMonsterManager();
        manager.InitializeMatching(202);

        var aliveTargets = manager.GetAliveTargets(202);
        var libraryGroup = Assert.Single(aliveTargets
            .Where(target => target.Area == AreaType.Library)
            .GroupBy(target => target.ClusterId));
        Assert.Equal(3, libraryGroup.Count());
        Assert.All(libraryGroup, target => Assert.Equal(3, target.ClusterSize));
        Assert.Equal([0, 1, 2], libraryGroup
            .Select(target => target.ClusterMemberIndex)
            .OrderBy(index => index)
            .ToArray());

        var examRoomGroup = Assert.Single(aliveTargets
            .Where(target => target.Area == AreaType.ExamRoom)
            .GroupBy(target => target.ClusterId));
        Assert.Equal(2, examRoomGroup.Count());
        Assert.All(examRoomGroup, target => Assert.Equal(2, target.ClusterSize));
        Assert.Equal([0, 1], examRoomGroup
            .Select(target => target.ClusterMemberIndex)
            .OrderBy(index => index)
            .ToArray());
    }

    [Fact]
    public void Tick_SettlesSameClusterIntoDeterministicLooseFormation()
    {
        const long matchingId = 202;
        var first = new EmotionAfterimageMonsterManager();
        var second = new EmotionAfterimageMonsterManager();
        first.InitializeMatching(matchingId);
        second.InitializeMatching(matchingId);
        var startedAt = new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc);
        var targetPosition = new Vector3f(16f, 55.5f, 0f);
        var target = new MonsterSpatialTarget(10, MapId.School, AreaType.Classroom4, targetPosition);

        Advance(first, matchingId, [target], startedAt, 80);
        Advance(second, matchingId, [target], startedAt, 80);

        var firstFormation = first.GetAliveTargets(matchingId)
            .Where(monster => monster.Area == AreaType.Classroom4)
            .OrderBy(monster => monster.MonsterId)
            .ToList();
        var secondFormation = second.GetAliveTargets(matchingId)
            .Where(monster => monster.Area == AreaType.Classroom4)
            .OrderBy(monster => monster.MonsterId)
            .ToList();

        Assert.Equal(2, firstFormation.Count);
        Assert.Equal(firstFormation.Count, secondFormation.Count);
        for (int index = 0; index < firstFormation.Count; index++)
        {
            Assert.Equal(firstFormation[index].Position.X, secondFormation[index].Position.X, 5);
            Assert.Equal(firstFormation[index].Position.Y, secondFormation[index].Position.Y, 5);
            Assert.InRange(Distance(firstFormation[index].Position, targetPosition), 0.35f, 0.55f);
        }

        Assert.True(Distance(firstFormation[0].Position, firstFormation[1].Position) >= 0.6f);
    }

    [Fact]
    public void Tick_MultipleGroundClusters_DoNotCollapseOntoTheSamePoint()
    {
        const long matchingId = 202;
        var manager = new EmotionAfterimageMonsterManager();
        manager.InitializeMatching(matchingId);
        var startedAt = new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc);
        var targetPosition = new Vector3f(32f, 30f, 0f);
        var target = new MonsterSpatialTarget(10, MapId.School, AreaType.Ground, targetPosition);

        Advance(manager, matchingId, [target], startedAt, 80);

        var formation = manager.GetAliveTargets(matchingId)
            .Where(monster => monster.Area == AreaType.Ground)
            .OrderBy(monster => monster.MonsterId)
            .ToList();
        Assert.Equal(4, formation.Count);
        for (int left = 0; left < formation.Count; left++)
        {
            Assert.InRange(Distance(formation[left].Position, targetPosition), 0.55f, 0.61f);
            for (int right = left + 1; right < formation.Count; right++)
                Assert.True(Distance(formation[left].Position, formation[right].Position) >= 0.55f);
        }
    }

    [Fact]
    public void Tick_WhenTargetDisappears_ReturnsToOwnAnchorsAndStaysStill()
    {
        const long matchingId = 202;
        var manager = new EmotionAfterimageMonsterManager();
        manager.InitializeMatching(matchingId);
        var startedAt = new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc);
        var homeByMonsterId = manager.GetAliveTargets(matchingId)
            .Where(monster => monster.Area == AreaType.Classroom4)
            .ToDictionary(monster => monster.MonsterId, monster => monster.Position);
        var target = new MonsterSpatialTarget(
            10, MapId.School, AreaType.Classroom4, new Vector3f(19f, 58f, 0f));

        DateTime now = Advance(manager, matchingId, [target], startedAt, 50);
        now = Advance(manager, matchingId, [], now, 100);

        var returned = manager.GetAliveTargets(matchingId)
            .Where(monster => monster.Area == AreaType.Classroom4)
            .ToList();
        Assert.All(returned, monster =>
        {
            Vector3f home = homeByMonsterId[monster.MonsterId];
            Assert.Equal(home.X, monster.Position.X, 5);
            Assert.Equal(home.Y, monster.Position.Y, 5);
        });

        var stableTick = manager.Tick(matchingId, [], now.AddMilliseconds(100));
        Assert.Empty(stableTick.ChangedStates);
        Assert.Empty(stableTick.Attacks);
    }

    private static DateTime Advance(
        EmotionAfterimageMonsterManager manager,
        long matchingId,
        IReadOnlyList<MonsterSpatialTarget> targets,
        DateTime startedAt,
        int tickCount)
    {
        DateTime now = startedAt;
        for (int index = 0; index < tickCount; index++)
        {
            now = now.AddMilliseconds(100);
            manager.Tick(matchingId, targets, now);
        }

        return now;
    }

    private static float Distance(Vector3f left, Vector3f right)
    {
        float x = left.X - right.X;
        float y = left.Y - right.Y;
        return MathF.Sqrt(x * x + y * y);
    }
}
