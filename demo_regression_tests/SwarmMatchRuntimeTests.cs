using game_server.matches;
using System.Collections.Concurrent;
using game_server.services;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class SwarmMatchRuntimeTests
{
    [Fact]
    public void TerminalCleanup_RemovesSwarmWithItsMatchEvenIfPostCleanupFails()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            onRedisCleanup: _ => throw new InvalidOperationException());
        var ended = store.GetOrCreate(91001);
        var sibling = store.GetOrCreate(91002);
        ended.Swarm.OrbBoard.IncrementFamilyUpgradeCount(1, OrbColor.Red);
        using (var scope = MatchRuntimeStore.Enter(ended))
        {
            Assert.Same(ended.Swarm, scope.Runtime.Swarm);
            ended.TryMarkTerminal();
        }
        Assert.Null(store.GetOrNull(91001));
        Assert.Throws<InvalidOperationException>(() => store.GetOrThrow(91001));
        Assert.False(store.TryEnter(91001, out _));
        Assert.Same(sibling, store.GetOrNull(91002));
        Assert.Equal(1, store.Count);
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
        var store = TestGameSessionServices.CreateMatchRuntimeStore(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        SwarmMatchRuntime first = store.GetOrCreate(42001).Swarm;
        SwarmMatchRuntime second = store.GetOrCreate(42002).Swarm;
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

        first.Crossfire.AddShape(CreateCrossfireShape(
            eventId: 11,
            ownerId: playerId,
            anchorCombatTargetId: 7001,
            armedAtUtc: nowUtc.AddSeconds(1)));
        first.Crossfire.SetSunBurn(
            playerId,
            ownerId: playerId,
            weaponItemId: 101,
            AreaType.S2Ground,
            nowUtc,
            durationSeconds: 3d,
            tickIntervalSeconds: 1d);
        SwarmCrossfireConvergenceObservation firstConvergence =
            first.Crossfire.TrackConvergence(7001, nowUtc);

        Assert.Equal(1, firstConvergence.HitCount);
        Assert.True(second.Crossfire.IsEmpty);
        Assert.Equal(first.MatchingId, Assert.Single(first.Crossfire.DodgeSnapshot).MatchingId);

        second.Crossfire.AddShape(CreateCrossfireShape(
            eventId: 12,
            ownerId: playerId,
            anchorCombatTargetId: 7001,
            armedAtUtc: nowUtc.AddSeconds(1)));
        second.Crossfire.SetSunBurn(
            playerId,
            ownerId: playerId + 1,
            weaponItemId: 202,
            AreaType.S2Gym1,
            nowUtc,
            durationSeconds: 4d,
            tickIntervalSeconds: 2d);
        SwarmCrossfireConvergenceObservation secondConvergence =
            second.Crossfire.TrackConvergence(7001, nowUtc);

        Assert.Equal(1, secondConvergence.HitCount);
        Assert.Equal(first.MatchingId, Assert.Single(first.Crossfire.DodgeSnapshot).MatchingId);
        Assert.Equal(second.MatchingId, Assert.Single(second.Crossfire.DodgeSnapshot).MatchingId);
        Assert.True(first.Crossfire.TryGetSunBurn(playerId, out SwarmSunBurnState? firstBurn));
        Assert.True(second.Crossfire.TryGetSunBurn(playerId, out SwarmSunBurnState? secondBurn));
        Assert.Equal(playerId, firstBurn!.OwnerId);
        Assert.Equal(playerId + 1, secondBurn!.OwnerId);
        Assert.Equal(1, first.Crossfire.ConvergenceWindowCount);
        Assert.Equal(1, second.Crossfire.ConvergenceWindowCount);
    }

    [Fact]
    public void GetOrCreate_ReturnsSameRuntimeForSameIdAndIsolatesDifferentIds()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        const long firstMatchingId = 42001;
        const long secondMatchingId = 42002;

        SwarmMatchRuntime first = store.GetOrCreate(firstMatchingId).Swarm;
        SwarmMatchRuntime firstAgain = store.GetOrCreate(firstMatchingId).Swarm;
        SwarmMatchRuntime second = store.GetOrCreate(secondMatchingId).Swarm;

        Assert.Same(first, firstAgain);
        Assert.NotSame(first, second);
        Assert.Equal(firstMatchingId, first.MatchingId);
        Assert.Equal(secondMatchingId, second.MatchingId);
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void Remove_PreservesSiblingAndNextGetOrCreateBuildsNewAggregate()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        const long removedMatchingId = 42003;
        const long siblingMatchingId = 42004;

        SwarmMatchRuntime removed = store.GetOrCreate(removedMatchingId).Swarm;
        SwarmMatchRuntime sibling = store.GetOrCreate(siblingMatchingId).Swarm;

        Assert.True(store.Remove(removedMatchingId));
        SwarmMatchRuntime? missing = store.GetOrNull(removedMatchingId)?.Swarm;
        Assert.Null(missing);
        SwarmMatchRuntime? preservedSibling = store.GetOrNull(siblingMatchingId)?.Swarm;
        Assert.Same(sibling, preservedSibling);
        Assert.Equal(1, store.Count);

        SwarmMatchRuntime recreated = store.GetOrCreate(removedMatchingId).Swarm;

        Assert.NotSame(removed, recreated);
        Assert.Equal(removedMatchingId, recreated.MatchingId);
        Assert.Same(sibling, store.GetOrCreate(siblingMatchingId).Swarm);
        Assert.Equal(2, store.Count);
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

        SwarmMatchRuntime removed = store.GetOrCreate(removedMatchingId).Swarm;
        SwarmMatchRuntime sibling = store.GetOrCreate(siblingMatchingId).Swarm;

        removed.TrailCombat.OrbTrails[(removedMatchingId, removedPlayerId)] = [];
        removed.GrowthOffers.PreviewCost[(removedMatchingId, removedPlayerId)] = 3;
        removed.BotTactics.FleeDirective.Add((removedMatchingId, removedPlayerId));
        removed.Pacing.StartingOrbGrantedPlayers.Add((removedMatchingId, removedPlayerId));
        removed.WindBlade.ApplyWound(removedPlayerId, DateTime.MaxValue);
        removed.OrbBoard.IncrementFamilyUpgradeCount(removedPlayerId, OrbColor.Green);
        removed.Crossfire.AddShape(CreateCrossfireShape(
            eventId: 11,
            ownerId: removedPlayerId,
            anchorCombatTargetId: 7001,
            armedAtUtc: crossfireNowUtc.AddSeconds(1)));
        removed.Crossfire.SetSunBurn(
            removedPlayerId,
            ownerId: removedPlayerId,
            weaponItemId: 101,
            AreaType.S2Ground,
            crossfireNowUtc,
            durationSeconds: 3d,
            tickIntervalSeconds: 1d);
        removed.Crossfire.TrackConvergence(7001, crossfireNowUtc);

        sibling.TrailCombat.OrbTrails[(siblingMatchingId, siblingPlayerId)] = [];
        sibling.GrowthOffers.PreviewCost[(siblingMatchingId, siblingPlayerId)] = 7;
        sibling.BotTactics.FleeDirective.Add((siblingMatchingId, siblingPlayerId));
        sibling.Pacing.StartingOrbGrantedPlayers.Add((siblingMatchingId, siblingPlayerId));
        sibling.WindBlade.ApplyWound(siblingPlayerId, DateTime.MaxValue);
        sibling.OrbBoard.IncrementFamilyUpgradeCount(siblingPlayerId, OrbColor.Blue);
        sibling.Crossfire.AddShape(CreateCrossfireShape(
            eventId: 12,
            ownerId: siblingPlayerId,
            anchorCombatTargetId: 8001,
            armedAtUtc: crossfireNowUtc.AddSeconds(1)));
        sibling.Crossfire.SetSunBurn(
            siblingPlayerId,
            ownerId: siblingPlayerId,
            weaponItemId: 202,
            AreaType.S2Gym1,
            crossfireNowUtc,
            durationSeconds: 4d,
            tickIntervalSeconds: 2d);
        sibling.Crossfire.TrackConvergence(8001, crossfireNowUtc);

        Assert.True(store.Remove(removedMatchingId));

        SwarmMatchRuntime? missing = store.GetOrNull(removedMatchingId)?.Swarm;
        Assert.Null(missing);
        SwarmMatchRuntime? preservedSibling = store.GetOrNull(siblingMatchingId)?.Swarm;
        Assert.Same(sibling, preservedSibling);
        Assert.Contains((siblingMatchingId, siblingPlayerId), sibling.TrailCombat.OrbTrails.Keys);
        Assert.Equal(7, sibling.GrowthOffers.PreviewCost[(siblingMatchingId, siblingPlayerId)]);
        Assert.Contains((siblingMatchingId, siblingPlayerId), sibling.BotTactics.FleeDirective);
        Assert.Contains((siblingMatchingId, siblingPlayerId), sibling.Pacing.StartingOrbGrantedPlayers);
        Assert.True(sibling.WindBlade.IsWounded(siblingPlayerId, DateTime.UtcNow));
        Assert.Equal(1, sibling.OrbBoard.GetFamilyUpgradeCount(siblingPlayerId, OrbColor.Blue));
        Assert.Equal(1, sibling.Crossfire.ShapeCount);
        Assert.Equal(1, sibling.Crossfire.SunBurnCount);
        Assert.Equal(1, sibling.Crossfire.ConvergenceWindowCount);
        Assert.Equal(siblingMatchingId, Assert.Single(sibling.Crossfire.DodgeSnapshot).MatchingId);
        Assert.True(sibling.Crossfire.TryGetSunBurn(
            siblingPlayerId,
            out SwarmSunBurnState? siblingBurn));
        Assert.Equal(siblingPlayerId, siblingBurn!.OwnerId);
        Assert.Equal(1, store.Count);

        SwarmMatchRuntime replacement = store.GetOrCreate(removedMatchingId).Swarm;
        Assert.NotSame(removed, replacement);
        Assert.Empty(replacement.TrailCombat.OrbTrails);
        Assert.Empty(replacement.GrowthOffers.PreviewCost);
        Assert.Empty(replacement.BotTactics.FleeDirective);
        Assert.Empty(replacement.Pacing.StartingOrbGrantedPlayers);
        Assert.False(replacement.WindBlade.IsWounded(removedPlayerId, DateTime.UtcNow));
        Assert.Equal(0, replacement.OrbBoard.GetFamilyUpgradeCount(removedPlayerId, OrbColor.Green));
        Assert.NotSame(removed.Crossfire, replacement.Crossfire);
        Assert.True(replacement.Crossfire.IsEmpty);
        Assert.Empty(replacement.Crossfire.DodgeSnapshot);
    }

    [Fact]
    public void OfferIds_RemainProcessWideAcrossMatchesAndRuntimeRecreation()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        const long firstMatchingId = 42007;
        const long secondMatchingId = 42008;

        SwarmMatchRuntime first = store.GetOrCreate(firstMatchingId).Swarm;
        SwarmMatchRuntime second = store.GetOrCreate(secondMatchingId).Swarm;

        Assert.Equal(1, first.GrowthOfferCoordinator.AllocateOfferId());
        Assert.Equal(2, second.GrowthOfferCoordinator.AllocateOfferId());

        Assert.True(store.Remove(firstMatchingId));
        SwarmMatchRuntime recreated = store.GetOrCreate(firstMatchingId).Swarm;

        Assert.Equal(3, recreated.GrowthOfferCoordinator.AllocateOfferId());
    }

    [Fact]
    public void CrossfireEventIds_RemainProcessWideAndThreadSafeAcrossRuntimeRecreation()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        const long firstMatchingId = 42011;
        const long secondMatchingId = 42012;

        SwarmMatchRuntime first = store.GetOrCreate(firstMatchingId).Swarm;
        SwarmMatchRuntime second = store.GetOrCreate(secondMatchingId).Swarm;

        Assert.Equal(1L, first.Crossfire.AllocateEventId());
        Assert.Equal(2L, second.Crossfire.AllocateEventId());

        Assert.True(store.Remove(firstMatchingId));
        SwarmMatchRuntime recreated = store.GetOrCreate(firstMatchingId).Swarm;
        Assert.Equal(3L, recreated.Crossfire.AllocateEventId());

        var allocated = new ConcurrentBag<long>();
        Parallel.For(
            0,
            1000,
            index => allocated.Add(
                (index & 1) == 0
                    ? recreated.Crossfire.AllocateEventId()
                    : second.Crossfire.AllocateEventId()));

        Assert.Equal(1000, allocated.Count);
        Assert.Equal(1000, allocated.Distinct().Count());
        Assert.Equal(4L, allocated.Min());
        Assert.Equal(1003L, allocated.Max());
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
            Area = AreaType.S2Ground,
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
