using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using network.common.data.helpers;
using network.core;
using network.helpers;
using network.interfaces;
using user_server.controllers;

namespace user_server;

public class UserServer(
    INetworkService networkService,
    IRedisConnectionPool redisPool,
    INatsClientFactory natsClientFactory,
    ILogger<UserServer> logger,
    IConfiguration configuration,
    ICacheHelper cacheHelper,
    IServerConfig serverConfig)
    : IHostedService
{
    private CancellationTokenSource? _cts;
    private Task? _leaveUserTask;
    private readonly ChatController _chatController = new(cacheHelper);
    private readonly ConcurrentQueue<GameUser> _leaveUserQueue = new();

    public Task StartAsync(CancellationToken ct)
    {
        try
        {
            logger.LogInformation("User Server starting...");
            logger.LogInformation("About to initialize services...");
            InitializeServices();
            logger.LogInformation("Services initialized successfully");
            StartNetworkService();
            _cts = new CancellationTokenSource();
            _leaveUserTask = LeaveUser(_cts.Token);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "User Server starting failed");
            return Task.FromException(ex);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("User server stopping...");
        await _cts?.CancelAsync()!;
        if (_leaveUserTask != null) await _leaveUserTask;
        _cts?.Dispose();
    }

    private void EnqueueUserLeave(GameUser user)
    {
        _leaveUserQueue.Enqueue(user);
    }

    private void InitializeServices()
    {
        logger.LogInformation("Getting natsEndpoint from configuration");
        var natsEndpoint = configuration["natsEndPoint"] ??
                           throw new InvalidOperationException("NatsEndpoint is not configured or is invalid.");
        logger.LogInformation($"natsEndpoint: {natsEndpoint}");
        try
        {
            logger.LogInformation("Initializing natsClientFactory");
            natsClientFactory.Initialize(natsEndpoint);
            logger.LogInformation("natsClientFactory initialized successfully");            
            GameDataHelper.Initialize();
            MapHelper.Initialize(serverConfig.GameServerNum);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to initialize services.", ex);
        }
    }

    private void StartNetworkService()
    {
        var port = configuration.GetValue<short>("servicePort");
        networkService.SessionCreatedCallback += OnSessionCreated;
        networkService.Listen(IPAddress.Any, port);
    }

    private void OnSessionCreated(UserToken token)
    {
        try
        {
            var redLockFactory = redisPool.GetRedLockFactory();
            var natsClient = natsClientFactory.Create();
            _ = new GameUser(token, redLockFactory, natsClient, logger, cacheHelper, EnqueueUserLeave, _chatController);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create nats client");
        }
    }

    private async Task LeaveUser(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
            try
            {
                await ProcessLeaveUser();
                await Task.Delay(10, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "User Server leave failed");
            }
    }

    private async Task ProcessLeaveUser()
    {
        try
        {
            if (_leaveUserQueue.TryDequeue(out var user))
            {
                var token = await user.Release();
                if (token != null) networkService.CloseClientSocket(token);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "User Server leave failed");
        }
    }
}