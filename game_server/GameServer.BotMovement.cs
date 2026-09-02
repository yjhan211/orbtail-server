using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using game_server.network;
using game_server.services;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.core;
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

    private void ProcessBotMovement(object? state)
    {
        var workers = new List<Task>();
        Exception? schedulingFailure = null;
        try
        {
            GameClientSession[] activeSessions = _sessionRegistry
                .SnapshotWhere(static session => session.PlayerId.HasValue)
                .ToArray();
            IReadOnlyList<long> matchingIds = GetActiveMatchingIds();
            foreach (long matchingId in matchingIds)
            {
                Task? worker = TryStartBotMovementWorker(matchingId, activeSessions);
                if (worker != null)
                    workers.Add(worker);
            }
        }
        catch (Exception ex)
        {
            schedulingFailure = ex;
        }
        finally
        {
            try
            {
                Task.WhenAll(workers).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "One or more bot movement workers failed");
            }

            if (schedulingFailure != null)
            {
                logger.LogError(
                    schedulingFailure,
                    "Failed to schedule bot movement workers");
            }
        }
    }

    private Task? TryStartBotMovementWorker(
        long matchingId,
        GameClientSession[] activeSessions)
    {
        IDisposable? outerRuntimeOperation = null;
        SwarmBotTickCoordinator.SwarmBotTickLease? tickLease = null;
        try
        {
            bool trackBusySkip =
                MatchStartGate.IsGameplayActive(matchingId) &&
                _botPlayerManager.HasBots(matchingId);
            outerRuntimeOperation = _matchRuntimeRegistry.TryAcquireOperationIfAvailable(
                matchingId,
                () => _swarmBotTickCoordinator.TryBegin(
                    matchingId,
                    trackBusySkip,
                    out tickLease));
            if (outerRuntimeOperation == null)
            {
                if (trackBusySkip)
                    _swarmBotTickCoordinator.TryRecordBusySkip(matchingId);
                return null;
            }

            if (tickLease == null)
            {
                ReleaseBotMovementTickClaim(
                    matchingId,
                    outerRuntimeOperation,
                    tickLease);
                return null;
            }

            IDisposable capturedOperation = outerRuntimeOperation;
            SwarmBotTickCoordinator.SwarmBotTickLease capturedLease = tickLease;
            return Task.Run(
                () => ProcessBotMovementForMatching(
                    matchingId,
                    activeSessions,
                    capturedOperation,
                    capturedLease));
        }
        catch (Exception ex)
        {
            ReleaseBotMovementTickClaim(
                matchingId,
                outerRuntimeOperation,
                tickLease);
            logger.LogError(
                ex,
                "Failed to schedule bot movement worker: MatchingId={MatchingId}",
                matchingId);
            return null;
        }
    }

    private void ProcessBotMovementForMatching(
        long matchingId,
        GameClientSession[] activeSessions,
        IDisposable outerRuntimeOperation,
        SwarmBotTickCoordinator.SwarmBotTickLease tickLease)
    {
        var tickStartedAt = DateTime.UtcNow;
        double snapshotElapsedMilliseconds = 0d;
        double planningElapsedMilliseconds = 0d;
        double walkingElapsedMilliseconds = 0d;
        double broadcastElapsedMilliseconds = 0d;
        bool movementAttempted = false;
        SwarmBotTickMetricsBatch? metricsBatch = null;

        try
        {
            BroadcastMatchStartCountdowns([matchingId], activeSessions);
            if (!MatchStartGate.IsGameplayActive(matchingId) ||
                !_botPlayerManager.HasBots(matchingId))
            {
                return;
            }

            SwarmBotMovementPlan? plan = null;
            SwarmBotPublicationTicket? publicationTicket = null;
            GameClientSession[]? sessionSnapshot = null;
            if (!_matchRuntimeRegistry.TryExecute(
                    matchingId,
                    () =>
                    {
                        movementAttempted = true;
                        long snapshotStartedAt = Stopwatch.GetTimestamp();
                        sessionSnapshot = _sessionRegistry.GetByMatch(matchingId)
                            .Where(session =>
                                session.PlayerId is > 0 &&
                                session.CurrentMapId == Config.SWARM_MATCH_MAP &&
                                session.CurrentMapSubId == matchingId)
                            .ToArray();
                        ImmutableArray<SwarmBotObserverSnapshot> observers =
                            CaptureSwarmBotObservers(matchingId, sessionSnapshot);
                        double sessionSnapshotElapsedMilliseconds =
                            Stopwatch.GetElapsedTime(snapshotStartedAt).TotalMilliseconds;
                        plan = _swarmBotMovementCoordinator.PrepareTick(
                            matchingId,
                            observers,
                            ResolveSwarmBotDirective);
                        snapshotElapsedMilliseconds +=
                            sessionSnapshotElapsedMilliseconds + plan.SnapshotElapsedMilliseconds;
                        publicationTicket =
                            _swarmBotMovementCoordinator.ReservePublication(matchingId);
                    }))
            {
                return;
            }

            if (publicationTicket is not { } ticket)
                return;

            SwarmBotMovementPlan? capturedPlan = plan;
            GameClientSession[] capturedSessions = sessionSnapshot ?? [];
            if (capturedPlan != null)
            {
                planningElapsedMilliseconds += capturedPlan.PlanningElapsedMilliseconds;
                walkingElapsedMilliseconds += capturedPlan.WalkingElapsedMilliseconds;
                broadcastElapsedMilliseconds +=
                    capturedPlan.DispatchPreparationElapsedMilliseconds;
            }

            long dispatchStartedAt = Stopwatch.GetTimestamp();
            _swarmBotMovementCoordinator.DispatchInOrder(
                ticket,
                () =>
                {
                    if (capturedPlan != null)
                        DispatchSwarmBotMovementPlan(capturedPlan, capturedSessions);
                });
            broadcastElapsedMilliseconds +=
                Stopwatch.GetElapsedTime(dispatchStartedAt).TotalMilliseconds;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Bot walking tick failed: MatchingId={MatchingId}",
                matchingId);
        }
        finally
        {
            try
            {
                if (movementAttempted)
                {
                    metricsBatch = _swarmBotTickCoordinator.Record(
                        tickLease,
                        new SwarmBotTickSample(
                            (DateTime.UtcNow - tickStartedAt).TotalMilliseconds,
                            snapshotElapsedMilliseconds,
                            planningElapsedMilliseconds,
                            walkingElapsedMilliseconds,
                            broadcastElapsedMilliseconds));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to record bot movement tick metrics: MatchingId={MatchingId}",
                    matchingId);
            }
            finally
            {
                ReleaseBotMovementTickClaim(
                    matchingId,
                    outerRuntimeOperation,
                    tickLease);
            }

            if (metricsBatch != null)
            {
                try
                {
                    PublishBotMovementMetrics(metricsBatch);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Failed to publish bot movement metrics: MatchingId={MatchingId}",
                        matchingId);
                }
            }
        }
    }

    private void ReleaseBotMovementTickClaim(
        long matchingId,
        IDisposable? outerRuntimeOperation,
        SwarmBotTickCoordinator.SwarmBotTickLease? tickLease)
    {
        try
        {
            outerRuntimeOperation?.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to release bot movement runtime lease: MatchingId={MatchingId}",
                matchingId);
        }
        finally
        {
            if (tickLease != null)
            {
                try
                {
                    _swarmBotTickCoordinator.Retire(tickLease);
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "Failed to retire bot movement tick: MatchingId={MatchingId}",
                        matchingId);
                }
            }
        }
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
        _matchRuntimeRegistry.TryExecute(
            batch.MatchingId,
            () => _gameEventLogManager.LogBotMovementTickPerformance(
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
                batch.MaxConsecutiveBusySkips));
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

    private void DispatchPendingSwarmBotMovement(PendingSwarmBotMovementDispatch pending)
    {
        DispatchWithMatchRuntimeLease(
            pending.RuntimeOperation,
            () =>
            {
                if (pending.PublicationTicket is not { } ticket)
                {
                    if (pending.Plan != null)
                    {
                        throw new InvalidOperationException(
                            "A prepared bot publication is missing its reserved ticket.");
                    }

                    return;
                }

                _swarmBotMovementCoordinator.DispatchInOrder(
                    ticket,
                    () =>
                    {
                        if (pending.Plan != null)
                            DispatchSwarmBotMovementPlan(pending.Plan, pending.SessionSnapshot);
                    });
            });
    }

    internal static void DispatchWithMatchRuntimeLease(IDisposable runtimeOperation, Action dispatch)
    {
        ArgumentNullException.ThrowIfNull(runtimeOperation);
        ArgumentNullException.ThrowIfNull(dispatch);

        ExceptionDispatchInfo? dispatchFailure = null;
        ExceptionDispatchInfo? releaseFailure = null;
        try
        {
            dispatch();
        }
        catch (Exception ex)
        {
            dispatchFailure = ExceptionDispatchInfo.Capture(ex);
        }
        finally
        {
            try
            {
                runtimeOperation.Dispose();
            }
            catch (Exception ex)
            {
                releaseFailure = ExceptionDispatchInfo.Capture(ex);
            }
        }

        if (dispatchFailure != null && releaseFailure != null)
        {
            throw new AggregateException(
                "Bot movement dispatch and deferred match finalization both failed.",
                dispatchFailure.SourceException,
                releaseFailure.SourceException);
        }

        dispatchFailure?.Throw();
        releaseFailure?.Throw();
    }

    private sealed record PendingSwarmBotMovementDispatch(
        IDisposable RuntimeOperation,
        SwarmBotMovementPlan? Plan,
        SwarmBotPublicationTicket? PublicationTicket,
        GameClientSession[] SessionSnapshot);
}
