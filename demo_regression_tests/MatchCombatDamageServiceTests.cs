using game_server.matches;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;

namespace demo_regression_tests;

public sealed class MatchCombatDamageServiceTests
{
    [Fact]
    public void ScheduledHitsWaitUntilDueAndAreConsumedOnceOnlyInTheirOwnMatch()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var combat = TestGameSessionServices.CreateCombatDamageService();
        var first = store.GetOrCreate(982003);
        var second = store.GetOrCreate(982004);
        var dueAt = DateTime.UtcNow.AddSeconds(1);

        using (first.Enter())
        {
            combat.ScheduleMonsterHit(first, new PendingMonsterHit(-123, 1001, 1, dueAt));
            combat.ProcessPendingMonsterHits(first, dueAt.AddTicks(-1), []);
            Assert.Single(first.CombatDamage.PendingMonsterHits);
        }
        using (second.Enter())
        {
            combat.ProcessPendingMonsterHits(second, dueAt, []);
            Assert.Empty(second.CombatDamage.PendingMonsterHits);
        }
        using (first.Enter())
        {
            Assert.Single(first.CombatDamage.PendingMonsterHits);
            combat.ProcessPendingMonsterHits(first, dueAt, []);
            combat.ProcessPendingMonsterHits(first, dueAt.AddSeconds(1), []);
            Assert.Empty(first.CombatDamage.PendingMonsterHits);
        }
    }

    [Fact]
    public void FractionalPvpDamageAccumulatesSeparatelyForEachVictim()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(982001);
        var second = store.GetOrCreate(982002);
        float rate = Config.SWARM_PVP_DAMAGE_PER_DAMAGE;
        float carry = 0f;

        var victim = TestGameSessionServices.GetOrRegisterPlayer(first, 1001);
        for (int index = 0; index < 20; index++)
        {
            float total = carry + rate;
            int expected = (int)total;
            carry = total - expected;
            Assert.Equal(expected, MatchCombatDamageService.ConsumeSwarmPvpDamage(victim, 1));
        }
        for (int index = 0; index < 20; index++)
            Assert.Equal((int)rate, MatchCombatDamageService.ConsumeSwarmPvpDamage(TestGameSessionServices.GetOrRegisterPlayer(first, 2000 + index), 1));

        // 다른 매치의 같은 ID는 다른 Player라 잔여가 섞이지 않는다.
        Assert.Equal((int)rate, MatchCombatDamageService.ConsumeSwarmPvpDamage(TestGameSessionServices.GetOrRegisterPlayer(second, 1001), 1));
    }
}
