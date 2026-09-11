using System.Collections.Concurrent;
using game_server.matches;
using game_server.matches.combat;
using game_server.players;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class MatchGameplayStateTests
{
    private static Player GetOrRegisterPlayer(MatchRuntime runtime, long playerId)
    {
        if (runtime.GetParticipant(playerId) is { } player) return player;
        player = new Player { Profile = new PlayerInfo { PlayerId = playerId } };
        runtime.RegisterParticipant(player);
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
        GetOrRegisterPlayer(ended, 1).IncrementOrbUpgradeCount(SunOrbGroupId);
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
    public void WindOrbAttackState_PreservesTimingBoundariesAndEngagementReset()
    {
        var state = new WindOrbAttackState();
        var player = new Player { Profile = new PlayerInfo { PlayerId = 10 } };
        DateTime nowUtc = new(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(player.TryBeginWindOrbTick(100, nowUtc, 1d));
        Assert.False(player.TryBeginWindOrbTick(100, nowUtc.AddMilliseconds(999), 1d));
        Assert.True(player.TryBeginWindOrbTick(100, nowUtc.AddSeconds(1), 1d));

        Assert.False(player.HasCompletedWindOrbSpinup(100, nowUtc, 0.2f));
        Assert.False(player.HasCompletedWindOrbSpinup(100, nowUtc.AddMilliseconds(200), 0.2f));
        Assert.True(player.HasCompletedWindOrbSpinup(100, nowUtc.AddMilliseconds(200).AddTicks(1), 0.2f));
        player.ResetWindOrbEngagement(100);
        Assert.False(player.HasCompletedWindOrbSpinup(100, nowUtc.AddSeconds(1), 0.2f));

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
    public void Player_CountsOrbUpgradesPerGroup()
    {
        var state = new Player { Profile = new PlayerInfo { PlayerId = 10 } };
        var bot = new Player { Profile = new PlayerInfo { PlayerId = -20 } };

        Assert.Equal(0, state.GetOrbUpgradeCount(SunOrbGroupId));
        Assert.Equal(1, state.IncrementOrbUpgradeCount(SunOrbGroupId));
        Assert.Equal(2, state.IncrementOrbUpgradeCount(SunOrbGroupId));
        Assert.Equal(0, state.GetOrbUpgradeCount(WindOrbGroupId));
        Assert.Equal(1, bot.IncrementOrbUpgradeCount(SunOrbGroupId));
        Assert.Equal(2, state.GetOrbUpgradeCount(SunOrbGroupId));
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

        first.WindOrbAttacks.ApplyWound(playerId, nowUtc.AddMinutes(1));
        Assert.True(first.WindOrbAttacks.IsWounded(playerId, nowUtc));
        Assert.False(second.WindOrbAttacks.IsWounded(playerId, nowUtc));
        Assert.False(GetOrRegisterPlayer(first, playerId).HasCompletedWindOrbSpinup(itemUid, nowUtc, 1d));
        Assert.False(GetOrRegisterPlayer(second, playerId).HasCompletedWindOrbSpinup(itemUid, nowUtc.AddSeconds(2), 1d));

        Assert.True(GetOrRegisterPlayer(first, playerId).TryBeginWindOrbTick(itemUid, nowUtc, 1d));
        Assert.False(GetOrRegisterPlayer(first, playerId).TryBeginWindOrbTick(itemUid, nowUtc, 1d));
        Assert.True(GetOrRegisterPlayer(second, playerId).TryBeginWindOrbTick(itemUid, nowUtc, 1d));
        Assert.True(first.WindOrbAttacks.TryClaimVictimShock(playerId, nowUtc, 1d));
        Assert.False(first.WindOrbAttacks.TryClaimVictimShock(playerId, nowUtc, 1d));
        Assert.True(second.WindOrbAttacks.TryClaimVictimShock(playerId, nowUtc, 1d));

        Assert.Equal(1, GetOrRegisterPlayer(first, playerId).IncrementOrbUpgradeCount(SunOrbGroupId));
        Assert.Equal(0, GetOrRegisterPlayer(second, playerId).GetOrbUpgradeCount(SunOrbGroupId));
        Assert.Equal(1, GetOrRegisterPlayer(second, playerId).IncrementOrbUpgradeCount(SunOrbGroupId));
        Assert.Equal(1, GetOrRegisterPlayer(first, playerId).GetOrbUpgradeCount(SunOrbGroupId));

        first.SunOrbAttacks.AddShape(CreateCrossfireShape(
            eventId: 11,
            ownerId: playerId,
            anchorCombatTargetId: 7001,
            armedAtUtc: nowUtc.AddSeconds(1)));
        first.SunOrbAttacks.SetSunBurn(
            playerId,
            ownerId: playerId,
            weaponItemId: 101,
            AreaType.S2Ground,
            nowUtc,
            durationSeconds: 3d,
            tickIntervalSeconds: 1d);
        SwarmCrossfireConvergenceObservation firstConvergence =
            first.SunOrbAttacks.TrackConvergence(7001, nowUtc);

        Assert.Equal(1, firstConvergence.HitCount);
        Assert.True(second.SunOrbAttacks.IsEmpty);
        Assert.Equal(first.MatchingId, Assert.Single(first.SunOrbAttacks.DodgeSnapshot).MatchingId);

        second.SunOrbAttacks.AddShape(CreateCrossfireShape(
            eventId: 12,
            ownerId: playerId,
            anchorCombatTargetId: 7001,
            armedAtUtc: nowUtc.AddSeconds(1)));
        second.SunOrbAttacks.SetSunBurn(
            playerId,
            ownerId: playerId + 1,
            weaponItemId: 202,
            AreaType.S2Gym1,
            nowUtc,
            durationSeconds: 4d,
            tickIntervalSeconds: 2d);
        SwarmCrossfireConvergenceObservation secondConvergence =
            second.SunOrbAttacks.TrackConvergence(7001, nowUtc);

        Assert.Equal(1, secondConvergence.HitCount);
        Assert.Equal(first.MatchingId, Assert.Single(first.SunOrbAttacks.DodgeSnapshot).MatchingId);
        Assert.Equal(second.MatchingId, Assert.Single(second.SunOrbAttacks.DodgeSnapshot).MatchingId);
        Assert.True(first.SunOrbAttacks.TryGetSunBurn(playerId, out SwarmSunBurnState? firstBurn));
        Assert.True(second.SunOrbAttacks.TryGetSunBurn(playerId, out SwarmSunBurnState? secondBurn));
        Assert.Equal(playerId, firstBurn!.OwnerId);
        Assert.Equal(playerId + 1, secondBurn!.OwnerId);
        Assert.Equal(1, first.SunOrbAttacks.ConvergenceWindowCount);
        Assert.Equal(1, second.SunOrbAttacks.ConvergenceWindowCount);
    }

    [Fact]
    public void GetOrCreate_ReturnsSameRuntimeForSameIdAndIsolatesDifferentIds()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        const long firstMatchingId = 42001;
        const long secondMatchingId = 42002;

        MatchRuntime first = store.GetOrCreate(firstMatchingId);
        MatchRuntime firstAgain = store.GetOrCreate(firstMatchingId);
        MatchRuntime second = store.GetOrCreate(secondMatchingId);

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

        MatchRuntime removed = store.GetOrCreate(removedMatchingId);
        MatchRuntime sibling = store.GetOrCreate(siblingMatchingId);

        Assert.True(store.Remove(removedMatchingId));
        MatchRuntime? missing = store.GetOrNull(removedMatchingId);
        Assert.Null(missing);
        MatchRuntime? preservedSibling = store.GetOrNull(siblingMatchingId);
        Assert.Same(sibling, preservedSibling);
        Assert.Equal(1, store.Count);

        MatchRuntime recreated = store.GetOrCreate(removedMatchingId);

        Assert.NotSame(removed, recreated);
        Assert.Equal(removedMatchingId, recreated.MatchingId);
        Assert.Same(sibling, store.GetOrCreate(siblingMatchingId));
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

        MatchRuntime removed = store.GetOrCreate(removedMatchingId);
        MatchRuntime sibling = store.GetOrCreate(siblingMatchingId);

        removed.TrailCombat.OrbTrails[removedPlayerId] = [];
        removed.BotTactics.FleeDirective.Add(removedPlayerId);
        removed.WindOrbAttacks.ApplyWound(removedPlayerId, DateTime.MaxValue);
        GetOrRegisterPlayer(removed, removedPlayerId).IncrementOrbUpgradeCount(WindOrbGroupId);
        removed.SunOrbAttacks.AddShape(CreateCrossfireShape(
            eventId: 11,
            ownerId: removedPlayerId,
            anchorCombatTargetId: 7001,
            armedAtUtc: crossfireNowUtc.AddSeconds(1)));
        removed.SunOrbAttacks.SetSunBurn(
            removedPlayerId,
            ownerId: removedPlayerId,
            weaponItemId: 101,
            AreaType.S2Ground,
            crossfireNowUtc,
            durationSeconds: 3d,
            tickIntervalSeconds: 1d);
        removed.SunOrbAttacks.TrackConvergence(7001, crossfireNowUtc);

        sibling.TrailCombat.OrbTrails[siblingPlayerId] = [];
        sibling.BotTactics.FleeDirective.Add(siblingPlayerId);
        sibling.WindOrbAttacks.ApplyWound(siblingPlayerId, DateTime.MaxValue);
        GetOrRegisterPlayer(sibling, siblingPlayerId).IncrementOrbUpgradeCount(WaveOrbGroupId);
        sibling.SunOrbAttacks.AddShape(CreateCrossfireShape(
            eventId: 12,
            ownerId: siblingPlayerId,
            anchorCombatTargetId: 8001,
            armedAtUtc: crossfireNowUtc.AddSeconds(1)));
        sibling.SunOrbAttacks.SetSunBurn(
            siblingPlayerId,
            ownerId: siblingPlayerId,
            weaponItemId: 202,
            AreaType.S2Gym1,
            crossfireNowUtc,
            durationSeconds: 4d,
            tickIntervalSeconds: 2d);
        sibling.SunOrbAttacks.TrackConvergence(8001, crossfireNowUtc);

        Assert.True(store.Remove(removedMatchingId));

        MatchRuntime? missing = store.GetOrNull(removedMatchingId);
        Assert.Null(missing);
        MatchRuntime? preservedSibling = store.GetOrNull(siblingMatchingId);
        Assert.Same(sibling, preservedSibling);
        Assert.Contains(siblingPlayerId, sibling.TrailCombat.OrbTrails.Keys);
        Assert.Contains(siblingPlayerId, sibling.BotTactics.FleeDirective);
        Assert.True(sibling.WindOrbAttacks.IsWounded(siblingPlayerId, DateTime.UtcNow));
        Assert.Equal(1, GetOrRegisterPlayer(sibling, siblingPlayerId).GetOrbUpgradeCount(WaveOrbGroupId));
        Assert.Equal(1, sibling.SunOrbAttacks.ShapeCount);
        Assert.Equal(1, sibling.SunOrbAttacks.SunBurnCount);
        Assert.Equal(1, sibling.SunOrbAttacks.ConvergenceWindowCount);
        Assert.Equal(siblingMatchingId, Assert.Single(sibling.SunOrbAttacks.DodgeSnapshot).MatchingId);
        Assert.True(sibling.SunOrbAttacks.TryGetSunBurn(
            siblingPlayerId,
            out SwarmSunBurnState? siblingBurn));
        Assert.Equal(siblingPlayerId, siblingBurn!.OwnerId);
        Assert.Equal(1, store.Count);

        MatchRuntime replacement = store.GetOrCreate(removedMatchingId);
        Assert.NotSame(removed, replacement);
        Assert.Empty(replacement.TrailCombat.OrbTrails);
        Assert.Empty(replacement.BotTactics.FleeDirective);
        Assert.False(replacement.WindOrbAttacks.IsWounded(removedPlayerId, DateTime.UtcNow));
        Assert.Equal(0, GetOrRegisterPlayer(replacement, removedPlayerId).GetOrbUpgradeCount(WindOrbGroupId));
        Assert.NotSame(removed.SunOrbAttacks, replacement.SunOrbAttacks);
        Assert.True(replacement.SunOrbAttacks.IsEmpty);
        Assert.Empty(replacement.SunOrbAttacks.DodgeSnapshot);
    }

    [Fact]
    public void CrossfireEventIds_RemainProcessWideAndThreadSafeAcrossRuntimeRecreation()
    {
        var store = TestGameSessionServices.CreateMatchRuntimeStore(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        const long firstMatchingId = 42011;
        const long secondMatchingId = 42012;

        MatchRuntime first = store.GetOrCreate(firstMatchingId);
        MatchRuntime second = store.GetOrCreate(secondMatchingId);

        long firstId = first.SunOrbAttacks.AllocateEventId();
        long secondId = second.SunOrbAttacks.AllocateEventId();
        Assert.True(secondId > firstId);

        Assert.True(store.Remove(firstMatchingId));
        MatchRuntime recreated = store.GetOrCreate(firstMatchingId);
        long recreatedId = recreated.SunOrbAttacks.AllocateEventId();
        Assert.True(recreatedId > secondId);

        var allocated = new ConcurrentBag<long>();
        Parallel.For(
            0,
            1000,
            index => allocated.Add(
                (index & 1) == 0
                    ? recreated.SunOrbAttacks.AllocateEventId()
                    : second.SunOrbAttacks.AllocateEventId()));

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
