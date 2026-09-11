using game_server.matches.combat;
using Microsoft.Extensions.Logging.Abstractions;
using network.common.data;

namespace demo_regression_tests;

public sealed class MatchCombatDamageServiceTests
{
    [Fact]
    public void ScheduledHitsWaitUntilDueAndAreConsumedOnceOnlyInTheirOwnMatch()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var combat = TestGameSessionServices.CreateCombatDamageService(store.EventLogs);
        var health = TestGameSessionServices.CreateHealthService(store, store.EventLogs, new game_server.matches.results.MatchSummaryFileStore(), NullLogger.Instance);
        var first = store.GetOrCreate(982003);
        var second = store.GetOrCreate(982004);
        var dueAt = DateTime.UtcNow.AddSeconds(1);
        var attack = new ProximityCombatAttack(1001, 1002, default, 0, 1, 0, 0);

        using (first.Enter())
        {
            combat.ScheduleMonsterHit(first, new PendingMonsterHit(-123, 1001, 1, dueAt));
            combat.SchedulePvpHit(first, attack, dueAt);
            combat.ProcessPendingMonsterHits(first, dueAt.AddTicks(-1), []);
            combat.ProcessPendingPvpHits(first, health, dueAt.AddTicks(-1), [], []);
            Assert.Single(first.CombatDamage.PendingMonsterHits);
            Assert.Single(first.CombatDamage.PendingPvpHits);
        }
        using (second.Enter())
        {
            combat.ProcessPendingMonsterHits(second, dueAt, []);
            combat.ProcessPendingPvpHits(second, health, dueAt, [], []);
            Assert.Empty(second.CombatDamage.PendingMonsterHits);
            Assert.Empty(second.CombatDamage.PendingPvpHits);
        }
        using (first.Enter())
        {
            Assert.Single(first.CombatDamage.PendingMonsterHits);
            Assert.Single(first.CombatDamage.PendingPvpHits);
            combat.ProcessPendingMonsterHits(first, dueAt, []);
            combat.ProcessPendingPvpHits(first, health, dueAt, [], []);
            combat.ProcessPendingMonsterHits(first, dueAt.AddSeconds(1), []);
            combat.ProcessPendingPvpHits(first, health, dueAt.AddSeconds(1), [], []);
            Assert.Empty(first.CombatDamage.PendingMonsterHits);
            Assert.Empty(first.CombatDamage.PendingPvpHits);
        }
    }

    [Fact]
    public void FractionalPvpDamageAccumulatesSeparatelyForEachVictim()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(982001);
        var second = store.GetOrCreate(982002);
        float rate = SwarmConfigData.GetFloat("SWARM_PVP_DAMAGE_PER_DAMAGE", 0.12f);
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
