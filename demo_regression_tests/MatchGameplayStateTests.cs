using System.Collections.Concurrent;
using game_server.matches;
using game_server.players;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class MatchGameplayStateTests
{
    [Fact]
    public void StatusEffectsShareExpiryRulesWithoutSharingPlayerState()
    {
        var effects = new PlayerStatusEffects();
        var other = new PlayerStatusEffects();
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var kind in Enum.GetValues<PlayerStatusEffectKind>())
        {
            Assert.False(effects.IsActive(kind, now));
            effects.Apply(kind, now.AddSeconds(5));
            Assert.True(effects.IsActive(kind, now.AddSeconds(5).AddTicks(-1)));
            Assert.False(effects.IsActive(kind, now.AddSeconds(5)));
            Assert.False(other.IsActive(kind, now));
            Assert.False(effects.TryApply(kind, now.AddSeconds(1), 10));
            Assert.Equal(now.AddSeconds(5), effects.GetExpiresAt(kind));
            effects.Apply(kind, now.AddSeconds(2));
            Assert.False(effects.IsActive(kind, now.AddSeconds(2)));
            Assert.True(effects.TryApply(kind, now.AddSeconds(2), 3));
        }
    }

    private static Player GetOrRegisterPlayer(MatchRuntime runtime, long playerId)
    {
        if (runtime.GetPlayer(playerId) is { } player) return player;
        player = new Player(new PlayerInfo { PlayerId = playerId });
        runtime.RegisterPlayer(player);
        return player;
    }

    private const int SunOrbGroupId = 10700001;
    private const int WindOrbGroupId = 10700002;
    private const int WaveOrbGroupId = 10700003;

    [Fact]
    public void TerminalCleanup_RemovesGameplayStateWithItsMatchEvenIfPostCleanupFails()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            onRedisCleanup: _ => throw new InvalidOperationException());
        var ended = store.GetOrCreate(91001);
        var sibling = store.GetOrCreate(91002);
        GetOrRegisterPlayer(ended, 1).Orbs.IncrementUpgradeCount(SunOrbGroupId);
        using (var scope = MatchRuntimeStore.Enter(ended))
        {
            Assert.Same(ended, scope.Runtime);
            ended.TryMarkEnded();
        }
        Assert.Null(store.GetOrNull(91001));
        Assert.Throws<InvalidOperationException>(() => store.GetOrThrow(91001));
        Assert.False(store.TryEnter(91001, out _));
        Assert.Same(sibling, store.GetOrNull(91002));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void WindOrbAttackState_PreservesAttackAndImmunityTimingBoundaries()
    {
        var player = new Player(new PlayerInfo { PlayerId = 10 });
        DateTime nowUtc = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(player.Orbs.TryBeginOrbAttack(100, nowUtc, 1d));
        Assert.False(player.Orbs.TryBeginOrbAttack(100, nowUtc.AddMilliseconds(999), 1d));
        Assert.True(player.Orbs.TryBeginOrbAttack(100, nowUtc.AddSeconds(1), 1d));


        var victim = new Player(new PlayerInfo { PlayerId = 20 });
        Assert.True(victim.StatusEffects.TryApply(PlayerStatusEffectKind.WindShockImmunity, nowUtc, 0.9d));
        Assert.False(victim.StatusEffects.TryApply(PlayerStatusEffectKind.WindShockImmunity, nowUtc.AddMilliseconds(899), 0.9d));
        Assert.True(victim.StatusEffects.TryApply(PlayerStatusEffectKind.WindShockImmunity, nowUtc.AddMilliseconds(900), 0.9d));

        Assert.False(victim.StatusEffects.IsActive(PlayerStatusEffectKind.Wound, nowUtc));
        victim.StatusEffects.Apply(PlayerStatusEffectKind.Wound, nowUtc.AddSeconds(5));
        Assert.True(victim.StatusEffects.IsActive(PlayerStatusEffectKind.Wound, nowUtc.AddMilliseconds(4999)));
        Assert.False(victim.StatusEffects.IsActive(PlayerStatusEffectKind.Wound, nowUtc.AddSeconds(5)));
        victim.StatusEffects.Apply(PlayerStatusEffectKind.Wound, nowUtc.AddSeconds(7));
        Assert.True(victim.StatusEffects.IsActive(PlayerStatusEffectKind.Wound, nowUtc.AddSeconds(6)));
    }

    [Fact]
    public void Player_CountsOrbUpgradesPerGroup()
    {
        var state = new Player(new PlayerInfo { PlayerId = 10 });
        var bot = new Player(new PlayerInfo { PlayerId = -20 });

        Assert.Equal(0, state.Orbs.GetUpgradeCount(SunOrbGroupId));
        Assert.Equal(1, state.Orbs.IncrementUpgradeCount(SunOrbGroupId));
        Assert.Equal(2, state.Orbs.IncrementUpgradeCount(SunOrbGroupId));
        Assert.Equal(0, state.Orbs.GetUpgradeCount(WindOrbGroupId));
        Assert.Equal(1, bot.Orbs.IncrementUpgradeCount(SunOrbGroupId));
        Assert.Equal(2, state.Orbs.GetUpgradeCount(SunOrbGroupId));
    }

    [Fact]
    public void MatchLocalState_IsIsolatedWhenTwoRuntimesUseTheSameKeys()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        MatchRuntime first = store.GetOrCreate(42001);
        MatchRuntime second = store.GetOrCreate(42002);
        const long playerId = 501;
        const long itemUid = 9001;
        DateTime nowUtc = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);

        GetOrRegisterPlayer(first, playerId).StatusEffects.Apply(PlayerStatusEffectKind.Wound, nowUtc.AddMinutes(1));
        Assert.True(GetOrRegisterPlayer(first, playerId).StatusEffects.IsActive(PlayerStatusEffectKind.Wound, nowUtc));
        Assert.False(GetOrRegisterPlayer(second, playerId).StatusEffects.IsActive(PlayerStatusEffectKind.Wound, nowUtc));

        Assert.True(GetOrRegisterPlayer(first, playerId).Orbs.TryBeginOrbAttack(itemUid, nowUtc, 1d));
        Assert.False(GetOrRegisterPlayer(first, playerId).Orbs.TryBeginOrbAttack(itemUid, nowUtc, 1d));
        Assert.True(GetOrRegisterPlayer(second, playerId).Orbs.TryBeginOrbAttack(itemUid, nowUtc, 1d));
        Assert.True(GetOrRegisterPlayer(first, playerId).StatusEffects.TryApply(PlayerStatusEffectKind.WindShockImmunity, nowUtc, 1d));
        Assert.False(GetOrRegisterPlayer(first, playerId).StatusEffects.TryApply(PlayerStatusEffectKind.WindShockImmunity, nowUtc, 1d));
        Assert.True(GetOrRegisterPlayer(second, playerId).StatusEffects.TryApply(PlayerStatusEffectKind.WindShockImmunity, nowUtc, 1d));

        Assert.Equal(1, GetOrRegisterPlayer(first, playerId).Orbs.IncrementUpgradeCount(SunOrbGroupId));
        Assert.Equal(0, GetOrRegisterPlayer(second, playerId).Orbs.GetUpgradeCount(SunOrbGroupId));
        Assert.Equal(1, GetOrRegisterPlayer(second, playerId).Orbs.IncrementUpgradeCount(SunOrbGroupId));
        Assert.Equal(1, GetOrRegisterPlayer(first, playerId).Orbs.GetUpgradeCount(SunOrbGroupId));

        first.SunCrossfireShapes.Add(CreateCrossfireShape(
            eventId: 11,
            ownerId: playerId,
            anchorCombatTargetId: 7001,
            armedAtUtc: nowUtc.AddSeconds(1)));
        GetOrRegisterPlayer(first, playerId).StatusEffects.SunBurn = new PlayerStatusEffects.SunBurnState(playerId, 101, AreaType.S2Gym1, nowUtc.AddSeconds(3), nowUtc.AddSeconds(1));

        Assert.Single(first.SunCrossfireShapes);
        Assert.Empty(second.SunCrossfireShapes);
        Assert.Null(GetOrRegisterPlayer(second, playerId).StatusEffects.SunBurn);

        second.SunCrossfireShapes.Add(CreateCrossfireShape(
            eventId: 12,
            ownerId: playerId,
            anchorCombatTargetId: 7001,
            armedAtUtc: nowUtc.AddSeconds(1)));
        GetOrRegisterPlayer(second, playerId).StatusEffects.SunBurn = new PlayerStatusEffects.SunBurnState(playerId + 1, 202, AreaType.S2Gym1, nowUtc.AddSeconds(4), nowUtc.AddSeconds(2));

        Assert.Single(first.SunCrossfireShapes);
        Assert.Single(second.SunCrossfireShapes);
        Assert.Equal(playerId, GetOrRegisterPlayer(first, playerId).StatusEffects.SunBurn!.Value.OwnerId);
        Assert.Equal(playerId + 1, GetOrRegisterPlayer(second, playerId).StatusEffects.SunBurn!.Value.OwnerId);
    }

    [Fact]
    public void Remove_DropsWholeAggregateWithoutTouchingSiblingHolderState()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        const long removedMatchingId = 42005;
        const long siblingMatchingId = 42006;
        const long removedPlayerId = 501;
        const long siblingPlayerId = 601;
        DateTime crossfireNowUtc = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        MatchRuntime removed = store.GetOrCreate(removedMatchingId);
        MatchRuntime sibling = store.GetOrCreate(siblingMatchingId);

        GetOrRegisterPlayer(removed, removedPlayerId).Orbs.OrbTrail.Add(new Vector3f(0f, 0f, 0f));
        GetOrRegisterPlayer(removed, removedPlayerId).StatusEffects.Apply(PlayerStatusEffectKind.Wound, DateTime.MaxValue);
        GetOrRegisterPlayer(removed, removedPlayerId).Orbs.IncrementUpgradeCount(WindOrbGroupId);
        removed.SunCrossfireShapes.Add(CreateCrossfireShape(
            eventId: 11,
            ownerId: removedPlayerId,
            anchorCombatTargetId: 7001,
            armedAtUtc: crossfireNowUtc.AddSeconds(1)));
        GetOrRegisterPlayer(removed, removedPlayerId).StatusEffects.SunBurn = new PlayerStatusEffects.SunBurnState(removedPlayerId, 101, AreaType.S2Gym1, crossfireNowUtc.AddSeconds(3d), crossfireNowUtc.AddSeconds(1d));

        GetOrRegisterPlayer(sibling, siblingPlayerId).Orbs.OrbTrail.Add(new Vector3f(0f, 0f, 0f));
        GetOrRegisterPlayer(sibling, siblingPlayerId).StatusEffects.Apply(PlayerStatusEffectKind.Wound, DateTime.MaxValue);
        GetOrRegisterPlayer(sibling, siblingPlayerId).Orbs.IncrementUpgradeCount(WaveOrbGroupId);
        sibling.SunCrossfireShapes.Add(CreateCrossfireShape(
            eventId: 12,
            ownerId: siblingPlayerId,
            anchorCombatTargetId: 8001,
            armedAtUtc: crossfireNowUtc.AddSeconds(1)));
        GetOrRegisterPlayer(sibling, siblingPlayerId).StatusEffects.SunBurn = new PlayerStatusEffects.SunBurnState(siblingPlayerId, 202, AreaType.S2Gym1, crossfireNowUtc.AddSeconds(4d), crossfireNowUtc.AddSeconds(2d));

        Assert.True(store.Remove(removedMatchingId));

        MatchRuntime? missing = store.GetOrNull(removedMatchingId);
        Assert.Null(missing);
        MatchRuntime? preservedSibling = store.GetOrNull(siblingMatchingId);
        Assert.Same(sibling, preservedSibling);
        Assert.Single(GetOrRegisterPlayer(sibling, siblingPlayerId).Orbs.OrbTrail);
        Assert.True(GetOrRegisterPlayer(sibling, siblingPlayerId).StatusEffects.IsActive(PlayerStatusEffectKind.Wound, DateTime.UtcNow));
        Assert.Equal(1, GetOrRegisterPlayer(sibling, siblingPlayerId).Orbs.GetUpgradeCount(WaveOrbGroupId));
        Assert.Single(sibling.SunCrossfireShapes);
        Assert.Equal(siblingPlayerId, GetOrRegisterPlayer(sibling, siblingPlayerId).StatusEffects.SunBurn!.Value.OwnerId);
        Assert.Equal(1, store.Count);

        MatchRuntime replacement = store.GetOrCreate(removedMatchingId);
        Assert.NotSame(removed, replacement);
        Assert.Empty(GetOrRegisterPlayer(replacement, removedPlayerId).Orbs.OrbTrail);
        Assert.False(GetOrRegisterPlayer(replacement, removedPlayerId).StatusEffects.IsActive(PlayerStatusEffectKind.Wound, DateTime.UtcNow));
        Assert.Equal(0, GetOrRegisterPlayer(replacement, removedPlayerId).Orbs.GetUpgradeCount(WindOrbGroupId));
        Assert.NotSame(removed.SunCrossfireShapes, replacement.SunCrossfireShapes);
        Assert.Empty(replacement.SunCrossfireShapes);
    }

    [Fact]
    public void CrossfireEventIds_RemainProcessWideAndThreadSafeAcrossRuntimeRecreation()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        const long firstMatchingId = 42011;
        const long secondMatchingId = 42012;

        MatchRuntime first = store.GetOrCreate(firstMatchingId);
        MatchRuntime second = store.GetOrCreate(secondMatchingId);

        long firstId = MatchOrbAttackService.AllocateEventId();
        long secondId = MatchOrbAttackService.AllocateEventId();
        Assert.True(secondId > firstId);

        Assert.True(store.Remove(firstMatchingId));
        MatchRuntime recreated = store.GetOrCreate(firstMatchingId);
        long recreatedId = MatchOrbAttackService.AllocateEventId();
        Assert.True(recreatedId > secondId);

        var allocated = new ConcurrentBag<long>();
        Parallel.For(
            0,
            1000,
            index => allocated.Add(
                (index & 1) == 0
                    ? MatchOrbAttackService.AllocateEventId()
                    : MatchOrbAttackService.AllocateEventId()));

        Assert.Equal(1000, allocated.Count);
        Assert.Equal(1000, allocated.Distinct().Count());
        Assert.True(allocated.Min() > recreatedId);
    }

    private static SwarmCrossfireShape CreateCrossfireShape(
        long eventId,
        long ownerId,
        long anchorCombatTargetId,
        DateTime armedAtUtc) =>
        new()
        {
            EventId = eventId,
            OwnerId = ownerId,
            WeaponItemId = 101,
            Damage = 10,
            Area = AreaType.S2Gym1,
            Origin = new Vector3f(0f, 0f, 0f),
            End = new Vector3f(6f, 0f, 0f),
            GroundLength = 6f,
            HalfWidth = 0.35f,
            BlastRadius = 0.5f,
            SweepSpeed = 4.5f,
            ArmedAtUtc = armedAtUtc,
            ExpiresAtUtc = armedAtUtc.AddSeconds(2),
            AnchorMonsterId = 301,
            AnchorCombatTargetId = anchorCombatTargetId,
            LastFront = -0.35f
        };
}
