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
            // 수면과 화상은 끝나는 규칙이 달라 전용 메서드로만 건다.
            if (kind is PlayerStatusEffectKind.Sleep or PlayerStatusEffectKind.SunBurn)
            {
                Assert.Throws<ArgumentException>(() => effects.Apply(kind, now.AddSeconds(5)));
                continue;
            }

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

    [Fact]
    public void SleepAndSunBurnShareTheEffectStoreButKeepTheirOwnEndRules()
    {
        var effects = new PlayerStatusEffects();
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // 수면은 시간이 아무리 지나도 깨울 때까지 걸려 있다.
        Assert.False(effects.IsActive(PlayerStatusEffectKind.Sleep, now));
        effects.StartSleep();
        Assert.True(effects.HasSleep());
        Assert.True(effects.IsActive(PlayerStatusEffectKind.Sleep, now.AddHours(1)));
        Assert.Equal(default, effects.GetExpiresAt(PlayerStatusEffectKind.Sleep));
        effects.StopSleep();
        Assert.False(effects.IsActive(PlayerStatusEffectKind.Sleep, now));

        // 화상은 현재 시각이 아니라 남은 도트로 끝난다.
        effects.ApplySunBurn(new PlayerStatusEffects.SunBurnState(1, 107000010, AreaType.S2Gym1, now.AddSeconds(3), now.AddSeconds(3)));
        Assert.True(effects.IsActive(PlayerStatusEffectKind.SunBurn, now.AddSeconds(10)));
        Assert.Equal(now.AddSeconds(3), effects.GetExpiresAt(PlayerStatusEffectKind.SunBurn));
        effects.ApplySunBurn(effects.SunBurn! with { NextTickAtUtc = now.AddSeconds(4) });
        Assert.False(effects.IsActive(PlayerStatusEffectKind.SunBurn, now));
        Assert.Null(effects.SunBurn);

        // 서로의 상태를 건드리지 않는다.
        effects.Apply(PlayerStatusEffectKind.Wound, now.AddSeconds(5));
        effects.StartSleep();
        Assert.True(effects.IsActive(PlayerStatusEffectKind.Wound, now));
        Assert.True(effects.HasSleep());
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
        TestGameData.EnsureBattleItemCombatLoaded();
        var player = new Player(new PlayerInfo { PlayerId = 10 });
        DateTime nowUtc = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);
        long windUid = player.Orbs.AddOrb(107000020).ItemUid;
        double tick = Config.SWARM_WIND_BLADE_TICK_SECONDS;

        // 주기는 오브가 스스로 안다. 처음 본 오브는 첫 위상만 심고 발동하지 않는다.
        Assert.Equal(tick, player.Orbs.GetAttackIntervalSeconds(windUid));
        Assert.False(player.Orbs.IsOrbAttackReady(windUid, nowUtc));
        DateTime firstAt = player.Orbs.GetNextOrbAttackAtUtc(windUid)!.Value;
        Assert.True(firstAt > nowUtc);
        Assert.True(player.Orbs.IsOrbAttackReady(windUid, firstAt));
        player.Orbs.ScheduleNextOrbAttack(windUid, firstAt);
        Assert.False(player.Orbs.IsOrbAttackReady(windUid, firstAt.AddSeconds(tick).AddMilliseconds(-1)));
        Assert.True(player.Orbs.IsOrbAttackReady(windUid, firstAt.AddSeconds(tick)));
        // 보유하지 않은 uid는 주기가 없어 발동하지 않고 타이머도 심지 않는다.
        Assert.False(player.Orbs.IsOrbAttackReady(100, nowUtc));
        Assert.Null(player.Orbs.GetNextOrbAttackAtUtc(100));


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
        long itemUid = GetOrRegisterPlayer(first, playerId).Orbs.AddOrb(107000020).ItemUid;
        DateTime nowUtc = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);

        GetOrRegisterPlayer(first, playerId).StatusEffects.Apply(PlayerStatusEffectKind.Wound, nowUtc.AddMinutes(1));
        Assert.True(GetOrRegisterPlayer(first, playerId).StatusEffects.IsActive(PlayerStatusEffectKind.Wound, nowUtc));
        Assert.False(GetOrRegisterPlayer(second, playerId).StatusEffects.IsActive(PlayerStatusEffectKind.Wound, nowUtc));

        Assert.False(GetOrRegisterPlayer(first, playerId).Orbs.IsOrbAttackReady(itemUid, nowUtc));
        Assert.NotNull(GetOrRegisterPlayer(first, playerId).Orbs.GetNextOrbAttackAtUtc(itemUid));
        Assert.Null(GetOrRegisterPlayer(second, playerId).Orbs.GetNextOrbAttackAtUtc(itemUid));
        Assert.True(GetOrRegisterPlayer(first, playerId).StatusEffects.TryApply(PlayerStatusEffectKind.WindShockImmunity, nowUtc, 1d));
        Assert.False(GetOrRegisterPlayer(first, playerId).StatusEffects.TryApply(PlayerStatusEffectKind.WindShockImmunity, nowUtc, 1d));
        Assert.True(GetOrRegisterPlayer(second, playerId).StatusEffects.TryApply(PlayerStatusEffectKind.WindShockImmunity, nowUtc, 1d));

        Assert.Equal(1, GetOrRegisterPlayer(first, playerId).Orbs.IncrementUpgradeCount(SunOrbGroupId));
        Assert.Equal(0, GetOrRegisterPlayer(second, playerId).Orbs.GetUpgradeCount(SunOrbGroupId));
        Assert.Equal(1, GetOrRegisterPlayer(second, playerId).Orbs.IncrementUpgradeCount(SunOrbGroupId));
        Assert.Equal(1, GetOrRegisterPlayer(first, playerId).Orbs.GetUpgradeCount(SunOrbGroupId));

        first.PendingSunAttacks.Add(CreatePendingSunAttack(
            eventId: 11,
            ownerId: playerId,
            anchorPlayerId: 7001,
            armedAtUtc: nowUtc.AddSeconds(1)));
        GetOrRegisterPlayer(first, playerId).StatusEffects.ApplySunBurn(new PlayerStatusEffects.SunBurnState(playerId, 101, AreaType.S2Gym1, nowUtc.AddSeconds(3), nowUtc.AddSeconds(1)));

        Assert.Single(first.PendingSunAttacks);
        Assert.Empty(second.PendingSunAttacks);
        Assert.Null(GetOrRegisterPlayer(second, playerId).StatusEffects.SunBurn);

        second.PendingSunAttacks.Add(CreatePendingSunAttack(
            eventId: 12,
            ownerId: playerId,
            anchorPlayerId: 7001,
            armedAtUtc: nowUtc.AddSeconds(1)));
        GetOrRegisterPlayer(second, playerId).StatusEffects.ApplySunBurn(new PlayerStatusEffects.SunBurnState(playerId + 1, 202, AreaType.S2Gym1, nowUtc.AddSeconds(4), nowUtc.AddSeconds(2)));

        Assert.Single(first.PendingSunAttacks);
        Assert.Single(second.PendingSunAttacks);
        Assert.Equal(playerId, GetOrRegisterPlayer(first, playerId).StatusEffects.SunBurn!.OwnerId);
        Assert.Equal(playerId + 1, GetOrRegisterPlayer(second, playerId).StatusEffects.SunBurn!.OwnerId);
    }

    [Fact]
    public void Remove_DropsWholeAggregateWithoutTouchingSiblingHolderState()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        const long removedMatchingId = 42005;
        const long siblingMatchingId = 42006;
        const long removedPlayerId = 501;
        const long siblingPlayerId = 601;
        DateTime sunAttackNowUtc = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        MatchRuntime removed = store.GetOrCreate(removedMatchingId);
        MatchRuntime sibling = store.GetOrCreate(siblingMatchingId);

        GetOrRegisterPlayer(removed, removedPlayerId).Orbs.OrbTrail.Add(new Vector3f(0f, 0f, 0f));
        GetOrRegisterPlayer(removed, removedPlayerId).StatusEffects.Apply(PlayerStatusEffectKind.Wound, DateTime.MaxValue);
        GetOrRegisterPlayer(removed, removedPlayerId).Orbs.IncrementUpgradeCount(WindOrbGroupId);
        removed.PendingSunAttacks.Add(CreatePendingSunAttack(
            eventId: 11,
            ownerId: removedPlayerId,
            anchorPlayerId: 7001,
            armedAtUtc: sunAttackNowUtc.AddSeconds(1)));
        GetOrRegisterPlayer(removed, removedPlayerId).StatusEffects.ApplySunBurn(new PlayerStatusEffects.SunBurnState(removedPlayerId, 101, AreaType.S2Gym1, sunAttackNowUtc.AddSeconds(3d), sunAttackNowUtc.AddSeconds(1d)));

        GetOrRegisterPlayer(sibling, siblingPlayerId).Orbs.OrbTrail.Add(new Vector3f(0f, 0f, 0f));
        GetOrRegisterPlayer(sibling, siblingPlayerId).StatusEffects.Apply(PlayerStatusEffectKind.Wound, DateTime.MaxValue);
        GetOrRegisterPlayer(sibling, siblingPlayerId).Orbs.IncrementUpgradeCount(WaveOrbGroupId);
        sibling.PendingSunAttacks.Add(CreatePendingSunAttack(
            eventId: 12,
            ownerId: siblingPlayerId,
            anchorPlayerId: 8001,
            armedAtUtc: sunAttackNowUtc.AddSeconds(1)));
        GetOrRegisterPlayer(sibling, siblingPlayerId).StatusEffects.ApplySunBurn(new PlayerStatusEffects.SunBurnState(siblingPlayerId, 202, AreaType.S2Gym1, sunAttackNowUtc.AddSeconds(4d), sunAttackNowUtc.AddSeconds(2d)));

        Assert.True(store.Remove(removedMatchingId));

        MatchRuntime? missing = store.GetOrNull(removedMatchingId);
        Assert.Null(missing);
        MatchRuntime? preservedSibling = store.GetOrNull(siblingMatchingId);
        Assert.Same(sibling, preservedSibling);
        Assert.Single(GetOrRegisterPlayer(sibling, siblingPlayerId).Orbs.OrbTrail);
        Assert.True(GetOrRegisterPlayer(sibling, siblingPlayerId).StatusEffects.IsActive(PlayerStatusEffectKind.Wound, DateTime.UtcNow));
        Assert.Equal(1, GetOrRegisterPlayer(sibling, siblingPlayerId).Orbs.GetUpgradeCount(WaveOrbGroupId));
        Assert.Single(sibling.PendingSunAttacks);
        Assert.Equal(siblingPlayerId, GetOrRegisterPlayer(sibling, siblingPlayerId).StatusEffects.SunBurn!.OwnerId);
        Assert.Equal(1, store.Count);

        MatchRuntime replacement = store.GetOrCreate(removedMatchingId);
        Assert.NotSame(removed, replacement);
        Assert.Empty(GetOrRegisterPlayer(replacement, removedPlayerId).Orbs.OrbTrail);
        Assert.False(GetOrRegisterPlayer(replacement, removedPlayerId).StatusEffects.IsActive(PlayerStatusEffectKind.Wound, DateTime.UtcNow));
        Assert.Equal(0, GetOrRegisterPlayer(replacement, removedPlayerId).Orbs.GetUpgradeCount(WindOrbGroupId));
        Assert.NotSame(removed.PendingSunAttacks, replacement.PendingSunAttacks);
        Assert.Empty(replacement.PendingSunAttacks);
    }

    [Fact]
    public void CrossfireEventIds_RemainProcessWideAndThreadSafeAcrossRuntimeRecreation()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        const long firstMatchingId = 42011;
        const long secondMatchingId = 42012;

        MatchRuntime first = store.GetOrCreate(firstMatchingId);
        MatchRuntime second = store.GetOrCreate(secondMatchingId);

        long firstId = PendingSunAttack.AllocateEventId();
        long secondId = PendingSunAttack.AllocateEventId();
        Assert.True(secondId > firstId);

        Assert.True(store.Remove(firstMatchingId));
        MatchRuntime recreated = store.GetOrCreate(firstMatchingId);
        long recreatedId = PendingSunAttack.AllocateEventId();
        Assert.True(recreatedId > secondId);

        var allocated = new ConcurrentBag<long>();
        Parallel.For(
            0,
            1000,
            index => allocated.Add(
                (index & 1) == 0
                    ? PendingSunAttack.AllocateEventId()
                    : PendingSunAttack.AllocateEventId()));

        Assert.Equal(1000, allocated.Count);
        Assert.Equal(1000, allocated.Distinct().Count());
        Assert.True(allocated.Min() > recreatedId);
    }

    private static PendingSunAttack CreatePendingSunAttack(
        long eventId,
        long ownerId,
        long anchorPlayerId,
        DateTime armedAtUtc) =>
        new()
        {
            EventId = eventId,
            OwnerId = ownerId,
            WeaponItemId = 101,
            Damage = 10,
            Area = AreaType.S2Gym1,
            OriginCell = new Cell(0, 0),
            EndCell = new Cell(8, 0),
            ArmedAtUtc = armedAtUtc,
            ExpiresAtUtc = armedAtUtc.AddSeconds(2),
            AnchorTarget = (ObjectType.PLAYER, anchorPlayerId)
        };
}
