using System.Net;
using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using network.core;
using network.managers;
using network.helpers;
using network.infrastructure;

namespace user_server
{
    public class UserServer(NetworkService networkService, RedisConnectionPool redisPool,
    NatsClientFactory natsClientFactory, LogManager logManager, IConfiguration configuration) : IHostedService
    {
        private readonly NetworkService _networkService = networkService;
        private readonly RedisConnectionPool _redisPool = redisPool;
        private readonly NatsClientFactory _natsClientFactory = natsClientFactory;
        private readonly IConfiguration _configuration = configuration;
        private readonly LogManager _logManager = logManager;
        private readonly ConcurrentQueue<GameUser> _leaveUserQueue = new();
        private CancellationTokenSource? _cts;
        private Task? _leaveUserTask;

        public Task StartAsync(CancellationToken ct)
        {
            try
            {
                _logManager.WriteInfoLog("User Server starting...");

                InitializeServices();
                StartNetworkService();

                _cts = new();
                _leaveUserTask = LeaveUser(_cts.Token);

                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logManager.WriteErrorLog(ex);
                return Task.FromException(ex);
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _logManager.WriteInfoLog("User server stopping...");

            _cts?.Cancel();

            if (_leaveUserTask != null)
            {
                await _leaveUserTask;
            }

            _cts?.Dispose();
        }

        public void EnqueueUserLeave(GameUser user)
        {
            _leaveUserQueue.Enqueue(user);
        }

        private void InitializeServices()
        {
            var redisEndpoints = _configuration["redisEndpoints"] ?? throw new InvalidOperationException("RedisEndpoints is not configured.");
            var natsEndpoint = _configuration["natsEndPoint"] ?? throw new InvalidOperationException("NatsEndpoint is not configured or is invalid.");

            try
            {
                _redisPool.Initialize(redisEndpoints);
                _natsClientFactory.Initialize(natsEndpoint);

                CommonMapHelper.Initialize(Program.GameServerNum);
                InstanceMapHelper.Initialize(Program.GameServerNum);
                CacheHelper.Initialize(_redisPool);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to initialize services.", ex);
            }
        }

        private void StartNetworkService()
        {
            var port = _configuration.GetValue<short>("servicePort");
            _networkService.SessionCreatedCallback += OnSessionCreated;
            _networkService.Listen(IPAddress.Any, port);
        }

        private void OnSessionCreated(UserToken token)
        {
            try
            {
                var redLockFactory = _redisPool.GetRedLockFactory();
                var natsClient = _natsClientFactory.Create();
                var user = new GameUser(token, redLockFactory, natsClient, _logManager, EnqueueUserLeave);
            }
            catch (Exception ex)
            {
                _logManager.WriteErrorLog(ex);
            }
        }

        private async Task LeaveUser(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
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
                    _logManager.WriteErrorLog(ex);
                }
            }
        }

        private async Task ProcessLeaveUser()
        {
            try
            {
                if (_leaveUserQueue.TryDequeue(out GameUser? user))
                {
                    var token = await user.Release();
                    if (token != null)
                    {
                        _networkService.CloseClientSocket(token);
                    }
                }
            }
            catch (Exception ex)
            {
                _logManager.WriteErrorLog(ex);
            }
        }
    }
}
