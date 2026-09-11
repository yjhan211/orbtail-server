using game_server.matches;
using game_server.matches.combat;
using game_server.players.bots;
using network.common;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class SunOrbAttackStateTests
{
    private static readonly DateTime NowUtc =
        new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ShapeQueries_PreserveTelegraphBoundariesAnchorsAndRemovalLifecycle()
    {
        SunOrbAttackState state = CreateState();
        SwarmCrossfireShape first = CreateShape(
            eventId: 1,
            ownerId: 10,
            anchorCombatTargetId: 101,
            armedAtUtc: NowUtc.AddSeconds(1));
        SwarmCrossfireShape second = CreateShape(
            eventId: 2,
            ownerId: 10,
            anchorCombatTargetId: 102,
            armedAtUtc: NowUtc.AddSeconds(2));

        state.AddShape(first);
        state.AddShape(second);

        Assert.Equal(2, state.ShapeCount);
        Assert.Same(first, state.GetShapeAt(0));
        Assert.Same(second, state.GetShapeAt(1));
        Assert.Equal(2, state.CountTelegraphing(10, NowUtc));
        Assert.Contains(10, state.CollectCappedOwners(NowUtc, maximumTelegraphsPerOwner: 2));
        Assert.Equal(
            [(10L, 101L), (10L, 102L)],
            state.CollectAnchoredTargets().OrderBy(anchor => anchor.CombatTargetId));

        Assert.Equal(1, state.CountTelegraphing(10, NowUtc.AddSeconds(1)));
        Assert.DoesNotContain(
            10,
            state.CollectCappedOwners(NowUtc.AddSeconds(1), maximumTelegraphsPerOwner: 2));

        state.RemoveShapeAt(1);
        state.PublishDodgeSnapshot();
        Assert.Equal(1, state.ShapeCount);
        Assert.Single(state.DodgeSnapshot);
        Assert.Equal([(10L, 101L)], state.CollectAnchoredTargets());

        state.RemoveShapeAt(0);
        state.PublishDodgeSnapshot();
        Assert.Equal(0, state.ShapeCount);
        Assert.Empty(state.DodgeSnapshot);
        Assert.Empty(state.CollectAnchoredTargets());
        Assert.Empty(state.CollectCappedOwners(NowUtc, maximumTelegraphsPerOwner: 1));
        Assert.True(state.IsEmpty);
    }

    [Fact]
    public void DodgeSnapshot_IsGroundScaledImmutableAndPublishedAfterCompletedRemovalPass()
    {
        const long matchingId = 43001;
        SunOrbAttackState state = CreateState(matchingId);
        SwarmCrossfireShape first = CreateShape(
            eventId: 1,
            ownerId: 10,
            anchorCombatTargetId: 101,
            armedAtUtc: NowUtc.AddSeconds(1),
            origin: new Vector3f(1f, 2f, 0f),
            end: new Vector3f(4f, 4f, 0f),
            groundLength: 7f,
            halfWidth: 0.4f,
            sweepSpeed: 5f);

        state.AddShape(first);
        IReadOnlyList<SwarmBotDodgePolicy.SwarmCrossfireDodgeThreat> firstSnapshot =
            state.DodgeSnapshot;
        SwarmBotDodgePolicy.SwarmCrossfireDodgeThreat threat = Assert.Single(firstSnapshot);

        Assert.Equal(matchingId, threat.MatchingId);
        Assert.Equal(first.Area, threat.Area);
        Assert.Equal(first.OwnerId, threat.OwnerId);
        Assert.Equal(1f, threat.OriginX);
        Assert.Equal(2f, threat.OriginY);
        Assert.Equal(0.6f, threat.AxisX, 3);
        Assert.Equal(0.8f, threat.AxisY, 3);
        Assert.Equal(7f, threat.GroundLength);
        Assert.Equal(0.4f, threat.HalfWidth);
        Assert.Equal(5f, threat.SweepSpeed);
        Assert.Equal(first.ArmedAtUtc, threat.ArmedAtUtc);
        Assert.Equal(first.ExpiresAtUtc, threat.ExpiresAtUtc);

        state.AddShape(CreateShape(
            eventId: 2,
            ownerId: 20,
            anchorCombatTargetId: 202,
            armedAtUtc: NowUtc,
            origin: new Vector3f(5f, 5f, 0f),
            end: new Vector3f(5.001f, 5.001f, 0f)));
        Assert.Single(state.DodgeSnapshot);
        Assert.Single(firstSnapshot);

        state.AddShape(CreateShape(
            eventId: 3,
            ownerId: 30,
            anchorCombatTargetId: 303,
            armedAtUtc: NowUtc,
            origin: new Vector3f(0f, 0f, 0f),
            end: new Vector3f(2f, 0f, 0f)));
        IReadOnlyList<SwarmBotDodgePolicy.SwarmCrossfireDodgeThreat> expandedSnapshot =
            state.DodgeSnapshot;
        Assert.Equal(2, expandedSnapshot.Count);
        Assert.Single(firstSnapshot);
        Assert.NotSame(firstSnapshot, expandedSnapshot);

        state.RemoveShapeAt(2);
        Assert.Same(expandedSnapshot, state.DodgeSnapshot);
        state.PublishDodgeSnapshot();
        Assert.Single(state.DodgeSnapshot);
        Assert.Equal(2, expandedSnapshot.Count);
    }

    [Fact]
    public void SunBurn_RefreshesPayloadAndUsesInclusiveDueAndExpiryBoundaries()
    {
        SunOrbAttackState state = CreateState();
        const long victimId = 20;
        var applied = new List<(long VictimId, SwarmSunBurnState Burn)>();

        state.SetSunBurn(
            victimId,
            ownerId: 10,
            weaponItemId: 101,
            AreaType.S2Ground,
            NowUtc,
            durationSeconds: 3d,
            tickIntervalSeconds: 1d);

        state.ProcessSunBurns(
            NowUtc.AddMilliseconds(999),
            tickIntervalSeconds: 1d,
            (id, burn) => applied.Add((id, burn)));
        Assert.Empty(applied);

        state.ProcessSunBurns(
            NowUtc.AddSeconds(1),
            tickIntervalSeconds: 1d,
            (id, burn) => applied.Add((id, burn)));
        Assert.Equal(victimId, Assert.Single(applied).VictimId);
        Assert.True(state.TryGetSunBurn(victimId, out SwarmSunBurnState? firstTick));
        Assert.Equal(NowUtc.AddSeconds(2), firstTick!.NextTickAtUtc);

        DateTime refreshedAtUtc = NowUtc.AddMilliseconds(1500);
        state.SetSunBurn(
            victimId,
            ownerId: 11,
            weaponItemId: 202,
            AreaType.S2Gym1,
            refreshedAtUtc,
            durationSeconds: 3d,
            tickIntervalSeconds: 1d);
        Assert.True(state.TryGetSunBurn(victimId, out SwarmSunBurnState? refreshed));
        Assert.Equal(11, refreshed!.OwnerId);
        Assert.Equal(202, refreshed.WeaponItemId);
        Assert.Equal(AreaType.S2Gym1, refreshed.Area);
        Assert.Equal(refreshedAtUtc.AddSeconds(3), refreshed.UntilUtc);
        Assert.Equal(refreshedAtUtc.AddSeconds(1), refreshed.NextTickAtUtc);

        applied.Clear();
        state.ProcessSunBurns(
            NowUtc.AddSeconds(2),
            tickIntervalSeconds: 1d,
            (id, burn) => applied.Add((id, burn)));
        Assert.Empty(applied);

        state.ProcessSunBurns(
            refreshedAtUtc.AddSeconds(1),
            tickIntervalSeconds: 1d,
            (id, burn) => applied.Add((id, burn)));
        SwarmSunBurnState appliedRefresh = Assert.Single(applied).Burn;
        Assert.Equal(11, appliedRefresh.OwnerId);
        Assert.Equal(202, appliedRefresh.WeaponItemId);

        applied.Clear();
        state.ProcessSunBurns(
            refreshedAtUtc.AddSeconds(3),
            tickIntervalSeconds: 1d,
            (id, burn) => applied.Add((id, burn)));
        Assert.Single(applied);
        Assert.Equal(0, state.SunBurnCount);
        Assert.False(state.TryGetSunBurn(victimId, out _));
        Assert.True(state.IsEmpty);
    }

    [Fact]
    public void SunBurn_CallbackFailureDoesNotAdvanceOrRemoveState()
    {
        SunOrbAttackState state = CreateState();
        const long victimId = 20;
        state.SetSunBurn(
            victimId,
            ownerId: 10,
            weaponItemId: 101,
            AreaType.S2Ground,
            NowUtc,
            durationSeconds: 1d,
            tickIntervalSeconds: 1d);

        Assert.Throws<InvalidOperationException>(() => state.ProcessSunBurns(
            NowUtc.AddSeconds(1),
            tickIntervalSeconds: 1d,
            (_, _) => throw new InvalidOperationException("simulated application failure")));

        Assert.True(state.TryGetSunBurn(victimId, out SwarmSunBurnState? burn));
        Assert.Equal(NowUtc.AddSeconds(1), burn!.NextTickAtUtc);
        Assert.Equal(NowUtc.AddSeconds(1), burn.UntilUtc);
        Assert.Equal(1, state.SunBurnCount);
    }

    [Fact]
    public void Convergence_UsesInclusiveOneSecondWindowAndResetsAfterIt()
    {
        SunOrbAttackState state = CreateState();

        SwarmCrossfireConvergenceObservation first = state.TrackConvergence(100, NowUtc);
        SwarmCrossfireConvergenceObservation exactBoundary =
            state.TrackConvergence(100, NowUtc.AddSeconds(1));
        SwarmCrossfireConvergenceObservation afterBoundary =
            state.TrackConvergence(100, NowUtc.AddSeconds(1).AddTicks(1));
        SwarmCrossfireConvergenceObservation otherTarget =
            state.TrackConvergence(200, NowUtc.AddSeconds(1).AddTicks(1));

        Assert.Equal(1, first.HitCount);
        Assert.Equal(0d, first.WindowMilliseconds);
        Assert.Equal(2, exactBoundary.HitCount);
        Assert.Equal(1000d, exactBoundary.WindowMilliseconds);
        Assert.Equal(1, afterBoundary.HitCount);
        Assert.Equal(0d, afterBoundary.WindowMilliseconds);
        Assert.Equal(1, otherTarget.HitCount);
        Assert.Equal(2, state.ConvergenceWindowCount);
    }

    private static SunOrbAttackState CreateState(long matchingId = 43000) =>
        TestGameSessionServices.CreateMatchRuntimeStore(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
            .GetOrCreate(matchingId).SunOrbAttacks;

    private static SwarmCrossfireShape CreateShape(
        long eventId,
        long ownerId,
        long anchorCombatTargetId,
        DateTime armedAtUtc,
        Vector3f? origin = null,
        Vector3f? end = null,
        float groundLength = 6f,
        float halfWidth = 0.35f,
        float sweepSpeed = 4.5f) =>
        new()
        {
            EventId = eventId,
            OwnerId = ownerId,
            WeaponItemId = 101,
            Damage = 10,
            Area = AreaType.S2Ground,
            Origin = origin ?? new Vector3f(0f, 0f, 0f),
            End = end ?? new Vector3f(6f, 0f, 0f),
            GroundLength = groundLength,
            HalfWidth = halfWidth,
            BlastRadius = 0.5f,
            SweepSpeed = sweepSpeed,
            ArmedAtUtc = armedAtUtc,
            ExpiresAtUtc = armedAtUtc.AddSeconds(2),
            AnchorMonsterId = 301,
            AnchorCombatTargetId = anchorCombatTargetId,
            LastFront = -halfWidth
        };
}
