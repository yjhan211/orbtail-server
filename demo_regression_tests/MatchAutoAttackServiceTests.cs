using game_server.matches;
using game_server.players;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public class MatchAutoAttackServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Resolve_AttacksNearestTargetImmediatelyWithoutAim()
    {
        using var arena = new Arena(910, 1, 2, 3);
        var attack = Assert.Single(arena.Update([
            Actor(1, 0, 0, 107000010), Actor(2, 2, 0), Actor(3, 1, 0)], Now));
        Assert.Equal(1, attack.AttackerPlayerId);
        Assert.Equal(3, attack.TargetPlayerId);
        Assert.Equal(107000010, attack.WeaponItemId);
        Assert.Equal(6, attack.Damage);
    }

    [Fact]
    public void Resolve_RespectsAreaRangeAndCooldownBoundary()
    {
        using var arena = new Arena(911, 1, 2, 3);
        var attacker = Actor(1, 0, 0, 107000010);
        Assert.Empty(arena.Update([attacker, Actor(2, 4, 0), Actor(3, 1, 0, area: AreaType.S2Corridor3)], Now));
        var actors = new[] { attacker, Actor(2, 3, 0) };
        // 적이 없던 처리는 주기를 소비하지 않으며 경계에 진입한 틱부터 공격한다.
        Assert.Single(arena.Update(actors, Now));
        Assert.Empty(arena.Update(actors, Now.AddMilliseconds(1499)));
        Assert.Single(arena.Update(actors, Now.AddMilliseconds(1500)));
    }

    [Fact]
    public void Resolve_TargetSwitchPreservesCooldownWithoutNewAim()
    {
        using var arena = new Arena(912, 1, 2, 3);
        var attacker = Actor(1, 0, 0, 107000010);
        Assert.Single(arena.Update([attacker, Actor(2, 1, 0)], Now));
        Assert.Empty(arena.Update([attacker, Actor(3, 0.5f, 0)], Now.AddMilliseconds(50)));
        Assert.Empty(arena.Update([attacker, Actor(3, 0.5f, 0)], Now.AddMilliseconds(1499)));
        var attack = Assert.Single(arena.Update([attacker, Actor(3, 0.5f, 0)], Now.AddMilliseconds(1500)));
        Assert.Equal(3, attack.TargetPlayerId);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Resolve_TargetLossDoesNotPauseCooldownOrRequireReacquisition(long returningId)
    {
        using var arena = new Arena(913, 1, 2, 3);
        var attacker = Actor(1, 0, 0, 107000010);
        Assert.Single(arena.Update([attacker, Actor(2, 1, 0)], Now));
        Assert.Empty(arena.Update([attacker], Now.AddMilliseconds(100)));
        Assert.Empty(arena.Update([attacker, Actor(returningId, 1, 0)], Now.AddMilliseconds(1000)));
        var attack = Assert.Single(arena.Update([attacker, Actor(returningId, 1, 0)], Now.AddMilliseconds(1500)));
        Assert.Equal(returningId, attack.TargetPlayerId);
        Assert.Empty(arena.Update([attacker], Now.AddSeconds(2)));
        Assert.Single(arena.Update([attacker, Actor(returningId, 1, 0)], Now.AddSeconds(10)));
    }

    [Fact]
    public void Resolve_DoesNotKeepPreviousTargetOverCloserTarget()
    {
        using var arena = new Arena(914, 1, 2, 3);
        var attacker = Actor(1, 0, 0, 107000010);
        Assert.Equal(2, Assert.Single(arena.Update([attacker, Actor(2, 1, 0)], Now)).TargetPlayerId);
        var attack = Assert.Single(arena.Update([attacker, Actor(2, 1, 0), Actor(3, 0.25f, 0)], Now.AddSeconds(1.5)));
        Assert.Equal(3, attack.TargetPlayerId);
    }

    [Fact]
    public void Resolve_PreservesPlayerPriorityOverCloserMonster()
    {
        using var arena = new Arena(915, 1, 2);
        var attack = Assert.Single(arena.Update([
            Actor(1, 0, 0, 107000010), Actor(2, 2, 0),
            Actor(-202001, 0.5f, 0) with { IsMonsterTarget = true, TargetPriority = 2 }], Now));
        Assert.Equal(2, attack.TargetPlayerId);
    }

    [Fact]
    public void Resolve_UsesPlayerIdAsDeterministicTieBreaker()
    {
        using var arena = new Arena(916, 10, 2, 3);
        var attack = Assert.Single(arena.Update([
            Actor(10, 0, 0, 107000010), Actor(3, -1, 0), Actor(2, 1, 0)], Now));
        Assert.Equal(2, attack.TargetPlayerId);
    }

    [Fact]
    public void Resolve_RejectedTargetsDoNotConsumeCooldown()
    {
        using var arena = new Arena(917, 1, 2, 3);
        var attacker = Actor(1, 0, 0, 107000010);
        Assert.Empty(arena.Update([attacker, Actor(2, 1, 0)], Now, (_, _) => false));
        var attack = Assert.Single(arena.Update([attacker, Actor(2, 1, 0), Actor(3, 2, 0)], Now,
            (_, target) => target.PlayerId != 2));
        Assert.Equal(3, attack.TargetPlayerId);
    }

    [Fact]
    public void Resolve_MultipleOrbsHaveIndependentCooldownsAndDeduplicateTargets()
    {
        using var arena = new Arena(918, 1, 2);
        var actors = new[]
        {
            Actor(1, 0, 0, 107000010) with { WeaponItemUid = 101 },
            Actor(1, 0, 0, 107000011) with { WeaponItemUid = 102, AttackIntervalSeconds = 0.5f },
            Actor(2, 1, 0) with { WeaponItemUid = 201 },
            Actor(2, 1, 0) with { WeaponItemUid = 202 }
        };
        var first = arena.Update(actors, Now);
        Assert.Equal(2, first.Count);
        Assert.All(first, attack => Assert.Equal(1, attack.CandidateTargetCount));
        Assert.Equal(102, Assert.Single(arena.Update(actors, Now.AddMilliseconds(500))).AttackerItemUid);
    }

    [Fact]
    public void MatchOwnedCombat_IsolatesCooldownsAndStopsAfterEnd()
    {
        using var first = new Arena(919, 1, 2);
        using var second = new Arena(920, 1, 2);
        var actors = new[] { Actor(1, 0, 0, 107000010), Actor(2, 1, 0) };
        Assert.Single(first.Update(actors, Now));
        Assert.Single(second.Update(actors, Now));
        Assert.Empty(first.Update(actors, Now));
        first.Match.GetPlayer(1)!.Orbs.ScheduleNextOrbAttack(0, Now, 0d);
        Assert.Single(first.Update(actors, Now));
        Assert.Empty(second.Update(actors, Now));
        first.Match.TryMarkEnded();
        Assert.Empty(first.Update(actors, Now.AddSeconds(10)));
    }

    private sealed class Arena : IDisposable
    {
        private readonly IDisposable _lock;
        private readonly MatchAutoAttackService _service = new();
        public Arena(long matchingId, params long[] playerIds)
        {
            var store = TestGameSessionServices.CreateMatchRuntimeStore(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            Match = store.GetOrCreate(matchingId);
            foreach (long playerId in playerIds)
                Match.RegisterPlayer(new Player(new PlayerInfo { PlayerId = playerId }));
            _lock = MatchRuntimeStore.Enter(Match);
        }
        public MatchRuntime Match { get; }
        public IReadOnlyList<ProximityCombatAttack> Update(IReadOnlyList<ProximityCombatActor> actors, DateTime nowUtc,
            Func<ProximityCombatActor, ProximityCombatActor, bool>? canAttackTarget = null) =>
            _service.UpdateAttacks(Match, actors, nowUtc, canAttackTarget);
        public void Dispose() => _lock.Dispose();
    }

    private static ProximityCombatActor Actor(long playerId, float x, float y, int weaponItemId = 0,
        AreaType area = AreaType.S2Classroom1) =>
        new(playerId, area, new Vector3f(x, y, 0f), weaponItemId,
            weaponItemId > 0 ? 3f : 0f, weaponItemId > 0 ? 6 : 0, weaponItemId > 0 ? 1.5f : 0f);
}
