using game_server.controllers;
using MessagePack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;
using network.helpers;
using network.infrastructure;
using network.managers;
using network.packets;

namespace game_server;

public class GameServer(
    IConfiguration configuration,
    LogManager logManager,
    RedisConnectionPool redisPool,
    NatsClientFactory natsClientFactory) : IHostedService
{
    private readonly List<CommonMapController> _commonMapControllerList = [];
    private readonly List<InstanceMapController> _instanceControllerList = [];
    private CancellationTokenSource _cts = new();
    private Timer? _messageTimer;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            logManager.WriteInfoLog("Game server starting...");

            InitializeServices();
            InitializeControllers();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            StartMessageProcessing();

            logManager.WriteInfoLog("Game server started successfully.");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            logManager.WriteErrorLog(ex);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logManager.WriteInfoLog("Game server stopping...");

        await _cts.CancelAsync();
        if (_messageTimer != null) await _messageTimer.DisposeAsync();

        await Task.WhenAll(_commonMapControllerList.Select(c => c.ShutdownAsync()));
        await Task.WhenAll(_instanceControllerList.Select(c => c.ShutdownAsync()));

        _cts.Dispose();

        logManager.WriteInfoLog("Game server stopped.");
    }

    private void InitializeServices()
    {
        var redisEndpoints = configuration["redisEndpoints"] ??
                             throw new InvalidOperationException("RedisEndpoints is not configured.");
        var natsEndpoint = configuration["natsEndPoint"] ??
                           throw new InvalidOperationException("NatsEndpoint is not configured or is invalid.");

        try
        {
            redisPool.Initialize(redisEndpoints);
            natsClientFactory.Initialize(natsEndpoint);

            GameDataHelper.Initialize(logManager);
            MapHelper.Initialize(Program.GameServerNum);
            CacheHelper.Initialize(redisPool);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to initialize services.", ex);
        }
    }

    private void InitializeControllers()
    {
        foreach (var mapId in GameMapData.GetCommonMapList())
        {
            _commonMapControllerList.Add(new CommonMapController(logManager, natsClientFactory.Create(), _cts, mapId));
        }
        
        foreach (var mapController in _commonMapControllerList)
        {
            mapController.Initialize();
        }

        _instanceControllerList.Add(new InstanceMapController(logManager, natsClientFactory.Create(), _cts));
        foreach (var instanceController in _instanceControllerList)
        {
            instanceController.Initialize();
        }
    }

    private void StartMessageProcessing()
    {
        _messageTimer = new Timer(
            async void (_) =>
            {
                try
                {
                    await ProcessMessages();
                }
                catch (Exception e)
                {
                    logManager.WriteErrorLog(e);
                }
            },
            null,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(10)
        );
    }

    private async Task ProcessMessages()
    {
        try
        {
            var message = await CacheHelper.Instance.DequeueAsync("game_server_queue");
            if (message != null)
            {
                using var packet = new Packet(message);
                await ProcessReceiveAsync(packet);
            }
        }
        catch (Exception ex)
        {
            logManager.WriteErrorLog(ex);
        }
    }


    private async Task ProcessReceiveAsync(Packet packet)
    {
        var protocolId = (Protocol)packet.PopProtocolId();
        var playerId = packet.PopPlayerId();
        var body = packet.PopBody();

        switch (protocolId)
        {
            case Protocol.U_TO_G_LOGOUT:
                await HandleMessage<U_TO_G_LOGOUT>(playerId, body, Logout);
                break;
            
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private static async Task HandleMessage<T>(long playerId, byte[] body, Func<long, T, Task> handleMessage)
    {
        var msg = MessagePackSerializer.Deserialize<T>(body);
        await handleMessage(playerId, msg);
    }

    private async Task Logout(long playerId, U_TO_G_LOGOUT msg)
    {
        var redLock = redisPool.GetRedLockFactory();
        await using var playerLock = await PlayerInfo.Lock(redLock, playerId);
        var playerInfo = await PlayerInfo.Load(msg.PlayerId);

        // TODO DB 붙이기 전까지 일단 안지움
        // ReSharper disable once RedundantJumpStatement
        if (playerInfo == null) return;

        // var objectInfo = playerInfo.ObjectInfo;
        // await PlayerInfoController.Delete(cache_helper, player_id);
    }
}