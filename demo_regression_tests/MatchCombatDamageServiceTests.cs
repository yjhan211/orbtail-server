using Microsoft.Extensions.Logging.Abstractions;
using network.common.data;

namespace demo_regression_tests;

public sealed class MatchCombatDamageServiceTests
{
    [Fact]
    public void ScheduledHitsWaitUntilDueAndAreConsumedOnceOnlyInTheirOwnMatch()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(982003);
        var second = store.GetOrCreate(982004);
        var dueAt = DateTime.UtcNow.AddSeconds(1);
        var attack = new game_server.combat.ProximityCombatAttack(1001, 1002, default, 0, 1, 0, 0);

        using (first.Enter())
        {
            first.CombatDamage.ScheduleMonsterHit(new game_server.combat.PendingMonsterHit(-123, 1001, 1, dueAt));
            first.CombatDamage.SchedulePvpHit(attack, dueAt);
            first.CombatDamage.ProcessPendingMonsterHits(dueAt.AddTicks(-1), []);
            first.CombatDamage.ProcessPendingPvpHits(TestGameSessionServices.CreateHealthService(store, store.EventLogs, new game_server.matches.results.MatchSummaryFileStore(), NullLogger.Instance), dueAt.AddTicks(-1), [], []);
            Assert.Equal(1, PendingCount(first.CombatDamage, "_pendingMonsterHits"));
            Assert.Equal(1, PendingCount(first.CombatDamage, "_pendingPvpHits"));
        }
        using (second.Enter())
        {
            second.CombatDamage.ProcessPendingMonsterHits(dueAt, []);
            second.CombatDamage.ProcessPendingPvpHits(TestGameSessionServices.CreateHealthService(store, store.EventLogs, new game_server.matches.results.MatchSummaryFileStore(), NullLogger.Instance), dueAt, [], []);
            Assert.Equal(0, PendingCount(second.CombatDamage, "_pendingMonsterHits"));
            Assert.Equal(0, PendingCount(second.CombatDamage, "_pendingPvpHits"));
        }
        using (first.Enter())
        {
            Assert.Equal(1, PendingCount(first.CombatDamage, "_pendingMonsterHits"));
            Assert.Equal(1, PendingCount(first.CombatDamage, "_pendingPvpHits"));
            first.CombatDamage.ProcessPendingMonsterHits(dueAt, []);
            first.CombatDamage.ProcessPendingPvpHits(TestGameSessionServices.CreateHealthService(store, store.EventLogs, new game_server.matches.results.MatchSummaryFileStore(), NullLogger.Instance), dueAt, [], []);
            first.CombatDamage.ProcessPendingMonsterHits(dueAt.AddSeconds(1), []);
            first.CombatDamage.ProcessPendingPvpHits(TestGameSessionServices.CreateHealthService(store, store.EventLogs, new game_server.matches.results.MatchSummaryFileStore(), NullLogger.Instance), dueAt.AddSeconds(1), [], []);
            Assert.Equal(0, PendingCount(first.CombatDamage, "_pendingMonsterHits"));
            Assert.Equal(0, PendingCount(first.CombatDamage, "_pendingPvpHits"));
        }
    }

    private static int PendingCount(object service, string fieldName) =>
        ((System.Collections.ICollection)service.GetType().GetField(fieldName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(service)!).Count;

    [Fact]
    public void FractionalPvpDamageAccumulatesSeparatelyForEachVictimAndMatch()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance);
        var first = store.GetOrCreate(982001);
        var second = store.GetOrCreate(982002);
        float rate = SwarmConfigData.GetFloat("SWARM_PVP_DAMAGE_PER_DAMAGE", 0.12f);
        float carry = 0f;

        using (first.Enter())
        {
            for (int index = 0; index < 20; index++)
            {
                float total = carry + rate;
                int expected = (int)total;
                carry = total - expected;
                Assert.Equal(expected, first.CombatDamage.ConsumeSwarmPvpDamage(1001, 1));
            }
            for (int index = 0; index < 20; index++)
                Assert.Equal((int)rate, first.CombatDamage.ConsumeSwarmPvpDamage(2000 + index, 1));
        }

        using (second.Enter())
            Assert.Equal((int)rate, second.CombatDamage.ConsumeSwarmPvpDamage(1001, 1));
    }
}
