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
        if (Interlocked.Exchange(ref _botMovementProcessing, 1) == 1)
        {
            Interlocked.Increment(ref _botMovementTickSkips);
            int consecutiveSkips = Interlocked.Increment(ref _botMovementConsecutiveSkips);
            UpdateMaximum(ref _botMovementMaxConsecutiveSkips, consecutiveSkips);
            return;
        }

        var botMovementTickStartedAt = DateTime.UtcNow;
        double snapshotElapsedMilliseconds = 0d;
        double planningElapsedMilliseconds = 0d;
        double walkingElapsedMilliseconds = 0d;
        double broadcastElapsedMilliseconds = 0d;
        try
        {
            GameClientSession[] activeSessions = _sessionRegistry
                .SnapshotWhere(static session => session.PlayerId.HasValue)
                .ToArray();
            IReadOnlyList<long> matchingIds = GetActiveMatchingIds();
            BroadcastMatchStartCountdowns(matchingIds, activeSessions);

            foreach (long matchingId in matchingIds)
            {
                if (!MatchStartGate.IsGameplayActive(matchingId) ||
                    !_botPlayerManager.HasBots(matchingId))
                {
                    continue;
                }

                SwarmBotMovementPlan? plan = null;
                SwarmBotPublicationTicket? publicationTicket = null;
                GameClientSession[]? sessionSnapshot = null;
                IDisposable? runtimeOperation = _matchRuntimeRegistry.TryAcquireOperation(
                    matchingId,
                    () =>
                    {
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
                        publicationTicket = _swarmBotMovementCoordinator.ReservePublication(matchingId);
                    });
                if (runtimeOperation == null)
                    continue;
                if (plan == null || sessionSnapshot == null || publicationTicket == null)
                {
                    SwarmBotPublicationTicket? ticketToRetire = publicationTicket;
                    DispatchWithMatchRuntimeLease(
                        runtimeOperation,
                        () =>
                        {
                            if (ticketToRetire is { } ticket)
                            {
                                _swarmBotMovementCoordinator.DispatchInOrder(
                                    ticket,
                                    static () => { });
                            }
                        });
                    continue;
                }

                SwarmBotMovementPlan capturedPlan = plan;
                SwarmBotPublicationTicket capturedTicket = publicationTicket.Value;
                GameClientSession[] capturedSessions = sessionSnapshot;
                planningElapsedMilliseconds += capturedPlan.PlanningElapsedMilliseconds;
                walkingElapsedMilliseconds += capturedPlan.WalkingElapsedMilliseconds;
                broadcastElapsedMilliseconds += capturedPlan.DispatchPreparationElapsedMilliseconds;
                long dispatchStartedAt = Stopwatch.GetTimestamp();
                DispatchWithMatchRuntimeLease(
                    runtimeOperation,
                    () => _swarmBotMovementCoordinator.DispatchInOrder(
                        capturedTicket,
                        () => DispatchSwarmBotMovementPlan(capturedPlan, capturedSessions)));
                broadcastElapsedMilliseconds +=
                    Stopwatch.GetElapsedTime(dispatchStartedAt).TotalMilliseconds;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "봇 walking 틱 처리 중 오류");
        }
        finally
        {
            try
            {
                double botTickElapsedMs = (DateTime.UtcNow - botMovementTickStartedAt).TotalMilliseconds;
                Interlocked.Exchange(ref _botMovementConsecutiveSkips, 0);
                _botMovementTickSamples.Add(botTickElapsedMs);
                _botMovementSnapshotSamples.Add(snapshotElapsedMilliseconds);
                _botMovementPlanningSamples.Add(planningElapsedMilliseconds);
                _botMovementWalkingSamples.Add(walkingElapsedMilliseconds);
                _botMovementBroadcastSamples.Add(broadcastElapsedMilliseconds);
                _botMovementTickCount++;
                _botMovementTickTotalMs += botTickElapsedMs;
                if (botTickElapsedMs > _botMovementTickMaxMs)
                    _botMovementTickMaxMs = botTickElapsedMs;
                if (_botMovementTickCount >= 200)
                    RecordBotMovementMetrics();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to record bot movement tick metrics");
            }
            finally
            {
                Volatile.Write(ref _botMovementProcessing, 0);
            }
        }
    }

    private void RecordBotMovementMetrics()
    {
        var sortedSamples = _botMovementTickSamples.OrderBy(value => value).ToArray();
        double p50Milliseconds = CalculatePercentile(sortedSamples, 0.50);
        double p95Milliseconds = CalculatePercentile(sortedSamples, 0.95);
        double p99Milliseconds = CalculatePercentile(sortedSamples, 0.99);
        double snapshotP95Milliseconds = CalculatePercentile(
            _botMovementSnapshotSamples.OrderBy(value => value).ToArray(), 0.95);
        double planningP95Milliseconds = CalculatePercentile(
            _botMovementPlanningSamples.OrderBy(value => value).ToArray(), 0.95);
        double walkingP95Milliseconds = CalculatePercentile(
            _botMovementWalkingSamples.OrderBy(value => value).ToArray(), 0.95);
        double broadcastP95Milliseconds = CalculatePercentile(
            _botMovementBroadcastSamples.OrderBy(value => value).ToArray(), 0.95);
        int skippedTicks = Interlocked.Exchange(ref _botMovementTickSkips, 0);
        int maxConsecutiveSkippedTicks = Interlocked.Exchange(ref _botMovementMaxConsecutiveSkips, 0);
        logger.LogInformation(
            "Bot movement tick: avg={Avg:F1}ms max={Max:F1}ms skips={Skips} over {Count} ticks; " +
            "p95 snapshot={SnapshotP95:F1}ms planning={PlanningP95:F1}ms walking={WalkingP95:F1}ms " +
            "broadcast={BroadcastP95:F1}ms",
            _botMovementTickTotalMs / _botMovementTickCount,
            _botMovementTickMaxMs,
            skippedTicks,
            _botMovementTickCount,
            snapshotP95Milliseconds,
            planningP95Milliseconds,
            walkingP95Milliseconds,
            broadcastP95Milliseconds);
        foreach (long matchingId in GetActiveMatchingIds().Where(_botPlayerManager.HasBots))
        {
            _matchRuntimeRegistry.TryExecute(
                matchingId,
                () => _gameEventLogManager.LogBotMovementTickPerformance(
                    matchingId,
                    p50Milliseconds,
                    p95Milliseconds,
                    p99Milliseconds,
                    snapshotP95Milliseconds,
                    planningP95Milliseconds,
                    walkingP95Milliseconds,
                    broadcastP95Milliseconds,
                    _botMovementTickCount,
                    skippedTicks,
                    maxConsecutiveSkippedTicks));
        }

        _botMovementTickCount = 0;
        _botMovementTickTotalMs = 0;
        _botMovementTickMaxMs = 0;
        _botMovementTickSamples.Clear();
        _botMovementSnapshotSamples.Clear();
        _botMovementPlanningSamples.Clear();
        _botMovementWalkingSamples.Clear();
        _botMovementBroadcastSamples.Clear();
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
