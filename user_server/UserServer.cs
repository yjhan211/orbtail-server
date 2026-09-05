using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using network.common.data.helpers;
using network.core;
using network.hosting;
using network.infrastructure.redis;
using user_server.network;
using user_server.services;

namespace user_server;

/// <summary>
///     UserServer 프로세스의 수명 조정자. 부품은 Program.cs(DI)에서 받고, 여기서는 시작·종료 순서만 정한다:
///     라우터 → lifecycle 구독 → 매칭 → 리슨, 종료는 매칭 quiesce → 네트워크 배수 → 매칭 stop → NATS 닫기.
/// </summary>
internal sealed class UserServer(
    NetworkService networkService,
    ILogger<UserServer> logger,
    IConfiguration configuration,
    IRedisOperations redisOperations,
    IPlayerSessionOwnershipStore sessionOwnershipStore,
    IRedLockFactory redLock,
    IPlayerService playerService,
    IAccountTokenService accountTokenService,
    ServerReadinessState readinessState,
    UserServerNodeIdentity node,
    UserSessionRegistry sessions,
    NatsPlayerSessionRouter sessionRouter,
    MatchingManager matchingManager,
    MatchingLifecycleSubscriber matchingLifecycleSubscriber)
    : IHostedService
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private readonly object _shutdownLock = new();
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
            await matchingManager.QuiesceAsync();
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
            await matchingManager.StopAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Matching manager shutdown failed");
        }

        await matchingLifecycleSubscriber.StopAsync();

        logger.LogInformation("UserServer stopped");
    }

    private Task InitializeServicesAsync(CancellationToken cancellationToken)
    {
        // 서버 환경에서 CSV 파일 경로 설정 (bin 디렉토리 기준)
        GameDataHelper.SetBasePath(AppDomain.CurrentDomain.BaseDirectory);
        GameDataHelper.Initialize(
            message => logger.LogDebug("{Message}", message),
            message => logger.LogError("{Message}", message));
        cancellationToken.ThrowIfCancellationRequested();

        // 세션 전달 요청을 받을 수 있게 된 뒤에 리더가 큐를 읽기 시작해야 한다.
        sessionRouter.Start();
        matchingLifecycleSubscriber.Start();
        matchingManager.Start();
        logger.LogInformation("User server node identity: NodeId={NodeId}", node.NodeId);

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

    /// <summary>TcpConnection 인증 전이와 함께 실행할 로컬 세션 등록. 외부 I/O는 이 콜백 안에서 하지 않는다.</summary>
    private (bool Accepted, Action? DisconnectSuperseded) RegisterSession(long playerId, GameSession session) =>
        sessions.Register(playerId, session);

    /// <summary>인증 상태 잠금을 푼 뒤 다른 User Server에 더 높은 로그인 세대를 알린다.</summary>
    private void AnnounceLogin(long playerId, long generation) =>
        sessionRouter.AnnounceLogin(playerId, generation);

    private IConnectionSession? CreateSession(TcpConnection connection)
    {
        try
        {
            var session = new GameSession(
                connection,
                logger,
                redisOperations,
                redLock,
                playerService,
                matchingManager,
                accountTokenService,
                sessionOwnershipStore,
                node.NodeId,
                RegisterSession,
                AnnounceLogin,
                sessions.Remove);

            logger.LogInformation("New session created");
            return session;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GameSession 생성 실패, 연결 종료");
            connection.Disconnect();
            return null;
        }
    }
}
