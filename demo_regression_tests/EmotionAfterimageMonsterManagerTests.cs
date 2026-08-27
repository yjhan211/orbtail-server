using game_server.services;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public class EmotionAfterimageMonsterManagerTests
{
    private const long MatchingId = 202;
    private static readonly DateTime StartedAt = new(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void InitialSpawn_ActivatesRoomPressureEverywhere_AndFourCoreHotspots()
    {
        var manager = CreateManager();

        var snapshot = manager.GetSnapshot(MatchingId);
        Assert.Equal(308, snapshot.Count);
        Assert.Equal(108, snapshot.Count(info => info.IsAlive));
        Assert.DoesNotContain(snapshot, info => info.AreaType.IsCorridor() && info.IsAlive);

        var activeByArea = snapshot
            .Where(info => info.IsAlive && !info.AreaType.IsCorridor())
            .GroupBy(info => info.AreaType)
            .ToDictionary(group => group.Key, group => group.ToList());
        Assert.Equal(13, activeByArea.Count);
        Assert.All(activeByArea.Values, monsters => Assert.InRange(monsters.Count, 8, 9));

        var coreAreas = activeByArea
            .Where(pair => pair.Value.Any(monster => monster.IsCore))
            .Select(pair => pair.Key)
            .OrderBy(area => area)
            .ToList();
        Assert.Equal(
            new[] { AreaType.S2Library1, AreaType.S2Library2, AreaType.S2Gym1, AreaType.S2Gym2 }
                .OrderBy(area => area),
            coreAreas);
        Assert.All(activeByArea.Where(pair => !coreAreas.Contains(pair.Key)), pair =>
        {
            Assert.Equal(8, pair.Value.Count);
            Assert.DoesNotContain(pair.Value, monster => monster.IsCore);
        });

        var reinforcementStates = manager.GetReinforcementSnapshot(MatchingId);
        Assert.Equal(4, reinforcementStates.Count);
        Assert.All(reinforcementStates, state => Assert.Equal(0, state.PhaseIndex));
        // RemainingBudget은 몸 예산이 아니라 방 보상 예산이다. 몸은 생존 상한 기준 무한 리필.
        Assert.All(reinforcementStates, state => Assert.Equal(10, state.RemainingBudget));
        Assert.All(reinforcementStates, state => Assert.Equal(9, state.TargetAliveCount));
    }

    [Fact]
    public void RoomPack_PreservesPreviousHealthAndStoneBudget_WithSixUnmarkedEscorts()
    {
        var manager = CreateManager();
        var pack = manager.GetSnapshot(MatchingId, AreaType.S2Library1).Where(info => info.IsAlive).ToList();

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
    public void DamagedCore_KeepsHealthWhileAnyPlayerRemainsInItsArea()
    {
        var manager = CreateManager();
        // 합류 구역은 팩이 2개(두 번째는 대기)라 핵도 둘 — 살아 있는 핵으로 고른다.
        var core = manager.GetSnapshot(MatchingId, AreaType.S2Library1)
            .Single(info => info.IsAlive && info.IsCore);
        manager.ApplyDamage(MatchingId, core.MonsterId, 10, 7, StartedAt);

        var occupiedTick = manager.Tick(
            MatchingId,
            [new MonsterSpatialTarget(10, Config.SWARM_MATCH_MAP, AreaType.S2Library1,
                new Vector3f(core.PositionX, core.PositionY, 0f))],
            StartedAt.AddSeconds(6.1));

        Assert.Equal(core.MaxHealth - 7,
            manager.GetSnapshot(MatchingId).Single(info => info.MonsterId == core.MonsterId).CurrentHealth);
        Assert.DoesNotContain(occupiedTick.ChangedStates,
            info => info.MonsterId == core.MonsterId && info.CurrentHealth == info.MaxHealth);

        manager.Tick(MatchingId, [], StartedAt.AddSeconds(12.2));
        Assert.Equal(core.MaxHealth,
            manager.GetSnapshot(MatchingId).Single(info => info.MonsterId == core.MonsterId).CurrentHealth);
    }

    [Fact]
    public void LethalDamageReportsFirstLastAndAllDamageContributors()
    {
        var manager = CreateManager();
        int monsterId = FirstEscortId(manager);

        manager.ApplyDamage(MatchingId, monsterId, 10, 3, StartedAt);
        var lethal = manager.ApplyDamage(MatchingId, monsterId, 20, 99, StartedAt.AddSeconds(1));

        Assert.True(lethal.Killed);
        Assert.Equal(10, lethal.FirstAttackerPlayerId);
        Assert.Equal(20, lethal.LastAttackerPlayerId);
        Assert.Equal(3, lethal.DamageByPlayer![10]);
        Assert.Equal(99, lethal.DamageByPlayer[20]);
    }
    [Fact]
    public void MarkedNormal_DespawnsWithoutTimeBasedRespawn_AndAwardsOneStone()
    {
        var manager = CreateManager();
        int monsterId = manager.GetSnapshot(MatchingId, AreaType.S2Library1)
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
    public void Reinforcement_ReleasesTwoAfterDelay_AndPaysFromRewardBudget()
    {
        var manager = CreateManager();
        KillRoomMonsters(manager, AreaType.S2Library1, 2);
        var target = FarTarget(AreaType.S2Library1);

        var scheduled = manager.Tick(MatchingId, [target], StartedAt);
        Assert.Empty(scheduled.ReinforcementReleases);
        Assert.True(Assert.Single(manager.GetReinforcementSnapshot(MatchingId),
            state => state.Area == AreaType.S2Library1).IsReleasePending);

        Assert.Empty(manager.Tick(MatchingId, [target], StartedAt.AddSeconds(1.49))
            .ReinforcementReleases);
        var releasedTick = manager.Tick(MatchingId, [target], StartedAt.AddSeconds(1.5));
        var release = Assert.Single(releasedTick.ReinforcementReleases);

        Assert.Equal(AreaType.S2Library1, release.Area);
        Assert.Equal(2, release.ReleasedCount);
        // 보상 예산 10에서 사전 킬 2가 차감된 잔액이 릴리즈 레코드에 실린다.
        Assert.Equal(8, release.RemainingBudget);
        Assert.Equal(9, release.AliveCountAfterRelease);
        Assert.Equal(2, releasedTick.SpawnedStates.Count(state =>
            state.AreaType == AreaType.S2Library1 && state.SummonStoneReward == 1));

        int reinforcementId = releasedTick.SpawnedStates
            .First(state => state.AreaType == AreaType.S2Library1 && state.SummonStoneReward == 1)
            .MonsterId;
        var lethal = manager.ApplyDamage(
            MatchingId, reinforcementId, 10, 999, StartedAt.AddSeconds(1.6));
        Assert.True(lethal.Killed);
        Assert.True(lethal.IsReinforcement);
        // 증원도 보상 예산이 남아 있는 동안은 1석을 지급한다.
        Assert.Equal(1, lethal.SummonStoneReward);
    }

    [Fact]
    public void Reinforcement_PausesWhenRoomIsEmpty_AndRestartsFullDelayOnReentry()
    {
        var manager = CreateManager();
        KillRoomMonsters(manager, AreaType.S2Library1, 2);
        var firstRelease = ReleaseReinforcementPair(manager, AreaType.S2Library1, StartedAt);
        Assert.Equal(8, Assert.Single(firstRelease.ReinforcementReleases).RemainingBudget);

        KillRoomMonsters(manager, AreaType.S2Library1, 2);
        var target = FarTarget(AreaType.S2Library1);

        manager.Tick(MatchingId, [target], StartedAt.AddSeconds(2));
        manager.Tick(MatchingId, [], StartedAt.AddSeconds(3));
        Assert.False(Assert.Single(manager.GetReinforcementSnapshot(MatchingId),
            state => state.Area == AreaType.S2Library1).IsReleasePending);

        manager.Tick(MatchingId, [target], StartedAt.AddSeconds(10));
        Assert.Empty(manager.Tick(MatchingId, [target], StartedAt.AddSeconds(11.49))
            .ReinforcementReleases);
        var released = Assert.Single(manager.Tick(
            MatchingId, [target], StartedAt.AddSeconds(11.5)).ReinforcementReleases);

        Assert.Equal(2, released.ReleasedCount);
        Assert.Equal(6, released.RemainingBudget);
    }

    [Fact]
    public void Reinforcement_KeepsRefillingBodies_WhileRewardBudgetDrains()
    {
        var manager = CreateManager();
        var when = StartedAt;

        // 옛 몸 예산(4)을 넘어서도 방출이 계속된다 — 죽은 증원 슬롯이 재사용된다.
        for (int round = 0; round < 3; round++)
        {
            KillRoomMonsters(manager, AreaType.S2Library1, 2);
            var released = ReleaseReinforcementPair(manager, AreaType.S2Library1, when);
            Assert.Equal(2, Assert.Single(released.ReinforcementReleases).ReleasedCount);
            when = when.AddSeconds(2);
        }

        var refilled = Assert.Single(manager.GetReinforcementSnapshot(MatchingId),
            state => state.Area == AreaType.S2Library1);
        Assert.Equal(6, refilled.TotalReleased);
        Assert.Equal(4, refilled.RemainingBudget);

        // 남은 보상 예산 4가 마르면 이후 처치는 0석이 된다. 몸 방출은 계속된다.
        for (int paidKill = 0; paidKill < 4; paidKill++)
        {
            var paid = manager.ApplyDamage(
                MatchingId, FirstAliveNonCoreId(manager), 10, 999, when);
            Assert.True(paid.Killed);
            Assert.Equal(1, paid.SummonStoneReward);
        }

        var unpaid = manager.ApplyDamage(MatchingId, FirstAliveNonCoreId(manager), 10, 999, when);
        Assert.True(unpaid.Killed);
        Assert.Equal(0, unpaid.SummonStoneReward);

        var drained = ReleaseReinforcementPair(manager, AreaType.S2Library1, when.AddSeconds(2));
        var release = Assert.Single(drained.ReinforcementReleases);
        Assert.Equal(2, release.ReleasedCount);
        Assert.Equal(0, release.RemainingBudget);
    }

    [Fact]
    public void AreaWaveClears_WhenCoreDies_WhileBodiesKeepSpawning()
    {
        var manager = CreateManager();

        // 핵 외 전원을 잡아도 클리어가 아니다.
        foreach (var monster in manager.GetSnapshot(MatchingId, AreaType.S2Library1)
                     .Where(info => info.IsAlive && !info.IsCore))
            manager.ApplyDamage(MatchingId, monster.MonsterId, 10, 999, StartedAt);
        Assert.False(manager.IsAreaWaveCleared(MatchingId, AreaType.S2Library1));

        // 핵 처치 = 클리어. 무한 리필에서 "전부 정리"는 성립하지 않으므로 유한한 핵이 방의 목표다.
        int coreId = manager.GetSnapshot(MatchingId, AreaType.S2Library1)
            .Single(info => info.IsAlive && info.IsCore).MonsterId;
        manager.ApplyDamage(MatchingId, coreId, 10, 999, StartedAt.AddSeconds(1));
        Assert.True(manager.IsAreaWaveCleared(MatchingId, AreaType.S2Library1));

        // 클리어 후 증원이 계속 나와도 클리어 상태는 유지된다.
        var released = ReleaseReinforcementPair(manager, AreaType.S2Library1, StartedAt.AddSeconds(2));
        Assert.NotEmpty(released.ReinforcementReleases);
        Assert.True(manager.IsAreaWaveCleared(MatchingId, AreaType.S2Library1));
    }

    [Fact]
    public void Closure_RemovesClosedHotspot_AndRefreshesSurvivingPhaseBudgets()
    {
        var manager = CreateManager();

        Assert.True(manager.ApplyAreaClosureAndSpawnWave(
            MatchingId, [AreaType.S2ExamRoom], StartedAt));
        var refreshed = manager.GetReinforcementSnapshot(MatchingId);
        Assert.Equal(4, refreshed.Count);
        Assert.All(refreshed, state => Assert.Equal(1, state.PhaseIndex));
        Assert.All(refreshed, state => Assert.Equal(11, state.RemainingBudget));
        // 유리 떼: 스테이지가 오를수록 동시 상한이 오른다 (9 -> 12 -> 15 -> 18).
        Assert.All(refreshed, state => Assert.Equal(12, state.TargetAliveCount));

        Assert.True(manager.ApplyAreaClosureAndSpawnWave(
            MatchingId, [AreaType.S2Library1], StartedAt.AddSeconds(1)));
        Assert.DoesNotContain(manager.GetReinforcementSnapshot(MatchingId),
            state => state.Area == AreaType.S2Library1);
        var closedAreaTick = manager.Tick(
            MatchingId, [FarTarget(AreaType.S2Library1)], StartedAt.AddSeconds(10));
        Assert.DoesNotContain(closedAreaTick.ReinforcementReleases,
            release => release.Area == AreaType.S2Library1);
    }

    [Fact]
    public void DensitySample_UsesActualAttackableAreaInsteadOfAliveCountAlone()
    {
        var manager = CreateManager();
        var occupiedAreas = new HashSet<AreaType> { AreaType.S2Library1 };

        var outOfRange = Assert.Single(manager.SampleDensity(
            MatchingId,
            occupiedAreas,
            new HashSet<AreaType>(),
            StartedAt));
        Assert.Equal(AreaType.S2Library1, outOfRange.Area);
        Assert.True(outOfRange.AliveMonsterCount > 0);
        Assert.False(outOfRange.HasAttackableMonster);

        var inRange = Assert.Single(manager.SampleDensity(
            MatchingId,
            occupiedAreas,
            new HashSet<AreaType> { AreaType.S2Library1 },
            StartedAt.AddSeconds(1)));
        Assert.True(inRange.HasAttackableMonster);
    }

    [Fact]
    public void AreaClosure_RefillsOnlyEnoughOpenAreasToRestoreTheHotspotCap()
    {
        var manager = CreateManager();

        Assert.True(manager.ApplyAreaClosureAndSpawnWave(MatchingId, [AreaType.S2Library1], StartedAt));

        var snapshot = manager.GetSnapshot(MatchingId);
        Assert.All(snapshot.Where(info => info.AreaType == AreaType.S2Library1),
            info => Assert.False(info.IsAlive));
        Assert.Equal(99, snapshot.Count(info => info.IsAlive));

        var release = manager.Tick(MatchingId, [], StartedAt);
        Assert.Equal(9, release.SpawnedStates.Count);
        Assert.DoesNotContain(release.SpawnedStates, state => state.AreaType == AreaType.S2Library1);
        Assert.All(release.SpawnedStates.Where(state => !state.IsCore), state =>
        {
            Assert.Equal(12, state.MaxHealth);
            Assert.Equal(1, state.SummonStoneReward);
        });
        var strengthenedCore = Assert.Single(release.SpawnedStates, state => state.IsCore);
        Assert.Equal(72, strengthenedCore.MaxHealth);
        Assert.Equal(8, strengthenedCore.SummonStoneReward);
        var coreTarget = new MonsterSpatialTarget(
            10, Config.SWARM_MATCH_MAP, strengthenedCore.AreaType,
            new Vector3f(strengthenedCore.PositionX, strengthenedCore.PositionY, 0f));
        var strengthenedCoreAttack = Assert.Single(manager.Tick(
            MatchingId, [coreTarget], StartedAt.AddMilliseconds(100)).Attacks,
            attack => attack.MonsterId == strengthenedCore.MonsterId);
        Assert.Equal(10, strengthenedCoreAttack.Damage);
        // 승격 방출은 그 방의 기존 팩(8)을 강화 팩(9)으로 대체한다 — 순증 +1 (99 → 100).
        Assert.Equal(100, manager.GetSnapshot(MatchingId).Count(info => info.IsAlive));
        Assert.Equal(4, manager.GetRewardAreaSnapshot(MatchingId).Areas.Count);
        Assert.Contains(manager.GetRewardAreaSnapshot(MatchingId).Areas,
            area => area.Area == strengthenedCore.AreaType && area.RemainingSummonStoneReward == 16);

        var laterTick = manager.Tick(MatchingId, [], StartedAt.AddSeconds(20));
        Assert.Empty(laterTick.SpawnedStates);
        Assert.False(manager.ApplyAreaClosureAndSpawnWave(MatchingId, [AreaType.S2Library1], StartedAt));
    }

    [Fact]
    public void ClosureSchedule_ReducesActiveRewardAreasFromFourToOne()
    {
        var manager = CreateManager();

        Assert.Equal(4, manager.GetRewardAreaSnapshot(MatchingId).Areas.Count);

        manager.ApplyAreaClosureAndSpawnWave(MatchingId,
            [AreaType.S2ExamRoom, AreaType.S2BroadcastRoom, AreaType.S2Classroom2], StartedAt);
        Assert.Equal(4, manager.GetRewardAreaSnapshot(MatchingId).Areas.Count);

        manager.ApplyAreaClosureAndSpawnWave(MatchingId,
            [AreaType.S2Library1, AreaType.S2Classroom1], StartedAt.AddSeconds(60));
        Assert.Equal(3, manager.GetRewardAreaSnapshot(MatchingId).Areas.Count);

        manager.ApplyAreaClosureAndSpawnWave(MatchingId,
            [AreaType.S2Library2, AreaType.S2Storage], StartedAt.AddSeconds(120));
        Assert.Equal(2, manager.GetRewardAreaSnapshot(MatchingId).Areas.Count);

        manager.ApplyAreaClosureAndSpawnWave(MatchingId,
            [AreaType.S2Gym1, AreaType.S2NurseOffice,
                AreaType.S2AdminOffice1, AreaType.S2AdminOffice2], StartedAt.AddSeconds(180));
        Assert.Single(manager.GetRewardAreaSnapshot(MatchingId).Areas);
        // 틱 없는 순수 차감 검증이라 승격은 없다 — 마지막 생존 핫스팟은 강당2.
        Assert.Equal(AreaType.S2Gym2, manager.GetRewardAreaSnapshot(MatchingId).Areas[0].Area);
    }

    [Fact]
    public void Tick_ChasesSameAreaTargetBeyondFormerLeashRange()
    {
        var manager = CreateManager();
        var first = manager.GetAliveTargets(MatchingId).First(target => target.Area == AreaType.S2Library1);
        var targetPosition = new Vector3f(first.Position.X + 5f, first.Position.Y + 3f, 0f);

        var tick = manager.Tick(MatchingId,
        [
            new MonsterSpatialTarget(10, Config.SWARM_MATCH_MAP, AreaType.S2Library1, targetPosition)
        ], StartedAt.AddMilliseconds(50));

        var moved = Assert.Single(tick.ChangedStates, info => info.MonsterId == first.MonsterId);
        Assert.NotEqual(first.Position.X, moved.PositionX);
        Assert.Empty(tick.Attacks);
    }

    [Fact]
    public void Tick_PrefersHighestTotalDamageWhenSeveralPlayersAreInTheSameArea()
    {
        var manager = CreateManager();
        var first = manager.GetAliveTargets(MatchingId).First(target => target.Area == AreaType.S2Library1);
        manager.ApplyDamage(MatchingId, first.MonsterId, 20, 1, StartedAt);
        manager.ApplyDamage(MatchingId, first.MonsterId, 10, 3, StartedAt);

        var attacks = manager.Tick(MatchingId,
        [
            new MonsterSpatialTarget(10, Config.SWARM_MATCH_MAP, AreaType.S2Library1, first.Position),
            new MonsterSpatialTarget(20, Config.SWARM_MATCH_MAP, AreaType.S2Library1, first.Position)
        ], StartedAt).Attacks;

        Assert.NotEmpty(attacks);
        Assert.All(attacks, attack => Assert.Equal(10, attack.TargetPlayerId));
    }

    [Fact]
    public void Tick_BucketsTargetsByMapAndAreaBeforeSelecting()
    {
        var manager = CreateManager();
        var first = manager.GetAliveTargets(MatchingId).First(target => target.Area == AreaType.S2Library1);

        var attacks = manager.Tick(MatchingId,
        [
            new MonsterSpatialTarget(99, MapId.None, AreaType.S2Library1, first.Position),
            new MonsterSpatialTarget(10, Config.SWARM_MATCH_MAP, AreaType.S2Library1, first.Position)
        ], StartedAt).Attacks;

        Assert.NotEmpty(attacks);
        Assert.All(attacks, attack => Assert.Equal(10, attack.TargetPlayerId));
    }

    [Fact]
    public void SpawnGroups_AssignNineDeterministicFormationSlots()
    {
        var manager = CreateManager();

        var libraryGroups = manager.GetAliveTargets(MatchingId)
            .Where(target => target.Area == AreaType.S2Gym1)
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
        // #272 School2: 표본 방(도서관1) 팩 옆에 목표를 둔다 — 좌표 하드코딩 대신 스폰 위치 기준.
        var anchor = first.GetAliveTargets(MatchingId)
            .First(monster => monster.Area == AreaType.S2Library1).Position;
        var targetPosition = new Vector3f(anchor.X + 2f, anchor.Y + 1f, 0f);
        var target = new MonsterSpatialTarget(10, Config.SWARM_MATCH_MAP, AreaType.S2Library1, targetPosition);

        Advance(first, [target], StartedAt, 90);
        Advance(second, [target], StartedAt, 90);

        var firstFormation = first.GetAliveTargets(MatchingId)
            .Where(monster => monster.Area == AreaType.S2Library1)
            .OrderBy(monster => monster.MonsterId)
            .ToList();
        var secondFormation = second.GetAliveTargets(MatchingId)
            .Where(monster => monster.Area == AreaType.S2Library1)
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
            .Where(target => target.Area == AreaType.S2Library1 && !target.IsCore &&
                             target.ClusterMemberIndex < target.ClusterSize - 3)
            .Select(target => target.MonsterId)
            .ToHashSet();
        var initialEscorts = manager.GetSnapshot(MatchingId, AreaType.S2Library1)
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
    public void MatchSeed_AssignsOneAffinityPerArea_AndExposesAllThreeInCoreHotspots()
    {
        var manager = CreateManager();
        var roomTargets = manager.GetAliveTargets(MatchingId)
            .Where(target => !target.Area.IsCorridor())
            .ToList();

        Assert.All(roomTargets.GroupBy(target => target.ClusterId),
            pack => Assert.Single(pack.Select(target => target.RewardItemId).Distinct()));

        var affinityByArea = roomTargets
            .GroupBy(target => target.Area)
            .ToDictionary(group => group.Key, group => group.Select(target => target.RewardItemId).Distinct().ToList());
        Assert.Equal(13, affinityByArea.Count);
        Assert.All(affinityByArea.Values, affinities => Assert.Single(affinities));
        Assert.DoesNotContain(affinityByArea.Values, affinities => affinities.Contains(107000040));

        var hotspotAffinities = roomTargets
            .Where(target => target.IsCore)
            .Select(target => target.RewardItemId)
            .ToList();
        Assert.Equal(4, hotspotAffinities.Count);
        Assert.Equal(3, hotspotAffinities.Distinct().Count());
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
    public void CorridorPressure_SpawnsAwayFromPlayers_AwardsBaseStone_AndDespawnsAfterFourSeconds()
    {
        var manager = CreateManager();
        var corridorTarget = new MonsterSpatialTarget(10, Config.SWARM_MATCH_MAP, AreaType.S2Corridor1, new Vector3f(35f, 52.5f, 0f));

        var spawned = manager.Tick(MatchingId, [corridorTarget], StartedAt);
        var neutral = Assert.Single(spawned.ChangedStates, info => info.AreaType == AreaType.S2Corridor1 && info.RewardItemId == 0);
        Assert.Equal(AreaType.S2Corridor1, neutral.AreaType);
        Assert.True(neutral.IsAlive);
        Assert.Equal(0, neutral.RewardItemId);
        Assert.False(neutral.IsCore);
        Assert.Equal(1, neutral.SummonStoneReward);
        Assert.True(Distance(new Vector3f(neutral.PositionX, neutral.PositionY, 0f), corridorTarget.Position) >= 3f);

        var lethal = manager.ApplyDamage(MatchingId, neutral.MonsterId, 10, 999, StartedAt.AddMilliseconds(1));
        Assert.True(lethal.Killed);
        Assert.Equal(1, lethal.SummonStoneReward);

        // Spawn another pressure monster, then verify the no-target timeout removes it.
        var respawned = manager.Tick(MatchingId, [corridorTarget], StartedAt.AddSeconds(4));
        var second = Assert.Single(respawned.ChangedStates, info => info.AreaType == AreaType.S2Corridor1 && info.IsAlive && info.RewardItemId == 0);
        var despawned = manager.Tick(MatchingId, [], StartedAt.AddSeconds(8.1));
        var removed = Assert.Single(despawned.ChangedStates, info => info.MonsterId == second.MonsterId);
        Assert.False(removed.IsAlive);
    }

    [Fact]
    public void CorridorPressure_SpawnsUpToSixAtFourSecondCadence()
    {
        var manager = CreateManager();
        var corridorTarget = new MonsterSpatialTarget(
            10, Config.SWARM_MATCH_MAP, AreaType.S2Corridor1, new Vector3f(0f, 0f, 0f));

        for (int index = 0; index < 7; index++)
            manager.Tick(MatchingId, [corridorTarget], StartedAt.AddSeconds(index * 4));

        // #272 가운데 병합: 앵커가 복도당 2점으로 재배치 — 한 복도의 동시 압박 상한은 2다.
        Assert.Equal(2, manager.GetSnapshot(MatchingId)
            .Count(info => info.AreaType == AreaType.S2Corridor1 && info.IsAlive && info.RewardItemId == 0));
    }

    [Fact]
    public void CorridorPressure_DoublesDamageAndRewardAfterEveryClosurePhase()
    {
        var manager = CreateManager();
        var corridorTarget = new MonsterSpatialTarget(
            10, Config.SWARM_MATCH_MAP, AreaType.S2Corridor1, new Vector3f(0f, 0f, 0f));

        manager.Tick(MatchingId, [corridorTarget], StartedAt);
        var corridorMonster = manager.GetSnapshot(MatchingId)
            .Single(info => info.AreaType == AreaType.S2Corridor1 && info.IsAlive && info.RewardItemId == 0);
        var atMonster = corridorTarget with
        {
            Position = new Vector3f(corridorMonster.PositionX, corridorMonster.PositionY, 0f)
        };

        var baseAttack = Assert.Single(manager.Tick(
            MatchingId, [atMonster], StartedAt.AddMilliseconds(100)).Attacks);
        Assert.Equal(1, baseAttack.Damage);
        Assert.Equal(1, corridorMonster.SummonStoneReward);

        Assert.True(manager.ApplyAreaClosureAndSpawnWave(
            MatchingId, [AreaType.S2Library1], StartedAt.AddSeconds(1)));
        var phaseOne = manager.GetSnapshot(MatchingId)
            .Single(info => info.MonsterId == corridorMonster.MonsterId);
        Assert.Equal(2, phaseOne.SummonStoneReward);
        var phaseOneAttack = Assert.Single(manager.Tick(
            MatchingId, [atMonster], StartedAt.AddSeconds(2)).Attacks,
            attack => attack.MonsterId == corridorMonster.MonsterId);
        Assert.Equal(2, phaseOneAttack.Damage);

        Assert.True(manager.ApplyAreaClosureAndSpawnWave(
            MatchingId, [AreaType.S2Gym1], StartedAt.AddSeconds(3)));
        var phaseTwo = manager.GetSnapshot(MatchingId)
            .Single(info => info.MonsterId == corridorMonster.MonsterId);
        Assert.Equal(4, phaseTwo.SummonStoneReward);
        var phaseTwoAttack = Assert.Single(manager.Tick(
            MatchingId, [atMonster], StartedAt.AddSeconds(4)).Attacks,
            attack => attack.MonsterId == corridorMonster.MonsterId);
        Assert.Equal(4, phaseTwoAttack.Damage);
    }

    private static void KillRoomMonsters(
        EmotionAfterimageMonsterManager manager,
        AreaType area,
        int count)
    {
        var monsterIds = manager.GetSnapshot(MatchingId, area)
            .Where(state => state.IsAlive && !state.IsCore)
            .OrderBy(state => state.MonsterId)
            .Take(count)
            .Select(state => state.MonsterId)
            .ToList();
        Assert.Equal(count, monsterIds.Count);
        foreach (int monsterId in monsterIds)
            Assert.True(manager.ApplyDamage(MatchingId, monsterId, 10, 999, StartedAt).Killed);
    }

    private static MonsterSpatialTarget FarTarget(AreaType area) =>
        new(10, Config.SWARM_MATCH_MAP, area, new Vector3f(1_000f, 1_000f, 0f));

    private static MonsterTickResult ReleaseReinforcementPair(
        EmotionAfterimageMonsterManager manager,
        AreaType area,
        DateTime scheduledAt)
    {
        var target = FarTarget(area);
        manager.Tick(MatchingId, [target], scheduledAt);
        return manager.Tick(MatchingId, [target], scheduledAt.AddSeconds(1.5));
    }

    private static EmotionAfterimageMonsterManager CreateManager()
    {
        var manager = new EmotionAfterimageMonsterManager();
        manager.InitializeMatching(MatchingId);
        return manager;
    }

    private static int FirstAliveNonCoreId(EmotionAfterimageMonsterManager manager) =>
        manager.GetSnapshot(MatchingId, AreaType.S2Library1)
            .First(state => state.IsAlive && !state.IsCore)
            .MonsterId;

    private static int FirstEscortId(EmotionAfterimageMonsterManager manager) =>
        manager.GetAliveTargets(MatchingId)
            .First(target => target.Area == AreaType.S2Library1 && !target.IsCore &&
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
