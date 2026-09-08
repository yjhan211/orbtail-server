using game_server.matches;
using System.Collections.Immutable;
using System.Diagnostics;
using game_server.network;
using game_server.sessions;
using Microsoft.Extensions.Logging;
using network.common;
using network.packets;
using static game_server.network.SessionSnapshotDelivery;

namespace game_server.services;

/// <summary>
///     전달받은 매치의 봇 이동 계획을 실행하고 수신자 스냅샷에 패킷을 보낸 뒤 처리 시간을 기록한다.
///     호출자는 매치 잠금을 보유한다. 다른 매치 조회나 타이머 관리는 하지 않는다.
///     봇의 전술 판단은 전달받은 함수에 위임한다.
/// </summary>
internal sealed class BotMovementService(
    GameEventLogManager eventLogs,
    ILogger<BotMovementService> logger)
{
    // MatchTickRunner가 전투 뒤 같은 매치 잠금 안에서 봇 이동을 실행한다.

    /// <summary>매치 잠금 안에서 봇 걸음을 확정하고 같은 순서로 바로 송신한다.</summary>
    public void Process(MatchRuntime runtime, Func<long, long, SwarmBotDirective> resolveDirective)
    {
        long matchingId = runtime.MatchingId;
        long tickStartedAt = Stopwatch.GetTimestamp();
        GameClientSession[] sessionSnapshot = runtime.Sessions.Snapshot()
            .Where(session =>
                session.PlayerId is > 0 &&
                session.CurrentMapId == Config.SWARM_MATCH_MAP &&
                session.MatchingId == matchingId)
            .ToArray();
        ImmutableArray<SwarmBotObserverSnapshot> observers =
            CaptureSwarmBotObservers(matchingId, sessionSnapshot);
        double sessionSnapshotElapsedMilliseconds =
            Stopwatch.GetElapsedTime(tickStartedAt).TotalMilliseconds;
        SwarmBotMovementPlan plan = runtime.BotMovement.PrepareTick(
            eventLogs,
            observers,
            resolveDirective);

        long dispatchStartedAt = Stopwatch.GetTimestamp();
        DispatchSwarmBotMovementPlan(plan, sessionSnapshot);
        double broadcastElapsedMilliseconds =
            plan.DispatchPreparationElapsedMilliseconds +
            Stopwatch.GetElapsedTime(dispatchStartedAt).TotalMilliseconds;

        SwarmBotTickMetricsBatch? batch = runtime.Swarm.BotTickMetrics.Record(
            matchingId,
            new SwarmBotTickSample(
                Stopwatch.GetElapsedTime(tickStartedAt).TotalMilliseconds,
                sessionSnapshotElapsedMilliseconds + plan.SnapshotElapsedMilliseconds,
                plan.PlanningElapsedMilliseconds,
                plan.WalkingElapsedMilliseconds,
                broadcastElapsedMilliseconds));
        if (batch != null)
            PublishBotMovementMetrics(batch);
    }

    private void PublishBotMovementMetrics(SwarmBotTickMetricsBatch batch)
    {
        double[] sortedTickSamples = batch.TickSamples.OrderBy(value => value).ToArray();
        double p50Milliseconds = CalculatePercentile(sortedTickSamples, 0.50);
        double p95Milliseconds = CalculatePercentile(sortedTickSamples, 0.95);
        double p99Milliseconds = CalculatePercentile(sortedTickSamples, 0.99);
        double snapshotP95Milliseconds = CalculatePercentile(
            batch.SnapshotSamples.OrderBy(value => value).ToArray(),
            0.95);
        double planningP95Milliseconds = CalculatePercentile(
            batch.PlanningSamples.OrderBy(value => value).ToArray(),
            0.95);
        double walkingP95Milliseconds = CalculatePercentile(
            batch.WalkingSamples.OrderBy(value => value).ToArray(),
            0.95);
        double broadcastP95Milliseconds = CalculatePercentile(
            batch.BroadcastSamples.OrderBy(value => value).ToArray(),
            0.95);
        logger.LogInformation(
            "Bot movement tick: MatchingId={MatchingId} avg={Avg:F1}ms max={Max:F1}ms skips={Skips} over {Count} ticks; " +
            "p95 snapshot={SnapshotP95:F1}ms planning={PlanningP95:F1}ms walking={WalkingP95:F1}ms " +
            "broadcast={BroadcastP95:F1}ms",
            batch.MatchingId,
            batch.TotalElapsedMilliseconds / batch.TickSamples.Length,
            batch.MaxElapsedMilliseconds,
            batch.BusySkips,
            batch.TickSamples.Length,
            snapshotP95Milliseconds,
            planningP95Milliseconds,
            walkingP95Milliseconds,
            broadcastP95Milliseconds);
        eventLogs.LogBotMovementTickPerformance(
            batch.MatchingId,
            p50Milliseconds,
            p95Milliseconds,
            p99Milliseconds,
            snapshotP95Milliseconds,
            planningP95Milliseconds,
            walkingP95Milliseconds,
            broadcastP95Milliseconds,
            batch.TickSamples.Length,
            batch.BusySkips,
            batch.MaxConsecutiveBusySkips);
    }

    private static ImmutableArray<SwarmBotObserverSnapshot> CaptureSwarmBotObservers(
        long matchingId,
        IReadOnlyList<GameClientSession> sessionSnapshot)
    {
        var observers = ImmutableArray.CreateBuilder<SwarmBotObserverSnapshot>();
        for (int ordinal = 0; ordinal < sessionSnapshot.Count; ordinal++)
        {
            GameClientSession session = sessionSnapshot[ordinal];
            if (session.PlayerId is not > 0 ||
                session.CurrentMapId != Config.SWARM_MATCH_MAP ||
                session.MatchingId != matchingId)
                continue;

            SwarmVectorSnapshot? position = session.LastValidatedPosition == null
                ? null
                : SwarmVectorSnapshot.Capture(session.LastValidatedPosition);
            observers.Add(new SwarmBotObserverSnapshot(
                ordinal,
                session.PlayerId.Value,
                session.CurrentArea,
                session.IsEliminated,
                position));
        }

        return observers.ToImmutable();
    }

    private void DispatchSwarmBotMovementPlan(
        SwarmBotMovementPlan plan,
        IReadOnlyList<GameClientSession> sessionSnapshot)
    {
        foreach (SwarmBotMovementDispatch movement in plan.Movements)
        {
            if (movement.IsAreaTransition)
            {
                using var leavePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(movement.BotPlayerId);
                SendToCapturedRecipients(leavePacket, movement.LeaveRecipientOrdinals, sessionSnapshot);

                if (movement.EnteringBot != null)
                {
                    using var enterPacket = PacketMaker.G_TO_C_AREA_PLAYER_ENTER(
                        movement.EnteringBot.ToGameObjectInfo());
                    SendToCapturedRecipients(enterPacket, movement.DestinationRecipientOrdinals, sessionSnapshot);
                }
            }

            using (var movePacket = PacketMaker.G_TO_C_MOVE(
                       movement.BotPlayerId,
                       movement.Position.ToVector3f(),
                       movement.Velocity.ToVector3f(),
                       movement.Rotation,
                       movement.ToCell.ToCell(),
                       movement.ServerTimestamp,
                       movement.OrbOrbitPhaseDegrees))
            {
                SendToCapturedRecipients(movePacket, movement.DestinationRecipientOrdinals, sessionSnapshot);
            }

            if (movement.Encounter is { } encounter &&
                TryGetCapturedValue(sessionSnapshot, encounter.TargetSessionOrdinal, out GameClientSession target))
            {
                using var encounterPacket = PacketMaker.G_TO_C_ENCOUNTER_REVEAL(
                    encounter.BotPlayerId,
                    encounter.Area,
                    encounter.EventType,
                    encounter.CooldownSeconds,
                    encounter.RevealDelayMs);
                target.TrySend(encounterPacket);
                logger.LogInformation(
                    "Bot corridor encounter event: Matching={MatchingId}, Bot={Bot}, Target={Target}, " +
                    "Area={Area}, EventType={EventType}",
                    plan.MatchingId,
                    encounter.BotPlayerId,
                    encounter.TargetPlayerId,
                    encounter.Area,
                    encounter.EventType);
            }
        }

        foreach (SwarmBotGroundItemRemovalDispatch removal in plan.GroundItemRemovals)
        {
            using var packet = PacketMaker.G_TO_C_GROUND_ITEM_REMOVED(
                removal.GroundItemUid,
                removal.BotPlayerId,
                removal.AutoUsed);
            SendToCapturedRecipients(packet, removal.RecipientOrdinals, sessionSnapshot);
        }


    }

    public void DispatchExternalMovement(MatchRuntime runtime, BotMovementEvent movement)
    {
        long matchingId = runtime.MatchingId;
        GameClientSession[] sessionSnapshot = runtime.Sessions.Snapshot()
            .Where(session =>
                session.PlayerId is > 0 &&
                session.CurrentMapId == Config.SWARM_MATCH_MAP &&
                session.MatchingId == matchingId)
            .ToArray();
        ImmutableArray<SwarmBotObserverSnapshot> observers =
            CaptureSwarmBotObservers(matchingId, sessionSnapshot);
        SwarmBotMovementPlan plan = runtime.BotMovement.PrepareExternalMovement(
            eventLogs,
            movement,
            observers);
        DispatchSwarmBotMovementPlan(plan, sessionSnapshot);
    }

    private static double CalculatePercentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
            return 0d;

        double position = (sortedValues.Count - 1) * Math.Clamp(percentile, 0d, 1d);
        int lowerIndex = (int)Math.Floor(position);
        int upperIndex = (int)Math.Ceiling(position);
        if (lowerIndex == upperIndex)
            return sortedValues[lowerIndex];
        double fraction = position - lowerIndex;
        return sortedValues[lowerIndex] + (sortedValues[upperIndex] - sortedValues[lowerIndex]) * fraction;
    }
}
