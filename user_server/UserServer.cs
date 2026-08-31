using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.helpers;
using network.contracts.authentication;
using network.contracts.scaling;
using network.core;
using network.helpers;
using network.hosting;
using network.infrastructure;
using network.interfaces;
using user_server.network;
using user_server.services;
using user_server.services.scaling;

namespace user_server;

/// <summary>
///     Hosts the UserServer process and orders TCP sessions, matching, replica coordination,
///     messaging, readiness, and shutdown without owning their internal state machines.
/// </summary>
public class UserServer(
    INetworkService networkService,
    INatsClientFactory natsClientFactory,
    ILogger<UserServer> logger,
    IConfiguration configuration,
    ICacheHelper cacheHelper,
    IMatchingQueueClaimStore matchingClaimStore,
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
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private readonly UserSessionRegistry _sessions = new(logger);
    private readonly CancellationTokenSource _clusterHeartbeatCancellation = new();
    private readonly object _shutdownLock = new();
    private IMatchingManager? _matchingManager;
    private MatchingDeliveryRouter? _matchingDeliveryRouter;
    private MatchingMessagingRuntime? _matchingMessagingRuntime;
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

        if (_matchingMessagingRuntime != null)
        {
            await _matchingMessagingRuntime.StopAsync();
        }
        else if (_matchingDeliveryRouter != null)
        {
            // Startup can fail after the router owns its NATS client but before the
            // combined messaging runtime is constructed. Preserve the standalone
            // cleanup path so that partial composition does not leak the connection.
            try
            {
                await _matchingDeliveryRouter.StopAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Matching delivery router shutdown failed");
            }
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
        var matchingManager = new MatchingManager(
            logger,
            cacheHelper,
            matchingClaimStore,
            redLock,
            gameHandoffTicketService,
            _sessions.Get,
            configuration.GetValue("gameHandoff:writeLegacySpawnFields", false),
            scalingContext);
        _matchingManager = matchingManager;

        _matchingMessagingRuntime = new MatchingMessagingRuntime(
            natsClientFactory.Create(),
            _matchingDeliveryRouter,
            coordinationStore,
            clusterOptions,
            processIdentity,
            gameServerRoutingOptions,
            _sessions,
            matchingManager,
            logger);
        _matchingMessagingRuntime.Start();
        matchingManager.Start();

        logger.LogInformation("Services initialized successfully");
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
                GameSession[] sessions = _sessions.Snapshot();
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
                _sessions.Register,
                _sessions.Remove);

            logger.LogInformation("New session created");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GameSession 생성 실패, 연결 종료");
            token.Disconnect();
        }
    }

}
