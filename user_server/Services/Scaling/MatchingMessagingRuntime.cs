using System.Buffers.Binary;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.contracts.messaging;
using network.contracts.scaling;
using network.infrastructure;
using network.interfaces;
using network.packets;
using user_server.network;

namespace user_server.services.scaling;

/// <summary>
///     Owns UserServer matching messaging: exact-owner request/reply delivery and lifecycle
///     subscriptions. Durable lifecycle effects are committed once in Redis before session delivery.
/// </summary>
internal sealed class MatchingMessagingRuntime(
    INatsClient lifecycleClient,
    IMatchingDeliveryRouter deliveryRouter,
    IUserServerCoordinationStore coordinationStore,
    UserServerClusterOptions clusterOptions,
    UserServerProcessIdentity processIdentity,
    MatchingGameServerRoutingOptions gameServerRoutingOptions,
    UserSessionRegistry sessions,
    IMatchingManager matchingManager,
    ILogger logger)
{
    private enum MatchingLifecycleEvent
    {
        PlayerLeft,
        PlayerCompleted,
        PlayerAdmissionFailed,
        PlayerReleased
    }

    private static readonly TimeSpan LifecycleMarkerLifetime = TimeSpan.FromDays(8);
    private static readonly MessagePackSerializerOptions SerializerOptions =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    public void Start()
    {
        deliveryRouter.Start(HandleMatchingDeliveryRequestAsync);
        InitializeLifecycleSubscriptions();
    }

    public async Task StopAsync()
    {
        try
        {
            await lifecycleClient.CloseAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "NATS close failed during shutdown");
        }

        try
        {
            await deliveryRouter.StopAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Matching delivery router shutdown failed");
        }
    }

    private void InitializeLifecycleSubscriptions()
    {
        if (!clusterOptions.Enabled && !gameServerRoutingOptions.Enabled)
        {
            lifecycleClient.Subscribe(
                MatchingLifecycleSubjects.PlayerLeft,
                (_, body) => HandleLegacyLifecycleMessage(body, MatchingLifecycleEvent.PlayerLeft));
            lifecycleClient.Subscribe(
                MatchingLifecycleSubjects.PlayerCompleted,
                (_, body) => HandleLegacyLifecycleMessage(body, MatchingLifecycleEvent.PlayerCompleted));
            lifecycleClient.Subscribe(
                MatchingLifecycleSubjects.PlayerAdmissionFailed,
                (_, body) => HandleLegacyLifecycleMessage(body, MatchingLifecycleEvent.PlayerAdmissionFailed));
            lifecycleClient.Subscribe(
                MatchingLifecycleSubjects.PlayerReleased,
                (_, body) => HandleLegacyLifecycleMessage(body, MatchingLifecycleEvent.PlayerReleased));
            return;
        }

        lifecycleClient.EnsureDurableStream(new NatsDurableStreamOptions
        {
            Name = MatchingLifecycleSubjects.Stream,
            Subjects = [MatchingLifecycleSubjects.AllPlayerEvents],
            Description = "Durable matching lifecycle events consumed by the UserServer cluster"
        });
        lifecycleClient.SubscribeDurableQueue(
            new NatsDurableConsumerOptions
            {
                StreamName = MatchingLifecycleSubjects.Stream,
                Subject = MatchingLifecycleSubjects.AllPlayerEvents,
                DurableName = MatchingLifecycleSubjects.UserServerDurable,
                QueueGroup = MatchingLifecycleSubjects.UserServerQueue,
                DeliverSubject = MatchingLifecycleSubjects.UserServerDeliverSubject,
                // A lifecycle effect may already be committed in Redis when owner delivery is
                // temporarily unavailable. Keep redelivering for the stream lifetime instead
                // of stranding a live UserServer session after the library default 10 attempts.
                MaxDeliver = int.MaxValue
            },
            HandleDurableLifecycleMessageAsync);
    }

    private async Task<MatchingDeliveryResponse> HandleMatchingDeliveryRequestAsync(
        MatchingDeliveryRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UserSessionOwner? currentOwner = await coordinationStore.GetSessionOwnerAsync(request.PlayerId);
        bool targetsCurrentOwner = currentOwner != null && OwnerMatchesRequest(currentOwner, request);
        if (request.Kind == MatchingDeliveryKind.DisconnectSupersededSession)
        {
            if (targetsCurrentOwner)
            {
                return MatchingDeliveryResponse.Create(
                    request.DeliveryId,
                    MatchingDeliveryStatus.StaleOwner);
            }
        }
        else if (!targetsCurrentOwner)
        {
            return MatchingDeliveryResponse.Create(
                request.DeliveryId,
                MatchingDeliveryStatus.StaleOwner);
        }

        GameSession? session = sessions.Get(request.PlayerId);
        if (session == null)
        {
            return MatchingDeliveryResponse.Create(
                request.DeliveryId,
                MatchingDeliveryStatus.SessionUnavailable);
        }

        return session.HandleMatchingDelivery(request);
    }

    private static bool OwnerMatchesRequest(
        UserSessionOwner owner,
        MatchingDeliveryRequest request)
    {
        return owner.PlayerId == request.PlayerId &&
               string.Equals(owner.NodeId, request.OwnerNodeId, StringComparison.Ordinal) &&
               string.Equals(owner.NodeGeneration, request.OwnerNodeGeneration, StringComparison.Ordinal) &&
               string.Equals(owner.SessionId, request.OwnerSessionId, StringComparison.Ordinal) &&
               owner.SessionGeneration == request.OwnerSessionGeneration;
    }

    private async Task<NatsDurableMessageDisposition> HandleDurableLifecycleMessageAsync(
        NatsDurableMessage message,
        CancellationToken cancellationToken)
    {
        if (!TryGetLifecycleEffect(message.Subject, out MatchingLifecycleEffect requestedEffect))
        {
            logger.LogWarning(
                "Terminating matching lifecycle message with an unknown subject: Subject={Subject}",
                message.Subject);
            return NatsDurableMessageDisposition.Terminate;
        }

        MatchingLifecycleEnvelope? envelope;
        try
        {
            envelope = MessagePackSerializer.Deserialize<MatchingLifecycleEnvelope>(
                message.Data,
                SerializerOptions,
                cancellationToken);
        }
        catch (MessagePackSerializationException ex)
        {
            logger.LogWarning(
                ex,
                "Terminating malformed matching lifecycle envelope: Subject={Subject}, Sequence={Sequence}",
                message.Subject,
                message.StreamSequence);
            return NatsDurableMessageDisposition.Terminate;
        }

        if (envelope == null ||
            !envelope.IsValid ||
            string.IsNullOrWhiteSpace(message.MessageId) ||
            !string.Equals(message.MessageId, envelope.EventId, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Terminating invalid matching lifecycle envelope: Subject={Subject}, Version={Version}, PlayerId={PlayerId}, MatchingId={MatchingId}, MessageId={MessageId}, EventId={EventId}",
                message.Subject,
                envelope?.Version,
                envelope?.PlayerId,
                envelope?.MatchingId,
                message.MessageId,
                envelope?.EventId);
            return NatsDurableMessageDisposition.Terminate;
        }

        try
        {
            DateTimeOffset occurredAt =
                DateTimeOffset.FromUnixTimeMilliseconds(envelope.OccurredAtUnixMilliseconds);
            MatchingLifecycleApplyResult applyResult =
                await coordinationStore.ApplyMatchingLifecycleOnceAsync(
                    envelope.EventId,
                    requestedEffect,
                    envelope.PlayerId,
                    envelope.MatchingId,
                    occurredAt,
                    LifecycleMarkerLifetime);
            if (applyResult.HasConflictingTerminalEvent)
            {
                logger.LogWarning(
                    "Conflicting terminal lifecycle event ignored after first global effect: PlayerId={PlayerId}, MatchingId={MatchingId}, Requested={Requested}, Effective={Effective}, EventId={EventId}",
                    envelope.PlayerId,
                    envelope.MatchingId,
                    requestedEffect,
                    applyResult.EffectiveEffect,
                    envelope.EventId);
            }

            return await RouteLifecycleToOwnerAsync(
                envelope.PlayerId,
                envelope.MatchingId,
                applyResult,
                cancellationToken);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            logger.LogWarning(
                ex,
                "Terminating matching lifecycle envelope with an invalid timestamp: EventId={EventId}",
                envelope.EventId);
            return NatsDurableMessageDisposition.Terminate;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Matching lifecycle processing failed and will be retried: Subject={Subject}, EventId={EventId}, Delivery={Delivery}",
                message.Subject,
                envelope.EventId,
                message.DeliveryAttempt);
            return NatsDurableMessageDisposition.Retry;
        }
    }

    private async Task<NatsDurableMessageDisposition> RouteLifecycleToOwnerAsync(
        long playerId,
        long matchingId,
        MatchingLifecycleApplyResult applyResult,
        CancellationToken cancellationToken)
    {
        if (!clusterOptions.Enabled)
        {
            GameSession? localSession = sessions.Get(playerId);
            if (localSession == null)
                return NatsDurableMessageDisposition.Ack;

            var localOwner = new UserSessionOwner(
                playerId,
                processIdentity.NodeId,
                processIdentity.Generation,
                localSession.SessionId,
                1);
            MatchingDeliveryResponse localResponse = localSession.HandleLocalMatchingDelivery(
                CreateLifecycleDeliveryRequest(localOwner, matchingId, applyResult));
            return localResponse.Status switch
            {
                MatchingDeliveryStatus.Accepted => NatsDurableMessageDisposition.Ack,
                MatchingDeliveryStatus.InvalidRequest => NatsDurableMessageDisposition.Terminate,
                _ => NatsDurableMessageDisposition.Retry
            };
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            UserSessionOwner? owner = await coordinationStore.GetSessionOwnerAsync(playerId);
            if (owner == null)
                return NatsDurableMessageDisposition.Ack;

            MatchingDeliveryRequest request = CreateLifecycleDeliveryRequest(
                owner,
                matchingId,
                applyResult);
            MatchingDeliveryResponse response = await deliveryRouter.DeliverAsync(
                request,
                cancellationToken);
            switch (response.Status)
            {
                case MatchingDeliveryStatus.Accepted:
                    return NatsDurableMessageDisposition.Ack;
                case MatchingDeliveryStatus.StaleOwner:
                    continue;
                case MatchingDeliveryStatus.InvalidRequest:
                    logger.LogError(
                        "Terminating lifecycle notification rejected as invalid: PlayerId={PlayerId}, MatchingId={MatchingId}, Effect={Effect}",
                        playerId,
                        matchingId,
                        applyResult.EffectiveEffect);
                    return NatsDurableMessageDisposition.Terminate;
                default:
                    return NatsDurableMessageDisposition.Retry;
            }
        }

        return NatsDurableMessageDisposition.Retry;
    }

    private static MatchingDeliveryRequest CreateLifecycleDeliveryRequest(
        UserSessionOwner owner,
        long matchingId,
        MatchingLifecycleApplyResult applyResult)
    {
        int protocolId = 0;
        byte[] payload = Array.Empty<byte>();
        MatchingDeliveryKind kind = MatchingDeliveryKind.ClearMatchingAssignment;
        if (applyResult.EffectiveEffect == MatchingLifecycleEffect.PlayerAdmissionFailed)
        {
            kind = MatchingDeliveryKind.MatchingAdmissionFailed;
            protocolId = (int)Protocol.U_TO_C_MATCHING_FAILED;
            using var packet = PacketMaker.U_TO_C_MATCHING_FAILED(
                ErrorCode.MATCHING_FAILED,
                matchingId);
            packet.RecordSize();
            payload = packet.ToBytes();
        }

        return new MatchingDeliveryRequest
        {
            DeliveryId = applyResult.EffectiveEventFingerprint,
            Kind = kind,
            PlayerId = owner.PlayerId,
            MatchingId = matchingId,
            RequestId = applyResult.EffectiveEventFingerprint,
            OwnerNodeId = owner.NodeId,
            OwnerNodeGeneration = owner.NodeGeneration,
            OwnerSessionId = owner.SessionId,
            OwnerSessionGeneration = owner.SessionGeneration,
            ProtocolId = protocolId,
            Payload = payload
        };
    }

    private static bool TryGetLifecycleEffect(
        string subject,
        out MatchingLifecycleEffect effect)
    {
        effect = subject switch
        {
            MatchingLifecycleSubjects.PlayerLeft => MatchingLifecycleEffect.PlayerLeft,
            MatchingLifecycleSubjects.PlayerCompleted => MatchingLifecycleEffect.PlayerCompleted,
            MatchingLifecycleSubjects.PlayerAdmissionFailed => MatchingLifecycleEffect.PlayerAdmissionFailed,
            MatchingLifecycleSubjects.PlayerReleased => MatchingLifecycleEffect.PlayerReleased,
            _ => default
        };
        return effect != default;
    }

    private void HandleLegacyLifecycleMessage(byte[] body, MatchingLifecycleEvent lifecycleEvent)
    {
        if (body.Length != sizeof(long) && body.Length != sizeof(long) * 2)
        {
            logger.LogWarning("Invalid matching lifecycle message length: {Length}", body.Length);
            return;
        }

        if (lifecycleEvent is MatchingLifecycleEvent.PlayerAdmissionFailed or MatchingLifecycleEvent.PlayerReleased &&
            body.Length != sizeof(long) * 2)
        {
            logger.LogWarning(
                "Release-only lifecycle message requires playerId + matchingId payload: Event={Event}, Length={Length}",
                lifecycleEvent,
                body.Length);
            return;
        }

        long playerId = BinaryPrimitives.ReadInt64LittleEndian(body);
        long matchingId = body.Length == sizeof(long) * 2
            ? BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(sizeof(long)))
            : 0;
        if (matchingId > 0 && lifecycleEvent != MatchingLifecycleEvent.PlayerAdmissionFailed)
            sessions.Get(playerId)?.ClearMatchingAssignment(matchingId);
        matchingManager.TryRunBackgroundOperation(
            () => lifecycleEvent switch
            {
                MatchingLifecycleEvent.PlayerCompleted =>
                    matchingManager.RecordGameCompletionAsync(playerId, matchingId),
                MatchingLifecycleEvent.PlayerAdmissionFailed =>
                    matchingManager.AbortMatchingAdmissionAsync(playerId, matchingId),
                MatchingLifecycleEvent.PlayerReleased =>
                    matchingManager.ReleaseMatchingClaimAsync(playerId, matchingId),
                _ => matchingManager.RecordLeaveAsync(playerId, matchingId)
            },
            $"handle {lifecycleEvent} for player {playerId}, matching {matchingId}");
    }
}
