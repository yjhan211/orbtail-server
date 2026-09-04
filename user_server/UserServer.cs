using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using network.common.data.helpers;
using network.core;
using network.core.abstractions;
using network.gamehandoff;
using network.hosting;
using network.infrastructure.messaging;
using network.infrastructure.redis;
using user_server.network;
using user_server.services;

namespace user_server;

/// <summary>
///     Hosts the UserServer process and orders TCP sessions, matching, lifecycle messaging,
///     readiness, and shutdown without owning their internal state machines.
/// </summary>
public class UserServer(
    INetworkService networkService,
    NatsClientFactory natsClientFactory,
    ILogger<UserServer> logger,
    IConfiguration configuration,
    IRedisOperations redisOperations,
    IPlayerSessionOwnershipStore sessionOwnershipStore,
    IMatchingQueueClaimStore matchingClaimStore,
    IRedLockFactory redLock,
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
    private NatsPlayerSessionRouter? _sessionRouter;
    private Task? _shutdownTask;
    private string _nodeId = string.Empty;
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
        // 서버 환경에서 CSV 파일 경로 설정 (bin 디렉토리 기준)
        GameDataHelper.SetBasePath(AppDomain.CurrentDomain.BaseDirectory);
        GameDataHelper.Initialize();
        cancellationToken.ThrowIfCancellationRequested();

        // 라우터·lifecycle 구독은 NATS 연결 하나를 나눠 쓴다. 종료 시 구독자가 닫는다.
        _nodeId = ResolveNodeId();
        var natsClient = natsClientFactory.Create();
        _sessionRouter = new NatsPlayerSessionRouter(natsClient, _sessions.Get, _nodeId, logger);
        var matchingManager = new MatchingManager(
            logger,
            redisOperations,
            matchingClaimStore,
            redLock,
            gameHandoffTicketService,
            _sessionRouter,
            new MatchingLeaderLease(redisOperations, _nodeId, logger));
        _matchingManager = matchingManager;

        _matchingLifecycleSubscriber = new MatchingLifecycleSubscriber(
            natsClient,
            _sessionRouter,
            matchingManager,
            logger);
        // 세션 전달 요청을 받을 수 있게 된 뒤에 리더가 큐를 읽기 시작해야 한다.
        _sessionRouter.Start();
        _matchingLifecycleSubscriber.Start();
        matchingManager.Start();
        logger.LogInformation("User server node identity: NodeId={NodeId}", _nodeId);

        logger.LogInformation("Services initialized successfully");
        return Task.CompletedTask;
    }

    private void StartNetworkService()
    {
        short port = configuration.GetValue<short>("servicePort");
        networkService.SessionFactory = CreateSession;
        networkService.Listen(IPAddress.Any, port);
        logger.LogInformation($"Listening on port {port}");
    }

    /// <summary>UserToken 인증 전이와 함께 실행할 로컬 세션 등록. 외부 I/O는 이 콜백 안에서 하지 않는다.</summary>
    private (bool Accepted, Action? DisconnectSuperseded) RegisterSession(long playerId, GameSession session) =>
        _sessions.Register(playerId, session);

    /// <summary>인증 상태 잠금을 푼 뒤 다른 User Server에 더 높은 로그인 세대를 알린다.</summary>
    private void AnnounceLogin(long playerId, long generation) =>
        _sessionRouter?.AnnounceLogin(playerId, generation);

    /// <summary>
    ///     리더 lease 값이자 세션 알림의 origin. <c>USER_SERVER_ID</c>가 없으면 컨테이너 호스트명 — compose·k8s 모두 유일하다.
    /// </summary>
    private string ResolveNodeId()
    {
        string? configured = configuration["USER_SERVER_ID"];
        return string.IsNullOrWhiteSpace(configured) ? Environment.MachineName : configured.Trim();
    }

    private IPeer? CreateSession(UserToken token)
    {
        try
        {
            var session = new GameSession(
                token,
                logger,
                redisOperations,
                redLock,
                playerService,
                _matchingManager!,
                accountTokenService,
                sessionOwnershipStore,
                _nodeId,
                RegisterSession,
                AnnounceLogin,
                _sessions.Remove);

            logger.LogInformation("New session created");
            return session;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GameSession 생성 실패, 연결 종료");
            token.Disconnect();
            return null;
        }
    }
}
