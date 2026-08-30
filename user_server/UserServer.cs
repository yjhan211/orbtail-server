using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using MessagePack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.helpers;
using network.contracts.authentication;
using network.contracts.messaging;
using network.contracts.scaling;
using network.core;
using network.helpers;
using network.hosting;
using network.infrastructure;
using network.interfaces;
using network.packets;
using user_server.network;
using user_server.services;
using user_server.services.scaling;

namespace user_server;

public class UserServer(
    INetworkService networkService,
    INatsClientFactory natsClientFactory,
    ILogger<UserServer> logger,
    IConfiguration configuration,
    ICacheHelper cacheHelper,
    IRedLockFactory redLock,
    IServerConfig serverConfig,
    IPlayerService playerService,
    IAccountTokenService accountTokenService,
    IGameHandoffTicketService gameHandoffTicketService,
    ServerReadinessState readinessState,
    IUserServerCoordinationStore coordinationStore,
    UserServerClusterOptions clusterOptions,
    UserServerProcessIdentity processIdentity,
    IGameServerRoutingStore gameServerRoutingStore,
    MatchingGameServerRoutingOptions gameServerRoutingOptions,
    MatchingLifecycleOutboxStore matchingLifecycleOutboxStore,
    ILogger<MatchingDeliveryRouter> matchingDeliveryLogger,
    IHostApplicationLifetime applicationLifetime)
    : IHostedService
{
    private enum MatchingLifecycleEvent
    {
        PlayerLeft,
        PlayerCompleted,
        PlayerAdmissionFailed,
        PlayerReleased
    }

    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LifecycleMarkerLifetime = TimeSpan.FromDays(8);
    private static readonly MessagePackSerializerOptions LifecycleSerializerOptions =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);
    private readonly ConcurrentDictionary<long, GameSession> _sessions = new();
    private readonly CancellationTokenSource _clusterHeartbeatCancellation = new();
    private readonly object _shutdownLock = new();
    private IMatchingManager? _matchingManager;
    private INatsClient? _matchingLifecycleNatsClient;
    private MatchingDeliveryRouter? _matchingDeliveryRouter;
    private Task? _nodeHeartbeatTask;
    private Task? _sessionHeartbeatTask;
    private Task? _shutdownTask;
    private int _nodeLeaseAcquired;
    private int _stopping;

    public async Task StartAsync(CancellationToken ct)
    {
        readinessState.MarkNotReady("starting");
        try
        {
            logger.LogInformation("UserServer starting...");
            await InitializeServicesAsync(ct);
            StartNetworkService();
            StartClusterHeartbeat();
            readinessState.MarkReady();
            logger.LogInformation("UserServer started successfully");
        }
        catch (Exception ex)
        {
            readinessState.MarkNotReady("startup_failed");
            logger.LogError(ex, "UserServer start failed");
            await StopCoreAsync();
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_shutdownLock)
        {
            _shutdownTask ??= StopCoreAsync();
            return _shutdownTask;
        }
    }

    private async Task StopCoreAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
            return;

        readinessState.MarkNotReady("stopping");
        logger.LogInformation("UserServer stopping...");
        try
        {
            if (_matchingManager != null)
                await _matchingManager.QuiesceAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Matching manager quiesce failed");
        }

        Task networkShutdown = networkService.StopAsync(CancellationToken.None);
        try
        {
            await networkShutdown.WaitAsync(ShutdownTimeout);
        }
        catch (TimeoutException ex)
        {
            logger.LogWarning(
                ex,
                "UserServer network shutdown exceeded {Timeout}; continuing to wait before disposing dependencies",
                ShutdownTimeout);
            try
            {
                await networkShutdown;
            }
            catch (Exception shutdownException)
            {
                logger.LogWarning(shutdownException, "UserServer network shutdown failed after timeout");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "UserServer network shutdown failed");
        }

        try
        {
            if (_matchingManager != null)
                await _matchingManager.StopAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Matching manager shutdown failed");
        }

        _clusterHeartbeatCancellation.Cancel();
        await AwaitClusterHeartbeatShutdownAsync();

        try
        {
            if (_matchingLifecycleNatsClient != null)
                await _matchingLifecycleNatsClient.CloseAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "NATS close failed during shutdown");
        }

        try
        {
            if (_matchingDeliveryRouter != null)
                await _matchingDeliveryRouter.StopAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Matching delivery router shutdown failed");
        }

        if (Interlocked.Exchange(ref _nodeLeaseAcquired, 0) != 0)
        {
            try
            {
                await coordinationStore.ReleaseNodeLeaseAsync(processIdentity);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "UserServer node lease release failed: NodeId={NodeId}, Generation={Generation}",
                    processIdentity.NodeId,
                    processIdentity.Generation);
            }
        }

        logger.LogInformation("UserServer stopped");
    }

    private async Task InitializeServicesAsync(CancellationToken cancellationToken)
    {
        string natsEndpoint = configuration["natsEndPoint"]
                              ?? throw new InvalidOperationException("NatsEndpoint is not configured");

        natsClientFactory.Initialize(natsEndpoint);
        // 서버 환경에서 CSV 파일 경로 설정 (bin 디렉토리 기준)
        GameDataHelper.SetBasePath(AppDomain.CurrentDomain.BaseDirectory);
        GameDataHelper.Initialize();
        MapHelper.Initialize(serverConfig.GameServerNum);
        cancellationToken.ThrowIfCancellationRequested();

        if (clusterOptions.Enabled)
        {
            bool nodeLeaseAcquired = await coordinationStore.TryAcquireNodeLeaseAsync(
                processIdentity,
                clusterOptions.NodeLeaseLifetime);
            if (!nodeLeaseAcquired)
            {
                throw new InvalidOperationException(
                    $"UserServer node id '{processIdentity.NodeId}' is already owned by another process generation.");
            }

            Volatile.Write(ref _nodeLeaseAcquired, 1);
        }

        _matchingDeliveryRouter = new MatchingDeliveryRouter(
            processIdentity,
            clusterOptions,
            natsClientFactory.Create(),
            matchingDeliveryLogger);
        _matchingDeliveryRouter.Start(HandleMatchingDeliveryRequestAsync);

        MatchingManagerScalingContext? scalingContext =
            clusterOptions.Enabled || gameServerRoutingOptions.Enabled
                ? new MatchingManagerScalingContext(
                    clusterOptions,
                    processIdentity,
                    coordinationStore,
                    _matchingDeliveryRouter,
                    gameServerRoutingStore,
                    gameServerRoutingOptions,
                    matchingLifecycleOutboxStore)
                : null;
        _matchingManager = new MatchingManager(
            logger,
            cacheHelper,
            redLock,
            gameHandoffTicketService,
            GetSession,
            configuration.GetValue("gameHandoff:writeLegacySpawnFields", false),
            scalingContext);

        _matchingLifecycleNatsClient = natsClientFactory.Create();
        InitializeMatchingLifecycleSubscriptions(_matchingLifecycleNatsClient);

        logger.LogInformation("Services initialized successfully");
    }

    private void InitializeMatchingLifecycleSubscriptions(INatsClient client)
    {
        if (!clusterOptions.Enabled && !gameServerRoutingOptions.Enabled)
        {
            client.Subscribe(MatchingLifecycleSubjects.PlayerLeft,
                (_, body) => HandleMatchingLifecycleMessage(body, MatchingLifecycleEvent.PlayerLeft));
            client.Subscribe(MatchingLifecycleSubjects.PlayerCompleted,
                (_, body) => HandleMatchingLifecycleMessage(body, MatchingLifecycleEvent.PlayerCompleted));
            client.Subscribe(MatchingLifecycleSubjects.PlayerAdmissionFailed,
                (_, body) => HandleMatchingLifecycleMessage(body, MatchingLifecycleEvent.PlayerAdmissionFailed));
            client.Subscribe(MatchingLifecycleSubjects.PlayerReleased,
                (_, body) => HandleMatchingLifecycleMessage(body, MatchingLifecycleEvent.PlayerReleased));
            return;
        }

        client.EnsureDurableStream(new NatsDurableStreamOptions
        {
            Name = MatchingLifecycleSubjects.Stream,
            Subjects = [MatchingLifecycleSubjects.AllPlayerEvents],
            Description = "Durable matching lifecycle events consumed by the UserServer cluster"
        });
        client.SubscribeDurableQueue(
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
            HandleDurableMatchingLifecycleMessageAsync);
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

        GameSession? session = GetSession(request.PlayerId);
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

    private void StartClusterHeartbeat()
    {
        if (!clusterOptions.Enabled)
            return;
        if (Volatile.Read(ref _nodeLeaseAcquired) == 0)
            throw new InvalidOperationException("Cannot start cluster heartbeat without an active node lease.");

        _nodeHeartbeatTask = RunNodeHeartbeatAsync(_clusterHeartbeatCancellation.Token);
        _sessionHeartbeatTask = RunSessionOwnerHeartbeatAsync(_clusterHeartbeatCancellation.Token);
    }

    private async Task RunNodeHeartbeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(clusterOptions.HeartbeatInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                bool renewed = await coordinationStore.RenewNodeLeaseAsync(
                    processIdentity,
                    clusterOptions.NodeLeaseLifetime);
                if (!renewed)
                {
                    SignalNodeLeaseLost(null);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during orderly shutdown.
        }
        catch (Exception ex)
        {
            SignalNodeLeaseLost(ex);
        }
    }

    private async Task RunSessionOwnerHeartbeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(clusterOptions.HeartbeatInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                GameSession[] sessions = _sessions.Values.ToArray();
                await Parallel.ForEachAsync(
                    sessions,
                    new ParallelOptions
                    {
                        CancellationToken = cancellationToken,
                        MaxDegreeOfParallelism = 16
                    },
                    async (session, _) =>
                    {
                        UserSessionOwner? owner = session.SessionOwner;
                        if (owner == null)
                            return;

                        try
                        {
                            if (await coordinationStore.RenewSessionOwnerAsync(
                                    owner,
                                    clusterOptions.SessionOwnerLifetime))
                                return;

                            // A replacement owner is visible before the new socket commits auth so
                            // that registration can be fenced. Give a failed replacement one
                            // heartbeat interval to restore this exact owner before evicting it.
                            await Task.Delay(clusterOptions.HeartbeatInterval, cancellationToken);
                            if (session.SessionOwner != owner ||
                                await coordinationStore.RenewSessionOwnerAsync(
                                    owner,
                                    clusterOptions.SessionOwnerLifetime))
                                return;

                            logger.LogWarning(
                                "Distributed session owner was lost after revalidation; disconnecting stale local session: PlayerId={PlayerId}, SessionId={SessionId}",
                                owner.PlayerId,
                                owner.SessionId);
                            session.DisconnectForDuplicateLogin();
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(
                                ex,
                                "Distributed session owner heartbeat failed: PlayerId={PlayerId}, SessionId={SessionId}",
                                owner.PlayerId,
                                owner.SessionId);
                        }
                    });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during orderly shutdown.
        }
    }

    private void SignalNodeLeaseLost(Exception? exception)
    {
        if (Volatile.Read(ref _stopping) != 0)
            return;

        readinessState.MarkNotReady("user_node_lease_lost");
        if (exception == null)
        {
            logger.LogCritical(
                "UserServer node lease was lost: NodeId={NodeId}, Generation={Generation}",
                processIdentity.NodeId,
                processIdentity.Generation);
        }
        else
        {
            logger.LogCritical(
                exception,
                "UserServer node lease heartbeat failed: NodeId={NodeId}, Generation={Generation}",
                processIdentity.NodeId,
                processIdentity.Generation);
        }

        applicationLifetime.StopApplication();
    }

    private async Task AwaitClusterHeartbeatShutdownAsync()
    {
        Task[] tasks = new[] { _nodeHeartbeatTask, _sessionHeartbeatTask }
            .Where(task => task != null)
            .Cast<Task>()
            .ToArray();
        if (tasks.Length == 0)
            return;

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (_clusterHeartbeatCancellation.IsCancellationRequested)
        {
            // Expected during orderly shutdown.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "UserServer cluster heartbeat shutdown failed");
        }
    }

    private async Task<NatsDurableMessageDisposition> HandleDurableMatchingLifecycleMessageAsync(
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
                LifecycleSerializerOptions,
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

            return await RouteMatchingLifecycleToOwnerAsync(
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

    private async Task<NatsDurableMessageDisposition> RouteMatchingLifecycleToOwnerAsync(
        long playerId,
        long matchingId,
        MatchingLifecycleApplyResult applyResult,
        CancellationToken cancellationToken)
    {
        if (!clusterOptions.Enabled)
        {
            GameSession? localSession = GetSession(playerId);
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
            MatchingDeliveryResponse response = await _matchingDeliveryRouter!.DeliverAsync(
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

    private void HandleMatchingLifecycleMessage(byte[] body, MatchingLifecycleEvent lifecycleEvent)
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

        var matchingManager = _matchingManager;
        if (matchingManager == null) return;

        long playerId = BinaryPrimitives.ReadInt64LittleEndian(body);
        long matchingId = body.Length == sizeof(long) * 2
            ? BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(sizeof(long)))
            : 0;
        if (matchingId > 0 && lifecycleEvent != MatchingLifecycleEvent.PlayerAdmissionFailed)
            GetSession(playerId)?.ClearMatchingAssignment(matchingId);
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

    private void StartNetworkService()
    {
        short port = configuration.GetValue<short>("servicePort");
        networkService.SessionCreatedCallback += OnSessionCreated;
        networkService.Listen(IPAddress.Any, port);
        logger.LogInformation($"Listening on port {port}");
    }

    private void OnSessionCreated(UserToken token)
    {
        try
        {

            _ = new GameSession(
                token,
                logger,
                cacheHelper,
                redLock,
                playerService,
                _matchingManager!,
                accountTokenService,
                coordinationStore,
                clusterOptions,
                processIdentity,
                _matchingDeliveryRouter!,
                RegisterSession,
                RemoveSession);

            logger.LogInformation("New session created");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GameSession 생성 실패, 연결 종료");
            token.Disconnect();
        }
    }

    private Action? RegisterSession(long playerId, GameSession session)
    {
        while (true)
        {
            if (!_sessions.TryGetValue(playerId, out var existingSession))
            {
                if (_sessions.TryAdd(playerId, session))
                {
                    logger.LogInformation("Session registered: PlayerId={PlayerId}", playerId);
                    return null;
                }

                continue;
            }

            if (ReferenceEquals(existingSession, session))
                return null;

            if (!_sessions.TryUpdate(playerId, session, existingSession))
                continue;

            logger.LogWarning("Session replaced after duplicate login: PlayerId={PlayerId}", playerId);
            return existingSession.DisconnectForDuplicateLogin;
        }
    }

    private bool RemoveSession(long playerId, GameSession session)
    {
        bool removed = ((ICollection<KeyValuePair<long, GameSession>>)_sessions)
            .Remove(new KeyValuePair<long, GameSession>(playerId, session));
        if (removed)
            logger.LogInformation("Session removed: PlayerId={PlayerId}", playerId);
        return removed;
    }

    private GameSession? GetSession(long playerId)
    {
        _sessions.TryGetValue(playerId, out var session);
        return session;
    }

}
