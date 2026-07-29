using game_server.services;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public class EmotionAfterimageMonsterManagerTests
{
    private const long MatchingId = 202;
    private static readonly DateTime StartedAt = new(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void InitialSpawn_ActivatesOneNineMemberPackPerRoom_WithoutCorridorFarm()
    {
        var manager = CreateManager();

        var snapshot = manager.GetSnapshot(MatchingId);
        Assert.Equal(161, snapshot.Count);
        Assert.Equal(126, snapshot.Count(info => info.IsAlive));
        Assert.DoesNotContain(snapshot, info => info.AreaType == AreaType.Corridor && info.IsAlive);
        Assert.Equal(9, snapshot.Count(info => info.AreaType == AreaType.Classroom4 && info.IsAlive));
    }

    [Fact]
    public void RoomPack_PreservesPreviousHealthAndStoneBudget_WithSixUnmarkedEscorts()
    {
        var manager = CreateManager();
        var pack = manager.GetSnapshot(MatchingId, AreaType.Classroom4).Where(info => info.IsAlive).ToList();

        Assert.Equal(9, pack.Count);
        Assert.Equal(144, pack.Sum(info => info.MaxHealth));
        Assert.Equal(14, pack.Sum(info => info.SummonStoneReward));
        Assert.Equal(8, pack.Count(info => !info.IsCore && info.SummonStoneReward == 1));
        Assert.Single(pack.Select(info => info.RewardItemId).Distinct());
        var core = Assert.Single(pack, info => info.IsCore);
        Assert.Equal(48, core.MaxHealth);
        Assert.Equal(6, core.SummonStoneReward);
    }

    [Fact]
    public void Damage_ResetsAfterLeashDelay_WhenNoPlayerRemainsInRange()
    {
        var manager = CreateManager();
        int monsterId = FirstEscortId(manager);

        var hit = manager.ApplyDamage(MatchingId, monsterId, 10, 7, StartedAt);

        Assert.True(hit.StateChanged);
        Assert.False(hit.Killed);
        Assert.Equal(5, hit.State!.CurrentHealth);

        var tick = manager.Tick(MatchingId, [], StartedAt.AddSeconds(6));

        var reset = Assert.Single(tick.ChangedStates, info => info.MonsterId == monsterId);
        Assert.True(reset.IsAlive);
        Assert.Equal(reset.MaxHealth, reset.CurrentHealth);
    }

    [Fact]
    public void MarkedNormal_DespawnsWithoutTimeBasedRespawn_AndAwardsOneStone()
    {
        var manager = CreateManager();
        int monsterId = manager.GetSnapshot(MatchingId, AreaType.Classroom4)
            .First(info => info.SummonStoneReward == 1).MonsterId;

        var lethal = manager.ApplyDamage(MatchingId, monsterId, 10, 999, StartedAt);

        Assert.True(lethal.Killed);
        Assert.Equal(SummonStoneManager.NormalMonsterReward, lethal.SummonStoneReward);
        Assert.False(lethal.State!.IsAlive);
        var laterTick = manager.Tick(MatchingId, [], StartedAt.AddSeconds(60));
        Assert.DoesNotContain(laterTick.ChangedStates, info => info.MonsterId == monsterId);
        Assert.False(manager.GetSnapshot(MatchingId).Single(info => info.MonsterId == monsterId).IsAlive);
    }

    [Fact]
    public void AreaClosure_RemovesClosedPacksAndRefillsOnlyTheThreeDormantRoomPacks()
    {
        var manager = CreateManager();

        Assert.True(manager.ApplyAreaClosureAndSpawnWave(MatchingId,
            [AreaType.ExamRoom, AreaType.BroadcastRoom, AreaType.Classroom2]));

        var snapshot = manager.GetSnapshot(MatchingId);
        Assert.All(snapshot.Where(info => info.AreaType is AreaType.ExamRoom or AreaType.BroadcastRoom or AreaType.Classroom2),
            info => Assert.False(info.IsAlive));
        Assert.Equal(126, snapshot.Count(info => info.IsAlive));
        Assert.Equal(18, snapshot.Count(info => info.AreaType == AreaType.Library && info.IsAlive));
        Assert.False(manager.ApplyAreaClosureAndSpawnWave(MatchingId, [AreaType.ExamRoom]));
    }

    [Fact]
    public void Tick_ChasesSameAreaTargetBeyondFormerLeashRange()
    {
        var manager = CreateManager();
        var first = manager.GetAliveTargets(MatchingId).First(target => target.Area == AreaType.Classroom4);
        var targetPosition = new Vector3f(first.Position.X + 5f, first.Position.Y + 3f, 0f);

        var tick = manager.Tick(MatchingId,
        [
            new MonsterSpatialTarget(10, MapId.School, AreaType.Classroom4, targetPosition)
        ], StartedAt.AddMilliseconds(50));

        var moved = Assert.Single(tick.ChangedStates, info => info.MonsterId == first.MonsterId);
        Assert.NotEqual(first.Position.X, moved.PositionX);
        Assert.Empty(tick.Attacks);
    }

    [Fact]
    public void Tick_PrefersHighestTotalDamageWhenSeveralPlayersAreInTheSameArea()
    {
        var manager = CreateManager();
        var first = manager.GetAliveTargets(MatchingId).First(target => target.Area == AreaType.Classroom4);
        manager.ApplyDamage(MatchingId, first.MonsterId, 20, 1, StartedAt);
        manager.ApplyDamage(MatchingId, first.MonsterId, 10, 3, StartedAt);

        var attacks = manager.Tick(MatchingId,
        [
            new MonsterSpatialTarget(10, MapId.School, AreaType.Classroom4, first.Position),
            new MonsterSpatialTarget(20, MapId.School, AreaType.Classroom4, first.Position)
        ], StartedAt).Attacks;

        Assert.NotEmpty(attacks);
        Assert.All(attacks, attack => Assert.Equal(10, attack.TargetPlayerId));
    }

    [Fact]
    public void Tick_BucketsTargetsByMapAndAreaBeforeSelecting()
    {
        var manager = CreateManager();
        var first = manager.GetAliveTargets(MatchingId).First(target => target.Area == AreaType.Classroom4);

        var attacks = manager.Tick(MatchingId,
        [
            new MonsterSpatialTarget(99, MapId.None, AreaType.Classroom4, first.Position),
            new MonsterSpatialTarget(10, MapId.School, AreaType.Classroom4, first.Position)
        ], StartedAt).Attacks;

        Assert.NotEmpty(attacks);
        Assert.All(attacks, attack => Assert.Equal(10, attack.TargetPlayerId));
    }

    [Fact]
    public void SpawnGroups_AssignNineDeterministicFormationSlots()
    {
        var manager = CreateManager();

        var libraryGroups = manager.GetAliveTargets(MatchingId)
            .Where(target => target.Area == AreaType.Library)
            .GroupBy(target => target.ClusterId);
        var group = Assert.Single(libraryGroups);
        Assert.Equal(9, group.Count());
        Assert.All(group, target => Assert.Equal(9, target.ClusterSize));
        Assert.Equal(Enumerable.Range(0, 9), group.Select(target => target.ClusterMemberIndex).OrderBy(index => index));
    }

    [Fact]
    public void Tick_SettlesPackIntoDeterministicLooseEscortAndCoreFormation()
    {
        var first = CreateManager();
        var second = CreateManager();
        var targetPosition = new Vector3f(16f, 55.5f, 0f);
        var target = new MonsterSpatialTarget(10, MapId.School, AreaType.Classroom4, targetPosition);

        Advance(first, [target], StartedAt, 90);
        Advance(second, [target], StartedAt, 90);

        var firstFormation = first.GetAliveTargets(MatchingId)
            .Where(monster => monster.Area == AreaType.Classroom4)
            .OrderBy(monster => monster.MonsterId)
            .ToList();
        var secondFormation = second.GetAliveTargets(MatchingId)
            .Where(monster => monster.Area == AreaType.Classroom4)
            .OrderBy(monster => monster.MonsterId)
            .ToList();

        Assert.Equal(9, firstFormation.Count);
        for (int index = 0; index < firstFormation.Count; index++)
        {
            Assert.Equal(firstFormation[index].Position.X, secondFormation[index].Position.X, 5);
            Assert.Equal(firstFormation[index].Position.Y, secondFormation[index].Position.Y, 5);
        }

        var distances = firstFormation.Select(monster => Distance(monster.Position, targetPosition)).OrderBy(value => value).ToList();
        Assert.InRange(distances[0], 0f, 0.03f);
        Assert.All(distances.Skip(1).Take(2), distance => Assert.InRange(distance, 0.58f, 0.67f));
        Assert.All(distances.Skip(3), distance => Assert.InRange(distance, 0.88f, 1.52f));
        Assert.Equal(6, firstFormation.Skip(3)
            .Select(monster => $"{monster.Position.X:F3},{monster.Position.Y:F3}").Distinct().Count());
    }

    [Fact]
    public void IdleEscorts_KeepDistinctHomeSlotsAndPatrolIndependently()
    {
        var manager = CreateManager();
        var escortIds = manager.GetAliveTargets(MatchingId)
            .Where(target => target.Area == AreaType.Classroom4 && !target.IsCore &&
                             target.ClusterMemberIndex < target.ClusterSize - 3)
            .Select(target => target.MonsterId)
            .ToHashSet();
        var initialEscorts = manager.GetSnapshot(MatchingId, AreaType.Classroom4)
            .Where(info => escortIds.Contains(info.MonsterId))
            .ToList();

        Assert.Equal(6, initialEscorts.Count);
        Assert.Equal(6, initialEscorts.Select(info => $"{info.PositionX:F3},{info.PositionY:F3}").Distinct().Count());
        Assert.True(initialEscorts.Max(info => info.PositionX) - initialEscorts.Min(info => info.PositionX) > 1f);
        Assert.True(initialEscorts.Max(info => info.PositionY) - initialEscorts.Min(info => info.PositionY) > 1f);

        var idleTick = manager.Tick(MatchingId, [], StartedAt.AddSeconds(1));
        var patrolUpdates = idleTick.ChangedStates
            .Where(info => initialEscorts.Any(escort => escort.MonsterId == info.MonsterId))
            .ToList();
        Assert.Equal(6, patrolUpdates.Count);
        Assert.Empty(idleTick.Attacks);

        foreach (var escort in initialEscorts)
        {
            var current = manager.GetSnapshot(MatchingId).Single(info => info.MonsterId == escort.MonsterId);
            Assert.InRange(Distance(new Vector3f(current.PositionX, current.PositionY, 0f),
                new Vector3f(escort.PositionX, escort.PositionY, 0f)), 0.01f, 0.19f);
        }
    }

    [Fact]
    public void MatchSeed_ConstrainsRegionalAffinitiesAndKeepsEachPackCohesive()
    {
        var manager = CreateManager();
        var observedTargets = manager.GetAliveTargets(MatchingId).ToDictionary(target => target.MonsterId);

        // The first closure consumes its three-pack budget and exposes every initially
        // dormant second pack without changing the placement chosen for this match.
        Assert.True(manager.ApplyAreaClosureAndSpawnWave(MatchingId,
            [AreaType.ExamRoom, AreaType.BroadcastRoom, AreaType.Classroom2]));
        foreach (var target in manager.GetAliveTargets(MatchingId))
            observedTargets.TryAdd(target.MonsterId, target);

        var roomTargets = observedTargets.Values.Where(target => target.Area != AreaType.Corridor).ToList();
        Assert.All(roomTargets.GroupBy(target => target.ClusterId),
            pack => Assert.Single(pack.Select(target => target.RewardItemId).Distinct()));

        var affinityByArea = roomTargets
            .GroupBy(target => target.Area)
            .ToDictionary(group => group.Key, group => group.Select(target => target.RewardItemId).Distinct().ToList());
        Assert.All(affinityByArea.Values, affinities => Assert.InRange(affinities.Count, 1, 2));

        foreach (int attackAffinity in new[] { 107000010, 107000020, 107000030 })
            Assert.True(affinityByArea.Values.Count(affinities => affinities.Contains(attackAffinity)) >= 2,
                $"Attack affinity {attackAffinity} must be reachable in at least two areas.");
        Assert.Contains(affinityByArea.Values, affinities => affinities.Contains(107000040));
    }

    [Fact]
    public void DifferentMatchSeeds_ChangeRegionalAffinityPlacement()
    {
        var first = CreateManager();
        var firstPlacement = first.GetAliveTargets(MatchingId)
            .ToDictionary(target => target.MonsterId, target => target.RewardItemId);

        bool changed = false;
        for (long matchingId = MatchingId + 1; matchingId < MatchingId + 33 && !changed; matchingId++)
        {
            var candidate = new EmotionAfterimageMonsterManager();
            candidate.InitializeMatching(matchingId);
            changed = candidate.GetAliveTargets(matchingId)
                .Any(target => firstPlacement[target.MonsterId] != target.RewardItemId);
        }

        Assert.True(changed, "A different match seed should produce a different regional affinity plan.");
    }
    [Fact]
    public void CorridorNeutral_SpawnsAwayFromPlayers_AwardsOneStone_AndDespawnsAfterFourSeconds()
    {
        var manager = CreateManager();
        var corridorTarget = new MonsterSpatialTarget(10, MapId.School, AreaType.Corridor, new Vector3f(35f, 52.5f, 0f));

        var spawned = manager.Tick(MatchingId, [corridorTarget], StartedAt);
        var neutral = Assert.Single(spawned.ChangedStates, info => info.AreaType == AreaType.Corridor);
        Assert.Equal(AreaType.Corridor, neutral.AreaType);
        Assert.True(neutral.IsAlive);
        Assert.Equal(0, neutral.RewardItemId);
        Assert.False(neutral.IsCore);
        Assert.Equal(SummonStoneManager.NormalMonsterReward, neutral.SummonStoneReward);
        Assert.True(Distance(new Vector3f(neutral.PositionX, neutral.PositionY, 0f), corridorTarget.Position) >= 3f);

        var lethal = manager.ApplyDamage(MatchingId, neutral.MonsterId, 10, 999, StartedAt.AddMilliseconds(1));
        Assert.True(lethal.Killed);
        Assert.Equal(SummonStoneManager.NormalMonsterReward, lethal.SummonStoneReward);

        // Spawn another neutral, then verify the no-target timeout removes it.
        var respawned = manager.Tick(MatchingId, [corridorTarget], StartedAt.AddSeconds(8));
        var second = Assert.Single(respawned.ChangedStates, info => info.AreaType == AreaType.Corridor && info.IsAlive);
        var despawned = manager.Tick(MatchingId, [], StartedAt.AddSeconds(12.1));
        var removed = Assert.Single(despawned.ChangedStates, info => info.MonsterId == second.MonsterId);
        Assert.False(removed.IsAlive);
    }

    private static EmotionAfterimageMonsterManager CreateManager()
    {
        var manager = new EmotionAfterimageMonsterManager();
        manager.InitializeMatching(MatchingId);
        return manager;
    }

    private static int FirstEscortId(EmotionAfterimageMonsterManager manager) =>
        manager.GetAliveTargets(MatchingId)
            .First(target => target.Area == AreaType.Classroom4 && !target.IsCore &&
                             target.ClusterMemberIndex < target.ClusterSize - 3)
            .MonsterId;

    private static DateTime Advance(
        EmotionAfterimageMonsterManager manager,
        IReadOnlyList<MonsterSpatialTarget> targets,
        DateTime startedAt,
        int tickCount)
    {
        DateTime now = startedAt;
        for (int index = 0; index < tickCount; index++)
        {
            now = now.AddMilliseconds(100);
            manager.Tick(MatchingId, targets, now);
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