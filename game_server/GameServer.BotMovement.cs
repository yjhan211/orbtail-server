using System.Collections.Immutable;
using System.Diagnostics;
using game_server.network;
using game_server.services;
using Microsoft.Extensions.Logging;
using network.common;
using network.packets;

namespace game_server;

public partial class GameServer
{
    private const int BotMovementTickIntervalMs = 50;

    private void StartBotMovementTimer()
    {
        _botMovementTimer = new Timer(ProcessBotMovement, null,
            TimeSpan.FromMilliseconds(BotMovementTickIntervalMs),
            TimeSpan.FromMilliseconds(BotMovementTickIntervalMs));
        logger.LogInformation("봇 walking 타이머 시작 ({Ms}ms 간격)", BotMovementTickIntervalMs);
    }

    /// <summary>
    ///     50ms 펄스마다 매치별 워커를 띄운다. 모니터는 스레드 친화적이라 잠금 시도는 워커 안에서 한다 —
    ///     잠금이 바쁜 매치는 이 펄스를 버리고(따라잡기 없음) 다른 매치는 그대로 진행한다.
    /// </summary>
    private void ProcessBotMovement(object? state)
    {
        try
        {
            GameClientSession[] activeSessions = _sessionRegistry
                .SnapshotWhere(static session => session.PlayerId.HasValue)
                .ToArray();
            Task[] workers = MatchRuntimes.ActiveIds()
                .Select(matchingId => Task.Run(() => RunBotMovementWorker(matchingId, activeSessions)))
                .ToArray();
            Task.WhenAll(workers).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "One or more bot movement workers failed");
        }
    }

    private void RunBotMovementWorker(long matchingId, GameClientSession[] activeSessions)
    {
        try
        {
            if (!MatchRuntimes.TryEnter(matchingId, out MatchScope scope))
            {
                if (ShouldTrackBotTickBusySkip(matchingId) &&
                    _swarmMatchRuntimes.TryGet(matchingId, out SwarmMatchRuntime? busyRuntime))
                {
                    busyRuntime.BotTickMetrics.RecordBusySkip();
                }

                return;
            }

            using (scope)
            {
                if (scope.Runtime.IsTerminal)
                    return;

                BroadcastMatchStartCountdowns([matchingId], activeSessions);
                if (!ShouldTrackBotTickBusySkip(matchingId))
                    return;

                ProcessBotMovementForMatching(matchingId);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Bot walking tick failed: MatchingId={MatchingId}", matchingId);
        }
    }

    /// <summary>봇이 실제로 걷는 매치만 바쁜 펄스를 계측한다 — 카운트다운·봇 없는 매치는 계측 잡음이다.</summary>
    private bool ShouldTrackBotTickBusySkip(long matchingId) =>
        MatchStartGate.IsGameplayActive(matchingId) && _botPlayerManager.HasBots(matchingId);

    /// <summary>매치 잠금 안에서 봇 걸음을 확정하고 같은 순서로 바로 송신한다.</summary>
    private void ProcessBotMovementForMatching(long matchingId)
    {
        long tickStartedAt = Stopwatch.GetTimestamp();
        GameClientSession[] sessionSnapshot = _sessionRegistry.GetByMatch(matchingId)
            .Where(session =>
                session.PlayerId is > 0 &&
                session.CurrentMapId == Config.SWARM_MATCH_MAP &&
                session.CurrentMapSubId == matchingId)
            .ToArray();
        ImmutableArray<SwarmBotObserverSnapshot> observers =
            CaptureSwarmBotObservers(matchingId, sessionSnapshot);
        double sessionSnapshotElapsedMilliseconds =
            Stopwatch.GetElapsedTime(tickStartedAt).TotalMilliseconds;
        SwarmBotMovementPlan plan = _swarmBotMovementCoordinator.PrepareTick(
            matchingId,
            observers,
            ResolveSwarmBotDirective);

        long dispatchStartedAt = Stopwatch.GetTimestamp();
        DispatchSwarmBotMovementPlan(plan, sessionSnapshot);
        double broadcastElapsedMilliseconds =
            plan.DispatchPreparationElapsedMilliseconds +
            Stopwatch.GetElapsedTime(dispatchStartedAt).TotalMilliseconds;

        SwarmBotTickMetricsBatch? batch = GetSwarmMatchRuntime(matchingId).BotTickMetrics.Record(
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
        _gameEventLogManager.LogBotMovementTickPerformance(
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
                session.CurrentMapSubId != matchingId)
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
                        movement.EnteringBot.ToPlayerInfo(),
                        movement.ToCell.ToCell());
                    SendToCapturedRecipients(enterPacket, movement.DestinationRecipientOrdinals, sessionSnapshot);
                }
            }

            using (var movePacket = PacketMaker.G_TO_C_MOVE(
                       movement.BotPlayerId,
                       movement.Position.ToVector3f(),
                       movement.Velocity.ToVector3f(),
                       movement.Rotation,
                       movement.ToCell.ToCell(),
                       lastProcessedInput: 0u,
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
                target.Send(encounterPacket);
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

        foreach (SwarmBotPlayerInfoDispatch autoEquip in plan.AutoEquips)
        {
            using var packet = PacketMaker.G_TO_C_PLAYER_INFO([autoEquip.PlayerInfo.ToPlayerInfo()]);
            SendToCapturedRecipients(packet, autoEquip.RecipientOrdinals, sessionSnapshot);
        }
    }

    private static void SendToCapturedRecipients(
        Packet packet,
        ImmutableArray<int> recipientOrdinals,
        IReadOnlyList<GameClientSession> sessionSnapshot)
    {
        foreach (int ordinal in recipientOrdinals)
        {
            if (TryGetCapturedValue(sessionSnapshot, ordinal, out GameClientSession session))
                session.Send(packet);
        }
    }

    internal static bool TryGetCapturedValue<T>(
        IReadOnlyList<T> values,
        int ordinal,
        out T value)
    {
        if ((uint)ordinal < (uint)values.Count)
        {
            value = values[ordinal];
            return true;
        }

        value = default!;
        return false;
    }
}
