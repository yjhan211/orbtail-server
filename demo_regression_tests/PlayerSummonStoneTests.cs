using game_server.matches;
using game_server.players;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

/// <summary>소환석 지급·비용·후보·지급 규칙. 상태는 Player가, 규칙은 PlayerOrbGrowthService가 갖는다.</summary>
public class PlayerSummonStoneTests
{
    public PlayerSummonStoneTests()
    {
        TestGameData.EnsureBattleItemCombatLoaded();
    }

    private static (MatchRuntime Match, Player Player) Create(long matchingId, long playerId = 10)
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var match = store.GetOrCreate(matchingId);
        return (match, TestGameSessionServices.GetOrRegisterPlayer(match, playerId));
    }

    private static Func<int, InGameItemInfo?> Grant(long uid) =>
        itemId => new InGameItemInfo { ItemUid = uid, ItemId = itemId, Count = 1 };

    [Fact]
    public void StartingStones_GrantTwoOpeningSummons()
    {
        var (match, player) = Create(202);
        using (match.Enter())
        {
            var granted = PlayerOrbGrowthService.AddSummonStones(match, player, PlayerOrbGrowthService.InitialSummonStoneCount);
            Assert.Equal(5, granted.StoneCount);

            var summon = PlayerOrbGrowthService.TrySummon(match, player, Grant(1));
            Assert.True(summon.Success);
            Assert.Equal(3, summon.State.StoneCount);
            Assert.Equal(3, summon.State.NextCost);

            var secondSummon = PlayerOrbGrowthService.TrySummon(match, player, Grant(2));
            Assert.True(secondSummon.Success);
            Assert.Equal(0, secondSummon.State.StoneCount);
            Assert.Equal(6, secondSummon.State.NextCost);
        }
    }

    [Fact]
    public void SummonCandidates_AreDeterministicDistinctAndFirstIsGranted()
    {
        var (match, player) = Create(214);
        using (match.Enter())
        {
            PlayerOrbGrowthService.AddSummonStones(match, player, PlayerOrbGrowthService.InitialSummonStoneCount);

            // 결정론: 같은 상태에서 몇 번을 조회해도 같은 후보. 재접속 복원의 전제다.
            var candidates = PlayerOrbGrowthService.GetSummonCandidates(match, player);
            Assert.Equal(candidates, PlayerOrbGrowthService.GetSummonCandidates(match, player));
            Assert.Equal(2, candidates.Length);

            // 같은 오브 두 개는 선택이 아니다 — 단, 공급 차단 토글로 풀이 1색이면 성립 불가.
            int enabledPoolSize = (Config.SWARM_SUN_ORB_ENABLED ? 1 : 0) +
                                  (Config.SWARM_WIND_ORB_ENABLED ? 1 : 0) +
                                  (Config.SWARM_WAVE_ORB_ENABLED ? 1 : 0);
            if (enabledPoolSize > 1)
                Assert.NotEqual(candidates[0], candidates[1]);

            // 첫 후보가 실제 지급 대상이다.
            var summon = PlayerOrbGrowthService.TrySummon(match, player, Grant(1));
            Assert.True(summon.Success);
            Assert.Equal(candidates[0], summon.ItemId);

            // 소환 후에는 다음 소환 횟수 기준의 새 후보가 나온다 (풀 1색이면 같을 수밖에 없다).
            if (enabledPoolSize > 1)
                Assert.NotEqual(candidates, PlayerOrbGrowthService.GetSummonCandidates(match, player));
        }
    }

    [Fact]
    public void FirstSummon_Candidates_AreBothAttackOrbs()
    {
        var (match, _) = Create(214);
        for (long playerId = 1; playerId <= 40; playerId++)
        {
            var player = new Player { Profile = new PlayerInfo { PlayerId = playerId } };
            var candidates = PlayerOrbGrowthService.GetSummonCandidates(match, player);
            Assert.All(candidates, itemId => Assert.False(OrbData.IsRecoveryOrb(itemId)));
        }
    }

    [Fact]
    public void FirstSummon_IsAlwaysAnAttackOrb()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        for (long matchingId = 1; matchingId <= 16; matchingId++)
        {
            var match = store.GetOrCreate(matchingId);
            using (match.Enter())
            {
                for (long playerId = 1; playerId <= 16; playerId++)
                {
                    var player = TestGameSessionServices.GetOrRegisterPlayer(match, playerId);
                    PlayerOrbGrowthService.AddSummonStones(match, player, PlayerOrbGrowthService.InitialSummonStoneCount);
                    var summon = PlayerOrbGrowthService.TrySummon(match, player, Grant(1));
                    Assert.True(summon.Success);
                    Assert.False(OrbData.IsRecoveryOrb(summon.ItemId));
                }
            }
        }
    }

    [Fact]
    public void MonsterRewards_AccumulateInOneAuthoritativeBalance()
    {
        var (match, player) = Create(202);
        using (match.Enter())
        {
            PlayerOrbGrowthService.AddSummonStones(match, player, PlayerOrbGrowthService.NormalMonsterReward);
            var state = PlayerOrbGrowthService.AddSummonStones(match, player, PlayerOrbGrowthService.CoreMonsterReward);

            Assert.Equal(4, state.StoneCount);
            Assert.Equal(0, state.SuccessfulSummonCount);
            Assert.Equal(2, state.NextCost);
        }
    }

    [Fact]
    public void SuccessfulSummons_IncreaseCostWithoutCap()
    {
        var (match, player) = Create(202);
        using (match.Enter())
        {
            PlayerOrbGrowthService.AddSummonStones(match, player, 100);
            long nextUid = 1;
            int[] expectedNextCosts = [3, 6, 10, 15, 21, 28];

            foreach (int expectedNextCost in expectedNextCosts)
            {
                var attempt = PlayerOrbGrowthService.TrySummon(match, player, Grant(nextUid++));
                Assert.True(attempt.Success);
                Assert.Equal(expectedNextCost, attempt.State.NextCost);
                Assert.Contains(attempt.ItemId, PlayerOrbGrowthService.SummonPoolItemIds);
            }

            Assert.Equal(43, PlayerOrbGrowthService.GetSummonStones(player).StoneCount);
        }
    }

    [Fact]
    public void FullInventory_DoesNotSpendStonesOrAdvanceRandomResult()
    {
        var (blockedMatch, blockedPlayer) = Create(202);
        var (controlMatch, controlPlayer) = Create(202);
        SummonOrbAttempt blocked, retry, control;
        using (blockedMatch.Enter())
        {
            PlayerOrbGrowthService.AddSummonStones(blockedMatch, blockedPlayer, 10);
            blocked = PlayerOrbGrowthService.TrySummon(blockedMatch, blockedPlayer, _ => null);
            retry = PlayerOrbGrowthService.TrySummon(blockedMatch, blockedPlayer, Grant(1));
        }
        using (controlMatch.Enter())
        {
            PlayerOrbGrowthService.AddSummonStones(controlMatch, controlPlayer, 10);
            control = PlayerOrbGrowthService.TrySummon(controlMatch, controlPlayer, Grant(1));
        }

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
        var (match, player) = Create(202);
        using (match.Enter())
        {
            PlayerOrbGrowthService.AddSummonStones(match, player, 1);
            bool grantCalled = false;

            var attempt = PlayerOrbGrowthService.TrySummon(match, player, itemId =>
            {
                grantCalled = true;
                return new InGameItemInfo { ItemUid = 1, ItemId = itemId, Count = 1 };
            });

            Assert.False(attempt.Success);
            Assert.Equal(ErrorCode.INSUFFICIENT_CURRENCY, attempt.ErrorCode);
            Assert.False(grantCalled);
            Assert.Equal(new SummonStoneSnapshot(1, 0, 2), attempt.State);
        }
    }

    [Fact]
    public void StoneChanges_RequireTheMatchLock()
    {
        var (match, player) = Create(202);
        Assert.Throws<InvalidOperationException>(() => PlayerOrbGrowthService.AddSummonStones(match, player, 1));
        Assert.Throws<InvalidOperationException>(() => PlayerOrbGrowthService.TrySpendSummonStones(match, player, 1));
        Assert.Throws<InvalidOperationException>(() => PlayerOrbGrowthService.TrySummon(match, player, Grant(1)));
        Assert.Equal(SummonStoneSnapshot.Empty, PlayerOrbGrowthService.GetSummonStones(player));
    }

    [Fact]
    public void MatchEnd_DropsPlayerBalancesWithTheParticipants()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(202);
        TestGameSessionServices.AddSummonStones(runtime, 10, 9);
        TestGameSessionServices.AddSummonStones(runtime, 20, 7);

        using (MatchRuntimeStore.Enter(runtime)) { runtime.TryMarkEnded(); }

        Assert.Equal(SummonStoneSnapshot.Empty, TestGameSessionServices.SummonStones(runtime, 10));
        Assert.Equal(SummonStoneSnapshot.Empty, TestGameSessionServices.SummonStones(runtime, 20));
        Assert.Null(store.GetOrNull(202));
    }
}
