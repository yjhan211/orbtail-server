using game_server.services;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class SwarmMatchRuntimeStoreTests
{
    [Fact]
    public void AttackEventState_PreservesBoundariesAndReversePendingOrder()
    {
        const long matchingId = 41999;
        const long attackerId = 10;
        const long targetId = 20;
        DateTime nowUtc = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);
        SwarmPvpAttackEventState state = new SwarmMatchRuntimeStore()
            .GetOrCreate(matchingId)
            .AttackEvents;
        SwarmPvpAttackEvent attackEvent = CreateAttackEvent(
            matchingId, 1, attackerId, targetId, OrbColor.Red, nowUtc.AddSeconds(1));

        state.RegisterEvent(attackEvent, nowUtc, nowUtc.AddSeconds(2));
        Assert.True(state.IsColorBlocked(attackerId, OrbColor.Red, nowUtc));
        Assert.True(state.IsAttributeBlocked(attackerId, nowUtc.AddMilliseconds(1999)));
        Assert.False(state.IsAttributeBlocked(attackerId, nowUtc.AddSeconds(2)));

        Assert.Empty(state.TakeReadyEvents(nowUtc));
        Assert.Same(attackEvent, Assert.Single(state.TakeReadyEvents(nowUtc.AddSeconds(1))));
        Assert.False(state.IsColorBlocked(attackerId, OrbColor.Red, nowUtc.AddSeconds(1)));
        Assert.True(state.TryGetCurrentTarget(attackerId, out long rememberedTargetId));
        Assert.Equal(targetId, rememberedTargetId);
        state.ClearCurrentTarget(attackerId);
        Assert.False(state.TryGetCurrentTarget(attackerId, out _));

        state.EnqueueVisual(CreatePendingVisual(1, nowUtc));
        state.EnqueueVisual(CreatePendingVisual(2, nowUtc));
        state.EnqueueVisual(CreatePendingVisual(3, nowUtc.AddTicks(1)));
        Assert.Equal([2L, 1L], state.TakeDueVisuals(nowUtc).Select(visual => visual.AttackEventId));
        Assert.Equal(3L, Assert.Single(state.TakeDueVisuals(nowUtc.AddTicks(1))).AttackEventId);

        state.EnqueueHit(CreatePendingHit(4, nowUtc));
        state.EnqueueHit(CreatePendingHit(5, nowUtc));
        state.EnqueueHit(CreatePendingHit(6, nowUtc.AddTicks(1)));
        Assert.Equal([5L, 4L], state.TakeDueHits(nowUtc).Select(hit => hit.AttackEventId));
        Assert.Equal(6L, Assert.Single(state.TakeDueHits(nowUtc.AddTicks(1))).AttackEventId);
    }

    [Fact]
    public void AttackEventState_PartialPendingDrainRemovesOnlyYieldedWork()
    {
        DateTime nowUtc = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);
        SwarmPvpAttackEventState state = new SwarmMatchRuntimeStore()
            .GetOrCreate(41998)
            .AttackEvents;
        state.EnqueueVisual(CreatePendingVisual(1, nowUtc));
        state.EnqueueVisual(CreatePendingVisual(2, nowUtc));

        using (IEnumerator<PendingSwarmAttackVisual> drain = state.TakeDueVisuals(nowUtc).GetEnumerator())
        {
            Assert.True(drain.MoveNext());
            Assert.Equal(2L, drain.Current.AttackEventId);
        }

        Assert.Equal(1L, Assert.Single(state.TakeDueVisuals(nowUtc)).AttackEventId);
    }

    [Fact]
    public void WindBladeState_PreservesTimingBoundariesAndEngagementReset()
    {
        var state = new SwarmWindBladeState();
        DateTime nowUtc = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(state.TryBeginTick(10, 100, nowUtc, 1d));
        Assert.False(state.TryBeginTick(10, 100, nowUtc.AddMilliseconds(999), 1d));
        Assert.True(state.TryBeginTick(10, 100, nowUtc.AddSeconds(1), 1d));

        Assert.False(state.HasCompletedSpinup(10, 100, nowUtc, 0.2f));
        Assert.False(state.HasCompletedSpinup(10, 100, nowUtc.AddMilliseconds(200), 0.2f));
        Assert.True(state.HasCompletedSpinup(10, 100, nowUtc.AddMilliseconds(200).AddTicks(1), 0.2f));
        state.ResetEngagement(10, 100);
        Assert.False(state.HasCompletedSpinup(10, 100, nowUtc.AddSeconds(1), 0.2f));

        Assert.True(state.TryClaimVictimShock(20, nowUtc, 0.9d));
        Assert.False(state.TryClaimVictimShock(20, nowUtc.AddMilliseconds(899), 0.9d));
        Assert.True(state.TryClaimVictimShock(20, nowUtc.AddMilliseconds(900), 0.9d));

        Assert.False(state.IsWounded(20, nowUtc));
        state.ApplyWound(20, nowUtc.AddSeconds(5));
        Assert.True(state.IsWounded(20, nowUtc.AddMilliseconds(4999)));
        Assert.False(state.IsWounded(20, nowUtc.AddSeconds(5)));
        state.ApplyWound(20, nowUtc.AddSeconds(7));
        Assert.True(state.IsWounded(20, nowUtc.AddSeconds(6)));
    }

    [Fact]
    public void OrbBoardState_CountsUpgradesPerPlayerAndColor()
    {
        var state = new SwarmOrbBoardState();

        Assert.Equal(0, state.GetFamilyUpgradeCount(10, OrbColor.Red));
        Assert.Equal(1, state.IncrementFamilyUpgradeCount(10, OrbColor.Red));
        Assert.Equal(2, state.IncrementFamilyUpgradeCount(10, OrbColor.Red));
        Assert.Equal(0, state.GetFamilyUpgradeCount(10, OrbColor.Green));
        Assert.Equal(1, state.IncrementFamilyUpgradeCount(-20, OrbColor.Red));
        Assert.Equal(2, state.GetFamilyUpgradeCount(10, OrbColor.Red));
    }

    [Fact]
    public void MatchLocalState_IsIsolatedWhenTwoRuntimesUseTheSameKeys()
    {
        var store = new SwarmMatchRuntimeStore();
        SwarmMatchRuntime first = store.GetOrCreate(42001);
        SwarmMatchRuntime second = store.GetOrCreate(42002);
        const long playerId = 501;
        const long itemUid = 9001;
        DateTime nowUtc = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);

        first.WindBlade.ApplyWound(playerId, nowUtc.AddMinutes(1));
        Assert.True(first.WindBlade.IsWounded(playerId, nowUtc));
        Assert.False(second.WindBlade.IsWounded(playerId, nowUtc));
        Assert.False(first.WindBlade.HasCompletedSpinup(playerId, itemUid, nowUtc, 1d));
        Assert.False(second.WindBlade.HasCompletedSpinup(playerId, itemUid, nowUtc.AddSeconds(2), 1d));

        Assert.True(first.WindBlade.TryBeginTick(playerId, itemUid, nowUtc, 1d));
        Assert.False(first.WindBlade.TryBeginTick(playerId, itemUid, nowUtc, 1d));
        Assert.True(second.WindBlade.TryBeginTick(playerId, itemUid, nowUtc, 1d));
        Assert.True(first.WindBlade.TryClaimVictimShock(playerId, nowUtc, 1d));
        Assert.False(first.WindBlade.TryClaimVictimShock(playerId, nowUtc, 1d));
        Assert.True(second.WindBlade.TryClaimVictimShock(playerId, nowUtc, 1d));

        Assert.Equal(1, first.OrbBoard.IncrementFamilyUpgradeCount(playerId, OrbColor.Red));
        Assert.Equal(0, second.OrbBoard.GetFamilyUpgradeCount(playerId, OrbColor.Red));
        Assert.Equal(1, second.OrbBoard.IncrementFamilyUpgradeCount(playerId, OrbColor.Red));
        Assert.Equal(1, first.OrbBoard.GetFamilyUpgradeCount(playerId, OrbColor.Red));

        Assert.NotSame(first.AttackEvents, second.AttackEvents);
        first.AttackEvents.RegisterEvent(
            CreateAttackEvent(first.MatchingId, 1, playerId, 7001, OrbColor.Green, nowUtc.AddSeconds(1)),
            nowUtc.AddSeconds(3),
            nowUtc.AddSeconds(4));
        Assert.True(first.AttackEvents.IsColorBlocked(playerId, OrbColor.Green, nowUtc));
        Assert.False(second.AttackEvents.IsColorBlocked(playerId, OrbColor.Green, nowUtc));
        Assert.True(first.AttackEvents.TryGetCurrentTarget(playerId, out long firstTargetId));
        Assert.Equal(7001, firstTargetId);
        Assert.False(second.AttackEvents.TryGetCurrentTarget(playerId, out _));
        second.AttackEvents.RegisterEvent(
            CreateAttackEvent(second.MatchingId, 2, playerId, 8001, OrbColor.Green, nowUtc.AddSeconds(1)),
            nowUtc.AddSeconds(3),
            nowUtc.AddSeconds(4));
        Assert.True(second.AttackEvents.IsColorBlocked(playerId, OrbColor.Green, nowUtc));
        Assert.True(first.AttackEvents.TryGetCurrentTarget(playerId, out firstTargetId));
        Assert.Equal(7001, firstTargetId);
    }

    [Fact]
    public void GetOrCreate_ReturnsSameRuntimeForSameIdAndIsolatesDifferentIds()
    {
        var store = new SwarmMatchRuntimeStore();
        const long firstMatchingId = 42001;
        const long secondMatchingId = 42002;

        SwarmMatchRuntime first = store.GetOrCreate(firstMatchingId);
        SwarmMatchRuntime firstAgain = store.GetOrCreate(firstMatchingId);
        SwarmMatchRuntime second = store.GetOrCreate(secondMatchingId);

        Assert.Same(first, firstAgain);
        Assert.NotSame(first, second);
        Assert.Equal(firstMatchingId, first.MatchingId);
        Assert.Equal(secondMatchingId, second.MatchingId);
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void Remove_PreservesSiblingAndNextGetOrCreateBuildsNewAggregate()
    {
        var store = new SwarmMatchRuntimeStore();
        const long removedMatchingId = 42003;
        const long siblingMatchingId = 42004;

        SwarmMatchRuntime removed = store.GetOrCreate(removedMatchingId);
        SwarmMatchRuntime sibling = store.GetOrCreate(siblingMatchingId);

        Assert.True(store.Remove(removedMatchingId));
        Assert.False(store.TryGet(removedMatchingId, out SwarmMatchRuntime? missing));
        Assert.Null(missing);
        Assert.True(store.TryGet(siblingMatchingId, out SwarmMatchRuntime? preservedSibling));
        Assert.Same(sibling, preservedSibling);
        Assert.Equal(1, store.Count);

        SwarmMatchRuntime recreated = store.GetOrCreate(removedMatchingId);

        Assert.NotSame(removed, recreated);
        Assert.Equal(removedMatchingId, recreated.MatchingId);
        Assert.Same(sibling, store.GetOrCreate(siblingMatchingId));
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void Remove_DropsWholeAggregateWithoutTouchingSiblingHolderState()
    {
        var store = new SwarmMatchRuntimeStore();
        const long removedMatchingId = 42005;
        const long siblingMatchingId = 42006;
        const long removedPlayerId = 501;
        const long siblingPlayerId = 601;

        SwarmMatchRuntime removed = store.GetOrCreate(removedMatchingId);
        SwarmMatchRuntime sibling = store.GetOrCreate(siblingMatchingId);

        removed.TrailCombat.OrbTrails[(removedMatchingId, removedPlayerId)] = [];
        removed.GrowthOffers.PreviewCost[(removedMatchingId, removedPlayerId)] = 3;
        removed.BotTactics.FleeDirective.Add((removedMatchingId, removedPlayerId));
        removed.Pacing.StartingOrbGrantedPlayers.Add((removedMatchingId, removedPlayerId));
        removed.WindBlade.ApplyWound(removedPlayerId, DateTime.MaxValue);
        removed.OrbBoard.IncrementFamilyUpgradeCount(removedPlayerId, OrbColor.Green);
        removed.AttackEvents.RegisterEvent(
            CreateAttackEvent(
                removedMatchingId, 1, removedPlayerId, 7001, OrbColor.Green, DateTime.MaxValue),
            DateTime.MaxValue,
            DateTime.MaxValue);

        sibling.TrailCombat.OrbTrails[(siblingMatchingId, siblingPlayerId)] = [];
        sibling.GrowthOffers.PreviewCost[(siblingMatchingId, siblingPlayerId)] = 7;
        sibling.BotTactics.FleeDirective.Add((siblingMatchingId, siblingPlayerId));
        sibling.Pacing.StartingOrbGrantedPlayers.Add((siblingMatchingId, siblingPlayerId));
        sibling.WindBlade.ApplyWound(siblingPlayerId, DateTime.MaxValue);
        sibling.OrbBoard.IncrementFamilyUpgradeCount(siblingPlayerId, OrbColor.Blue);
        sibling.AttackEvents.RegisterEvent(
            CreateAttackEvent(
                siblingMatchingId, 2, siblingPlayerId, 8001, OrbColor.Blue, DateTime.MaxValue),
            DateTime.MaxValue,
            DateTime.MaxValue);

        Assert.True(store.Remove(removedMatchingId));

        Assert.False(store.TryGet(removedMatchingId, out SwarmMatchRuntime? missing));
        Assert.Null(missing);
        Assert.True(store.TryGet(siblingMatchingId, out SwarmMatchRuntime? preservedSibling));
        Assert.Same(sibling, preservedSibling);
        Assert.Contains((siblingMatchingId, siblingPlayerId), sibling.TrailCombat.OrbTrails.Keys);
        Assert.Equal(7, sibling.GrowthOffers.PreviewCost[(siblingMatchingId, siblingPlayerId)]);
        Assert.Contains((siblingMatchingId, siblingPlayerId), sibling.BotTactics.FleeDirective);
        Assert.Contains((siblingMatchingId, siblingPlayerId), sibling.Pacing.StartingOrbGrantedPlayers);
        Assert.True(sibling.WindBlade.IsWounded(siblingPlayerId, DateTime.UtcNow));
        Assert.Equal(1, sibling.OrbBoard.GetFamilyUpgradeCount(siblingPlayerId, OrbColor.Blue));
        Assert.True(sibling.AttackEvents.TryGetCurrentTarget(siblingPlayerId, out long siblingTargetId));
        Assert.Equal(8001, siblingTargetId);
        Assert.Equal(1, store.Count);

        SwarmMatchRuntime replacement = store.GetOrCreate(removedMatchingId);
        Assert.NotSame(removed, replacement);
        Assert.Empty(replacement.TrailCombat.OrbTrails);
        Assert.Empty(replacement.GrowthOffers.PreviewCost);
        Assert.Empty(replacement.BotTactics.FleeDirective);
        Assert.Empty(replacement.Pacing.StartingOrbGrantedPlayers);
        Assert.False(replacement.WindBlade.IsWounded(removedPlayerId, DateTime.UtcNow));
        Assert.Equal(0, replacement.OrbBoard.GetFamilyUpgradeCount(removedPlayerId, OrbColor.Green));
        Assert.NotSame(removed.AttackEvents, replacement.AttackEvents);
        Assert.True(replacement.AttackEvents.IsEmpty);
    }

    [Fact]
    public void OfferIds_RemainProcessWideAcrossMatchesAndRuntimeRecreation()
    {
        var store = new SwarmMatchRuntimeStore();
        const long firstMatchingId = 42007;
        const long secondMatchingId = 42008;

        SwarmMatchRuntime first = store.GetOrCreate(firstMatchingId);
        SwarmMatchRuntime second = store.GetOrCreate(secondMatchingId);

        Assert.Equal(1, first.GrowthOfferCoordinator.AllocateOfferId());
        Assert.Equal(2, second.GrowthOfferCoordinator.AllocateOfferId());

        Assert.True(store.Remove(firstMatchingId));
        SwarmMatchRuntime recreated = store.GetOrCreate(firstMatchingId);

        Assert.Equal(3, recreated.GrowthOfferCoordinator.AllocateOfferId());
    }

    [Fact]
    public void AttackEventIds_RemainProcessWideAcrossMatchesAndRuntimeRecreation()
    {
        var store = new SwarmMatchRuntimeStore();
        const long firstMatchingId = 42009;
        const long secondMatchingId = 42010;

        SwarmMatchRuntime first = store.GetOrCreate(firstMatchingId);
        SwarmMatchRuntime second = store.GetOrCreate(secondMatchingId);

        Assert.Equal(1L, first.AttackEvents.AllocateAttackEventId());
        Assert.Equal(2L, second.AttackEvents.AllocateAttackEventId());

        Assert.True(store.Remove(firstMatchingId));
        SwarmMatchRuntime recreated = store.GetOrCreate(firstMatchingId);

        Assert.Equal(3L, recreated.AttackEvents.AllocateAttackEventId());
    }

    private static PendingSwarmAttackVisual CreatePendingVisual(long attackEventId, DateTime dueAtUtc) =>
        new(
            attackEventId,
            AttackerId: 10,
            TargetId: 20,
            AreaType.None,
            OrbColor.Red,
            Kind: 1,
            Ordinal: 0,
            Tier: 1,
            new Vector3f(),
            Radius: 0f,
            dueAtUtc);

    private static SwarmPvpAttackEvent CreateAttackEvent(
        long matchingId,
        long attackEventId,
        long attackerId,
        long targetId,
        OrbColor color,
        DateTime launchAtUtc) =>
        new()
        {
            AttackEventId = attackEventId,
            MatchingId = matchingId,
            AttackerId = attackerId,
            TargetId = targetId,
            Area = AreaType.None,
            Color = color,
            StartedAtUtc = launchAtUtc,
            LaunchAtUtc = launchAtUtc
        };

    private static PendingSwarmAttackHit CreatePendingHit(long attackEventId, DateTime dueAtUtc) =>
        new(
            attackEventId,
            FeedbackTargetId: 20,
            Array.Empty<ProximityCombatAttack>(),
            dueAtUtc);
}
