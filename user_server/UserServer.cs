using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using network.common.data.helpers;
using network.core;
using network.helpers;
using network.interfaces;
using user_server.network;
using user_server.services;

namespace user_server;

public class UserServer(
    INetworkService networkService,
    INatsClientFactory natsClientFactory,
    ILogger<UserServer> logger,
    IConfiguration configuration,
    ICacheHelper cacheHelper,
    IRedLockFactory redLock,
    IServerConfig serverConfig,
    PlayerService playerService)
    : IHostedService
{
    private CancellationTokenSource? _cts;
    private Task? _leaveUserTask;
    private readonly ConcurrentQueue<GameSession> _leaveUserQueue = new();
    private readonly ConcurrentDictionary<long, GameSession> _sessions = new();
    private MatchingManager? _matchingManager;

    public Task StartAsync(CancellationToken ct)
    {
        try
        {
            logger.LogInformation("UserServer starting...");
            InitializeServices();
            StartNetworkService();
            _cts = new CancellationTokenSource();
            _leaveUserTask = LeaveUser(_cts.Token);
            logger.LogInformation("UserServer started successfully");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "UserServer start failed");
            return Task.FromException(ex);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("UserServer stopping...");
        await (_cts?.CancelAsync() ?? Task.CompletedTask);
        if (_leaveUserTask != null) await _leaveUserTask;
        _matchingManager?.Dispose();
        _cts?.Dispose();
        logger.LogInformation("UserServer stopped");
    }

    private void InitializeServices()
    {
        var natsEndpoint = configuration["natsEndPoint"]
            ?? throw new InvalidOperationException("NatsEndpoint is not configured");

        natsClientFactory.Initialize(natsEndpoint);
        // 서버 환경에서 CSV 파일 경로 설정 (bin 디렉토리 기준)
        GameDataHelper.SetBasePath(AppDomain.CurrentDomain.BaseDirectory);
        GameDataHelper.Initialize();
        MapHelper.Initialize(serverConfig.GameServerNum);

        // MatchingManager 초기화
        natsClientFactory.Create();
        _matchingManager = new MatchingManager(logger, cacheHelper, GetSession);

        logger.LogInformation("Services initialized successfully");
    }

    private void StartNetworkService()
    {
        var port = configuration.GetValue<short>("servicePort");
        networkService.SessionCreatedCallback += OnSessionCreated;
        networkService.Listen(IPAddress.Any, port);
        logger.LogInformation($"Listening on port {port}");
    }

    private void OnSessionCreated(UserToken token)
    {
        try
        {
            natsClientFactory.Create();
            var sessionLogger = logger; // Or create a scoped logger

            _ = new GameSession(
                token,
                sessionLogger,
                cacheHelper,
                redLock,
                playerService,
                _matchingManager!,
                RegisterSession);

            logger.LogInformation("New session created");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create GameSession");
        }
    }

    private void RegisterSession(long playerId, GameSession session)
    {
        if (_sessions.TryAdd(playerId, session))
        {
            logger.LogInformation("Session registered: PlayerId={PlayerId}", playerId);
        }
        else
        {
            logger.LogWarning("Session already exists: PlayerId={PlayerId}", playerId);
        }
    }

    private GameSession? GetSession(long playerId)
    {
        _sessions.TryGetValue(playerId, out var session);
        return session;
    }

    // TODO 로그아웃
    // ReSharper disable once UnusedMember.Local
    private void EnqueueUserLeave(GameSession user)
    {
        _leaveUserQueue.Enqueue(user);
    }

    private async Task LeaveUser(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_leaveUserQueue.TryDequeue(out var session))
                {
                    if (session.PlayerId.HasValue)
                    {
                        _sessions.TryRemove(session.PlayerId.Value, out _);
                        logger.LogInformation($"Session removed: PlayerId={session.PlayerId}");
                    }
                }

                await Task.Delay(100, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in LeaveUser task");
            }
        }
    }
}
