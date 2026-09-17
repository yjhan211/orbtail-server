using game_server.matches;
using game_server.players;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.models;

namespace server_tests;

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

    private static readonly PlayerOrbGrowthService Growth = new(NullLogger<PlayerOrbGrowthService>.Instance);

    [Fact]
    public void StartingStones_GrantTwoOpeningSummons()
    {
        var (match, player) = Create(202);
        using (match.Enter())
        {
            var granted = PlayerOrbGrowthService.AddSummonStones(match, player, Config.SWARM_STARTING_STONE_GRANT);
            Assert.Equal(5, granted.StoneCount);

            var summon = Growth.Summon(match, player);
            Assert.True(summon.Success);
            Assert.Equal(3, summon.State.StoneCount);
            Assert.Equal(3, summon.State.NextCost);

            var secondSummon = Growth.Summon(match, player);
            Assert.True(secondSummon.Success);
            Assert.Equal(0, secondSummon.State.StoneCount);
            Assert.Equal(6, secondSummon.State.NextCost);
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
                    PlayerOrbGrowthService.AddSummonStones(match, player, Config.SWARM_STARTING_STONE_GRANT);
                    var summon = Growth.Summon(match, player);
                    Assert.True(summon.Success);
                    Assert.True(OrbData.IsOrbItem(summon.ItemId));
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
            PlayerOrbGrowthService.AddSummonStones(match, player, 1);
            var state = PlayerOrbGrowthService.AddSummonStones(match, player, 3);

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
            int[] expectedNextCosts = [3, 6, 10, 15, 21, 28];

            foreach (int expectedNextCost in expectedNextCosts)
            {
                var attempt = Growth.Summon(match, player);
                Assert.True(attempt.Success);
                Assert.Equal(expectedNextCost, attempt.State.NextCost);
                Assert.Contains(attempt.ItemId, PlayerOrbGrowthService.SummonPoolItemIds);
            }

            Assert.Equal(43, player.Orbs.SummonStones.StoneCount);
        }
    }

    [Fact]
    public void FullInventory_DoesNotSpendStones()
    {
        var (match, player) = Create(202);
        PlayerOrbGrowthService.SummonResult blocked, retry;
        using (match.Enter())
        {
            PlayerOrbGrowthService.AddSummonStones(match, player, 10);
            // 꼬리를 상한까지 채우면 소환이 막히고, 비우면 같은 소환석으로 다시 된다.
            while (player.Orbs.TryAddOrbWithCapacity(107000010, Config.SWARM_ORB_CAPACITY, out _))
            {
            }
            blocked = Growth.Summon(match, player);
            player.Orbs.TakeAllOrbs();
            retry = Growth.Summon(match, player);
        }

        Assert.False(blocked.Success);
        Assert.Equal(ErrorCode.INVENTORY_FULL, blocked.ErrorCode);
        Assert.Equal(10, blocked.State.StoneCount);
        Assert.Equal(0, blocked.State.SuccessfulSummonCount);
        Assert.True(retry.Success);
        Assert.Contains(retry.ItemId, PlayerOrbGrowthService.SummonPoolItemIds);
        Assert.Equal(8, retry.State.StoneCount);
    }

    [Fact]
    public void InsufficientStones_DoesNotGrantOrChangeState()
    {
        var (match, player) = Create(202);
        using (match.Enter())
        {
            PlayerOrbGrowthService.AddSummonStones(match, player, 1);

            var attempt = Growth.Summon(match, player);

            Assert.False(attempt.Success);
            Assert.Equal(ErrorCode.INSUFFICIENT_CURRENCY, attempt.ErrorCode);
            Assert.Empty(player.Orbs.GetAllOrbs());
            Assert.Equivalent(new SummonStoneStateInfo(1, 0), attempt.State);
        }
    }

    [Fact]
    public void SummonRecalculatesCostInsteadOfTrustingDisplayValue()
    {
        var (match, player) = Create(205);
        using var scope = match.Enter();
        PlayerOrbGrowthService.AddSummonStones(match, player, 1);
        player.Orbs.SummonStones.NextCost = 0;
        var result = Growth.Summon(match, player);
        Assert.False(result.Success);
        Assert.Empty(player.Orbs.GetAllOrbs());
        Assert.Equal(ErrorCode.INSUFFICIENT_CURRENCY, result.ErrorCode);
        Assert.Equal(1, player.Orbs.SummonStones.StoneCount);
    }
    [Fact]
    public void StoneChanges_RequireTheMatchLock()
    {
        var (match, player) = Create(202);
        Assert.Throws<InvalidOperationException>(() => PlayerOrbGrowthService.AddSummonStones(match, player, 1));
        Assert.Throws<InvalidOperationException>(() => PlayerOrbGrowthService.TrySpendSummonStones(match, player, 1));
        Assert.Throws<InvalidOperationException>(() => Growth.Summon(match, player));
        Assert.Equivalent(SummonStoneStateInfo.Empty, player.Orbs.SummonStones);
    }

    [Fact]
    public void MatchEnd_DropsPlayerBalancesWithTheParticipants()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(202);
        TestGameSessionServices.AddSummonStones(runtime, 10, 9);
        TestGameSessionServices.AddSummonStones(runtime, 20, 7);

        using (MatchRuntimeStore.Enter(runtime)) { runtime.TryMarkEnded(); }

        Assert.Equivalent(SummonStoneStateInfo.Empty, TestGameSessionServices.SummonStones(runtime, 10));
        Assert.Equivalent(SummonStoneStateInfo.Empty, TestGameSessionServices.SummonStones(runtime, 20));
        Assert.Null(store.GetOrNull(202));
    }
}
