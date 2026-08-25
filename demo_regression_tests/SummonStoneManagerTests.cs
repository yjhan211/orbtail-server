using game_server.services;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public class SummonStoneManagerTests
{
    [Fact]
    public void StartingStones_GrantTwoOpeningSummonsAndDoNotDuplicate()
    {
        var manager = new SummonStoneManager();

        var first = manager.EnsureStartingStones(202, 10);
        var reconnect = manager.EnsureStartingStones(202, 10);
        var otherPlayer = manager.EnsureStartingStones(202, 20);

        Assert.Equal(5, first.StoneCount);
        Assert.Equal(first, reconnect);
        Assert.Equal(first, otherPlayer);

        var summon = manager.TrySummon(202, 10,
            itemId => new InGameItemInfo { ItemUid = 1, ItemId = itemId, Count = 1 });

        Assert.True(summon.Success);
        Assert.Equal(3, summon.State.StoneCount);
        Assert.Equal(3, summon.State.NextCost);

        var secondSummon = manager.TrySummon(202, 10,
            itemId => new InGameItemInfo { ItemUid = 2, ItemId = itemId, Count = 1 });
        Assert.True(secondSummon.Success);
        Assert.Equal(0, secondSummon.State.StoneCount);
        Assert.Equal(6, secondSummon.State.NextCost);
    }


    [Fact]
    public void SummonCandidates_AreDeterministicDistinctAndChoiceOneIsGranted()
    {
        var manager = new SummonStoneManager();
        manager.EnsureStartingStones(214, 10);

        // 결정론: 같은 상태에서 몇 번을 조회해도 같은 후보. 재접속 복원의 전제다.
        var candidates = manager.GetSummonCandidates(214, 10);
        Assert.Equal(candidates, manager.GetSummonCandidates(214, 10));
        Assert.Equal(2, candidates.Length);

        // 같은 오브 두 개는 선택이 아니다 — 단, 공급 차단 토글로 풀이 1색이면 성립 불가.
        int enabledPoolSize = 1 + (Config.SWARM_SUN_ORB_ENABLED ? 1 : 0) +
                              (Config.SWARM_WAVE_ORB_ENABLED ? 1 : 0);
        if (enabledPoolSize > 1)
            Assert.NotEqual(candidates[0], candidates[1]);

        // 선택 인덱스 1이 실제로 두 번째 후보를 지급한다.
        var summon = manager.TrySummon(214, 10,
            itemId => new InGameItemInfo { ItemUid = 1, ItemId = itemId, Count = 1 },
            choiceIndex: 1);
        Assert.True(summon.Success);
        Assert.Equal(candidates[1], summon.ItemId);

        // 소환 후에는 다음 소환 횟수 기준의 새 후보가 나온다 (풀 1색이면 같을 수밖에 없다).
        if (enabledPoolSize > 1)
            Assert.NotEqual(candidates, manager.GetSummonCandidates(214, 10));
    }

    [Fact]
    public void FirstSummon_Candidates_AreBothAttackOrbs()
    {
        var manager = new SummonStoneManager();
        for (long playerId = 1; playerId <= 40; playerId++)
        {
            var candidates = manager.GetSummonCandidates(214, playerId);
            Assert.All(candidates, itemId => Assert.False(OrbData.IsRecoveryOrb(itemId)));
        }
    }

    [Fact]
    public void FirstSummon_IsAlwaysAnAttackOrb()
    {
        for (long matchingId = 1; matchingId <= 16; matchingId++)
            for (long playerId = 1; playerId <= 16; playerId++)
            {
                var manager = new SummonStoneManager();
                manager.EnsureStartingStones(matchingId, playerId);

                var summon = manager.TrySummon(matchingId, playerId,
                    itemId => new InGameItemInfo { ItemUid = 1, ItemId = itemId, Count = 1 });

                Assert.True(summon.Success);
                Assert.False(OrbData.IsRecoveryOrb(summon.ItemId));
            }
    }

    [Fact]
    public void SummonResults_AreUnaffectedByRegionalAfterimagePlacement()
    {
        var beforeLayout = new SummonStoneManager();
        beforeLayout.AddStones(202, 10, 2);
        var expected = beforeLayout.TrySummon(202, 10,
            itemId => new InGameItemInfo { ItemUid = 1, ItemId = itemId, Count = 1 });

        var monsters = new EmotionAfterimageMonsterManager();
        monsters.InitializeMatching(202);
        monsters.InitializeMatching(203);

        var afterLayout = new SummonStoneManager();
        afterLayout.AddStones(202, 10, 2);
        var actual = afterLayout.TrySummon(202, 10,
            itemId => new InGameItemInfo { ItemUid = 1, ItemId = itemId, Count = 1 });

        Assert.True(expected.Success);
        Assert.True(actual.Success);
        Assert.Equal(expected.ItemId, actual.ItemId);
        Assert.Equal(expected.State, actual.State);
    }
    [Fact]
    public void MonsterRewards_AccumulateInOneAuthoritativeBalance()
    {
        var manager = new SummonStoneManager();

        manager.AddStones(202, 10, SummonStoneManager.NormalMonsterReward);
        var state = manager.AddStones(202, 10, SummonStoneManager.CoreMonsterReward);

        Assert.Equal(4, state.StoneCount);
        Assert.Equal(0, state.SuccessfulSummonCount);
        Assert.Equal(2, state.NextCost);
    }

    [Fact]
    public void SuccessfulSummons_IncreaseCostWithoutCap()
    {
        var manager = new SummonStoneManager();
        manager.AddStones(202, 10, 100);
        long nextUid = 1;
        int[] expectedNextCosts = [3, 6, 10, 15, 21, 28];

        for (int index = 0; index < expectedNextCosts.Length; index++)
        {
            var attempt = manager.TrySummon(202, 10,
                itemId => new InGameItemInfo { ItemUid = nextUid++, ItemId = itemId, Count = 1 });

            Assert.True(attempt.Success);
            Assert.Equal(expectedNextCosts[index], attempt.State.NextCost);
            Assert.Contains(attempt.ItemId, manager.PoolItemIds);
        }

        Assert.Equal(43, manager.GetSnapshot(202, 10).StoneCount);
    }

    [Fact]
    public void FullInventory_DoesNotSpendStonesOrAdvanceRandomResult()
    {
        var blockedManager = new SummonStoneManager();
        blockedManager.AddStones(202, 10, 10);

        var blocked = blockedManager.TrySummon(202, 10, _ => null);
        var retry = blockedManager.TrySummon(202, 10,
            itemId => new InGameItemInfo { ItemUid = 1, ItemId = itemId, Count = 1 });

        var controlManager = new SummonStoneManager();
        controlManager.AddStones(202, 10, 10);
        var control = controlManager.TrySummon(202, 10,
            itemId => new InGameItemInfo { ItemUid = 1, ItemId = itemId, Count = 1 });

        Assert.False(blocked.Success);
        Assert.Equal(ErrorCode.INVENTORY_FULL, blocked.ErrorCode);
        Assert.Equal(10, blocked.State.StoneCount);
        Assert.Equal(0, blocked.State.SuccessfulSummonCount);
        Assert.True(retry.Success);
        Assert.Equal(control.ItemId, retry.ItemId);
        Assert.Equal(8, retry.State.StoneCount);
    }

    [Fact]
    public void InsufficientStones_DoesNotInvokeGrantOrChangeState()
    {
        var manager = new SummonStoneManager();
        manager.AddStones(202, 10, 1);
        bool grantCalled = false;

        var attempt = manager.TrySummon(202, 10, itemId =>
        {
            grantCalled = true;
            return new InGameItemInfo { ItemUid = 1, ItemId = itemId, Count = 1 };
        });

        Assert.False(attempt.Success);
        Assert.Equal(ErrorCode.INSUFFICIENT_CURRENCY, attempt.ErrorCode);
        Assert.False(grantCalled);
        Assert.Equal(new SummonStoneSnapshot(1, 0, 2), attempt.State);
    }

    [Fact]
    public void ConcurrentSummons_CannotSpendTheSameStonesTwice()
    {
        var manager = new SummonStoneManager();
        manager.AddStones(202, 10, 2);
        long nextUid = 0;
        var attempts = new System.Collections.Concurrent.ConcurrentBag<SummonOrbAttempt>();

        Parallel.For(0, 2, _ => attempts.Add(manager.TrySummon(202, 10,
            itemId => new InGameItemInfo
            {
                ItemUid = Interlocked.Increment(ref nextUid),
                ItemId = itemId,
                Count = 1
            })));

        Assert.Single(attempts, attempt => attempt.Success);
        Assert.Single(attempts, attempt => attempt.ErrorCode == ErrorCode.INSUFFICIENT_CURRENCY);
        Assert.Equal(new SummonStoneSnapshot(0, 1, 3), manager.GetSnapshot(202, 10));
    }

    [Fact]
    public void PassiveIncome_GrantsOneStoneEveryThirtyActiveSeconds()
    {
        var manager = new SummonStoneManager();

        var beforeGrant = manager.AdvancePassiveIncome(202, 10, 25, out int earlyAward);
        var firstGrant = manager.AdvancePassiveIncome(202, 10, 5, out int firstAward);
        var secondGrant = manager.AdvancePassiveIncome(202, 10, 30, out int secondAward);

        Assert.Equal(0, earlyAward);
        Assert.Equal(0, beforeGrant.StoneCount);
        Assert.Equal(1, firstAward);
        Assert.Equal(1, firstGrant.StoneCount);
        Assert.Equal(1, secondAward);
        Assert.Equal(2, secondGrant.StoneCount);
    }
    [Fact]
    public void RemoveMatchingState_ResetsAllPlayerBalances()
    {
        var manager = new SummonStoneManager();
        manager.AddStones(202, 10, 9);
        manager.AddStones(202, 20, 7);

        manager.RemoveMatchingState(202);

        Assert.Equal(new SummonStoneSnapshot(0, 0, 2), manager.GetSnapshot(202, 10));
        Assert.Equal(new SummonStoneSnapshot(0, 0, 2), manager.GetSnapshot(202, 20));
    }

    [Fact]
    public void ConcurrentRewards_DoNotLoseUpdates()
    {
        var manager = new SummonStoneManager();

        Parallel.For(0, 100, _ => manager.AddStones(202, 10, 1));

        Assert.Equal(100, manager.GetSnapshot(202, 10).StoneCount);
    }
}
