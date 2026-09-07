using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using network.common.data.helpers;
using network.core;
using network.hosting;
using network.infrastructure;
using network.infrastructure.redis;
using user_server.accounts;
using user_server.matching;
using user_server.players;
using user_server.sessions;

namespace user_server;

/// <summary>
///     UserServer의 시작과 종료를 관리하고, 새 TCP 연결마다 PlayerSession을 생성한다.
///     필요한 서비스는 Program.cs에서 DI로 전달받는다.
///
///     시작할 때 포트 설정을 검증하고 게임 데이터를 불러온 뒤,
///     서버 간 메시지 구독과 매칭 처리를 시작하고 TCP 접속을 받는다.
///     모든 준비가 끝나면 서버를 준비 완료 상태로 표시한다.
///
///     종료 시작부터 새 세션 생성을 거부하고, 새 매칭 처리를 멈춘 뒤 연결과 세션 정리가 끝나기를 기다린다.
///     이후 추적 중인 작업에 종료를 요청하고 완료를 기다린 뒤, 매칭 리더 등록을 해제하고 NATS 연결을 닫는다.
///     시작 실패와 일반 종료는 같은 정리 절차를 사용한다.
/// </summary>
internal sealed class UserServer(
    NetworkService networkService,
    ILogger<UserServer> logger,
    ILogger<PlayerSession> sessionLogger,
    IConfiguration configuration,
    IRedisOperations redisOperations,
    IPlayerSessionLeaseStore sessionLeaseStore,
    IRedLockFactory redLock,
    IPlayerService playerService,
    AccountTokenService accountTokenService,
    ServerReadinessState readinessState,
    UserServerNodeIdentity node,
    PlayerSessionRegistry sessions,
    NatsPlayerSessionRouter sessionRouter,
    MatchingManager matchingManager,
    BackgroundTaskTracker taskTracker,
    MatchingLifecycleSubscriber matchingLifecycleSubscriber)
    : IHostedService
{
    private readonly object _shutdownLock = new();
    private Task? _shutdownTask;
    private int _stopping;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        readinessState.MarkNotReady("starting");
        try
        {
            logger.LogInformation("UserServer starting...");
            int port = ResolveServicePort(configuration);
            InitializeServices(cancellationToken);
            StartNetworkService(port);
            readinessState.MarkReady();
            logger.LogInformation("UserServer started successfully");
        }
        catch (Exception ex)
        {
            readinessState.MarkNotReady("startup_failed");
            logger.LogError(ex, "UserServer start failed");
            await StopAsync(CancellationToken.None);
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
        Volatile.Write(ref _stopping, 1);
        readinessState.MarkNotReady("stopping");
        logger.LogInformation("UserServer stopping...");
        try
        {
            await matchingManager.StopMatchingLoopAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Matching loop stop failed");
        }

        try
        {
            await networkService.StopAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "UserServer network shutdown failed");
        }

        try
        {
            taskTracker.Shutdown();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Background task cancellation failed");
        }

        // 취소 요청 중 오류가 나더라도 진행 중인 작업은 의존성을 닫기 전에 기다린다.
        await taskTracker.DrainAsync();

        try
        {
            await matchingManager.StopAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Matching manager shutdown failed");
        }

        await matchingLifecycleSubscriber.StopAsync();

        logger.LogInformation("UserServer stopped");
    }

    private void InitializeServices(CancellationToken cancellationToken)
    {
        GameDataHelper.SetBasePath(AppDomain.CurrentDomain.BaseDirectory);
        GameDataHelper.Initialize(
            message => logger.LogDebug("{Message}", message),
            message => logger.LogError("{Message}", message));
        cancellationToken.ThrowIfCancellationRequested();

        sessionRouter.Start();
        matchingLifecycleSubscriber.Start();
        matchingManager.Start();
        logger.LogInformation("User server node identity: NodeId={NodeId}", node.NodeId);

        logger.LogInformation("Services initialized successfully");
    }

    internal static int ResolveServicePort(IConfiguration configuration)
    {
        string? configuredPort = configuration["clientPort"];
        if (configuredPort == null)
        {
            return 9001;
        }
        if (!int.TryParse(configuredPort, out int port) || port is < 1 or > 65535)
        {
            throw new InvalidOperationException("clientPort must be configured as an integer between 1 and 65535.");
        }
        return port;
    }

    private void StartNetworkService(int port)
    {
        networkService.SessionFactory = CreateSession;
        networkService.Listen(IPAddress.Any, port);
        logger.LogInformation("Listening on port {Port}", port);
    }

    private PlayerSession? CreateSession(TcpConnection connection)
    {
        if (Volatile.Read(ref _stopping) != 0)
        {
            connection.Disconnect();
            return null;
        }

        try
        {
            var session = new PlayerSession(
                connection,
                sessionLogger,
                redisOperations,
                redLock,
                playerService,
                matchingManager,
                accountTokenService,
                sessionLeaseStore,
                node.NodeId,
                sessions.Register,
                sessionRouter.AnnounceLogin,
                sessions.Remove,
                taskTracker.TryRun);

            logger.LogInformation("New session created");
            return session;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "PlayerSession 생성 실패, 연결 종료");
            connection.Disconnect();
            return null;
        }
    }
}
