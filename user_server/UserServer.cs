using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using network.common.data.helpers;
using network.config;
using network.core;
using network.helpers;
using network.infrastructure;
using network.managers;
using network.utils;
using user_server.controllers;

namespace user_server;

public class UserServer(
    NetworkService networkService,
    RedisConnectionPool redisPool,
    NatsClientFactory natsClientFactory,
    LogManager logManager,
    IConfiguration configuration,
    CacheHelper cacheHelper,
    ServerConfig serverConfig)
    : IHostedService
{
    private CancellationTokenSource? _cts;
    private ChatController _chatController = new(cacheHelper);
    private readonly ConcurrentQueue<GameUser> _leaveUserQueue = new();
    private Task? _leaveUserTask;

   public Task StartAsync(CancellationToken ct)
   {
       try
       {
           logManager.WriteInfoLog("User Server starting...");
           InitializeServices();
           StartNetworkService();
           _cts = new CancellationTokenSource();
           _leaveUserTask = LeaveUser(_cts.Token);
           return Task.CompletedTask;
       }
       catch (Exception ex)
       {
           logManager.WriteErrorLog(ex);
           return Task.FromException(ex);
       }
   }

   public async Task StopAsync(CancellationToken cancellationToken)
   {
       logManager.WriteInfoLog("User server stopping...");
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
       var natsEndpoint = configuration["natsEndPoint"] ??
                          throw new InvalidOperationException("NatsEndpoint is not configured or is invalid.");
       try
       {
           natsClientFactory.Initialize(natsEndpoint);
           GameDataHelper.Initialize(logManager);
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
           _ = new GameUser(token, redLockFactory, natsClient, logManager, cacheHelper, EnqueueUserLeave, _chatController);
       }
       catch (Exception ex)
       {
           logManager.WriteErrorLog(ex);
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
               logManager.WriteErrorLog(ex);
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
           logManager.WriteErrorLog(ex);
       }
   }
}