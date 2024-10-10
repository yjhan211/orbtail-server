using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using MessagePack;
using network.common;
using network.managers;
using network.helpers;
using network.infrastructure;
using network.packets;
using game_server.controllers;

namespace game_server
{
    public class GameServer : IHostedService
    {
        private readonly IConfiguration _configuration;
        private readonly LogManager _logManager;
        private readonly RedisConnectionPool _redisPool;
        private readonly NatsClientFactory _natsClientFactory;
        private readonly List<MapController> _mapControllerList;
        private readonly InstanceController _instanceController;
        private CancellationTokenSource _cts;
        private Timer? _messageTimer;

        public GameServer(IConfiguration configuration, LogManager logManager, RedisConnectionPool redisPool, NatsClientFactory natsClientFactory)
        {
            _configuration = configuration;
            _logManager = logManager;
            _redisPool = redisPool;
            _natsClientFactory = natsClientFactory;
            _cts = new();

            _mapControllerList = new();
            _instanceController = new(_logManager, natsClientFactory.Create(), _cts);
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logManager.WriteInfoLog("Game server starting...");

                InitializeServices();
                await InitializeControllers();

                _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                StartMessageProcessing();

                _logManager.WriteInfoLog("Game server started successfully.");
            }
            catch (Exception ex)
            {
                _logManager.WriteErrorLog(ex);
                throw;
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _logManager.WriteInfoLog("Game server stopping...");

            _cts?.Cancel();
            _messageTimer?.Dispose();

            await Task.WhenAll(_mapControllerList.Select(c => c.ShutdownAsync()));
            await _instanceController.ShutdownAsync();

            _cts?.Dispose();

            _logManager.WriteInfoLog("Game server stopped.");
        }

        private void InitializeServices()
        {
            var redisEndpoints = _configuration["RedisEndpoints"] ?? throw new InvalidOperationException("RedisEndpoints is not configured.");
            var natsEndpoint = _configuration["NatsEndPoint"] ?? throw new InvalidOperationException("NatsEndpoint is not configured or is invalid.");

            try
            {
                _redisPool.Initialize(redisEndpoints);
                _natsClientFactory.Initialize(natsEndpoint);

                MapHelper.Initialize();
                CacheHelper.Initialize(_redisPool);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to initialize services.", ex);
            }
        }

        private async Task InitializeControllers()
        {
            _mapControllerList.Add(new(_logManager, _natsClientFactory.Create(), _cts, MapID.CAMPUS_1));
            _mapControllerList.Add(new(_logManager, _natsClientFactory.Create(), _cts, MapID.FACTORY_1));
            _mapControllerList.Add(new(_logManager, _natsClientFactory.Create(), _cts, MapID.WETLAND_1));

            foreach (var mapController in _mapControllerList)
            {
                await mapController.Initialize();
            }

            _instanceController.Initialize();
        }

        private void StartMessageProcessing()
        {
            _messageTimer = new Timer(
                async _ => await ProcessMessages(),
                null,
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(10)
            );
        }

        private async Task ProcessMessages()
        {
            try
            {
                byte[]? message = await CacheHelper.Instance.DequeueAsync("game_server_queue");
                if (message != null)
                {
                    using var packet = new Packet(message);
                    await ProcessReceiveAsync(packet);
                }
            }
            catch (Exception ex)
            {
                _logManager.WriteErrorLog(ex);
            }
        }


        private async Task ProcessReceiveAsync(Packet packet)
        {
            PROTOCOL protocolId = (PROTOCOL)packet.PopProtocolId();
            long playerId = packet.PopPlayerId();
            byte[] body = packet.PopBody();

            switch (protocolId)
            {
                case PROTOCOL.U_TO_G_LOGOUT:
                    await HandleMessage<U_TO_G_LOGOUT>(playerId, body, Logout);
                    break;

                default:
                    break;
            }
        }

        private static async Task HandleMessage<T>(long player_id, byte[] body, Func<long, T, Task> handleMessage)
        {
            T msg = MessagePackSerializer.Deserialize<T>(body);
            await handleMessage(player_id, msg);
        }

        private async Task Logout(long playerId, U_TO_G_LOGOUT msg)
        {
            var redlock = _redisPool.GetRedLockFactory();
            using var playerLock = PlayerInfo.Lock(redlock, playerId);
            var playerInfo = await PlayerInfo.Load(msg.PlayerId);
            if (playerInfo == null)
            {
                throw new Exception($"can't find player_info. player_id : {playerId}");
            }

            GameObjectInfo objectInfo = playerInfo.ObjectInfo;
            if (objectInfo == null)
            {
                throw new Exception($"can't find object_info. player_id : {playerId}");
            }

            // TODO DB 붙이기 전까지 일단 안지움
            // await PlayerInfoController.Delete(cache_helper, player_id);
        }
    }
}
