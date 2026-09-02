using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using network.common.data.helpers;
using network.contracts.authentication;
using network.core;
using network.helpers;
using network.hosting;
using network.interfaces;
using user_server.network;
using user_server.services;

namespace user_server;

/// <summary>
///     Hosts the UserServer process and orders TCP sessions, matching, lifecycle messaging,
///     readiness, and shutdown without owning their internal state machines.
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
    ServerReadinessState readinessState)
    : IHostedService
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private readonly UserSessionRegistry _sessions = new(logger);
    private readonly object _shutdownLock = new();
    private IMatchingManager? _matchingManager;
    private MatchingLifecycleSubscriber? _matchingLifecycleSubscriber;
    private Task? _shutdownTask;
    private int _stopping;

    public async Task StartAsync(CancellationToken ct)
    {
        readinessState.MarkNotReady("starting");
        try
        {
            logger.LogInformation("UserServer starting...");
            await InitializeServicesAsync(ct);
            StartNetworkService();
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

    // 종료 순서: matching quiesce → network/session drain → matching stop → NATS stop
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

        if (_matchingLifecycleSubscriber != null)
            await _matchingLifecycleSubscriber.StopAsync();

        logger.LogInformation("UserServer stopped");
    }

    private Task InitializeServicesAsync(CancellationToken cancellationToken)
    {
        string natsEndpoint = configuration["natsEndPoint"]
                              ?? throw new InvalidOperationException("NatsEndpoint is not configured");

        natsClientFactory.Initialize(natsEndpoint);
        // 서버 환경에서 CSV 파일 경로 설정 (bin 디렉토리 기준)
        GameDataHelper.SetBasePath(AppDomain.CurrentDomain.BaseDirectory);
        GameDataHelper.Initialize();
        MapHelper.Initialize(serverConfig.GameServerNum);
        cancellationToken.ThrowIfCancellationRequested();

        var matchingManager = new MatchingManager(
            logger,
            cacheHelper,
            matchingClaimStore,
            redLock,
            gameHandoffTicketService,
            _sessions.Get);
        _matchingManager = matchingManager;

        _matchingLifecycleSubscriber = new MatchingLifecycleSubscriber(
            natsClientFactory.Create(),
            _sessions,
            matchingManager,
            logger);
        _matchingLifecycleSubscriber.Start();
        matchingManager.Start();

        logger.LogInformation("Services initialized successfully");
        return Task.CompletedTask;
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
