using game_server.controllers;
using game_server.services;
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
using network.utils;
using network.config;

namespace game_server;
public class GameServer : IHostedService
{
    private readonly IConfiguration _configuration;
    private readonly LogManager _logManager;
    private readonly NatsClientFactory _natsClientFactory;
    private readonly CacheHelper _cacheHelper;
    private readonly Dictionary<Protocol, Func<long, byte[], Task>> _protocolHandlers;
    
    private readonly List<CommonMapController> _commonMapControllerList = [];
    private readonly List<InstanceMapController> _instanceControllerList = [];
    private CancellationTokenSource _cts = new();
    private IAsyncDisposable? _messageProcessor;
    
    private readonly ServerConfig _serverConfig;

    public GameServer(
        IConfiguration configuration,
        LogManager logManager,
        NatsClientFactory natsClientFactory,
        CacheHelper cacheHelper,
        ServerConfig serverConfig)
    {
        _configuration = configuration;
        _logManager = logManager;
        _natsClientFactory = natsClientFactory;
        _cacheHelper = cacheHelper;
        _serverConfig = serverConfig;
        
        _protocolHandlers = new Dictionary<Protocol, Func<long, byte[], Task>>
        {
            { Protocol.U_TO_G_LOGOUT, HandleLogout }
        };
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logManager.WriteInfoLog("Game server starting...");

            InitializeServices();
            InitializeControllers();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            StartMessageProcessing();

            _logManager.WriteInfoLog("Game server started successfully.");
            return Task.CompletedTask;
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

        await _cts.CancelAsync();
        if (_messageProcessor != null) await _messageProcessor.DisposeAsync();

        await Task.WhenAll(_commonMapControllerList.Select(c => c.ShutdownAsync()));
        await Task.WhenAll(_instanceControllerList.Select(c => c.ShutdownAsync()));

        _cts.Dispose();

        _logManager.WriteInfoLog("Game server stopped.");
    }

    private void InitializeServices()
    {
        var natsEndpoint = _configuration.GetRequiredString("natsEndPoint");

        try
        {
            _natsClientFactory.Initialize(natsEndpoint);
            GameDataHelper.Initialize(_logManager);
            MapHelper.Initialize(_serverConfig.GameServerNum);
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
            var controller = new CommonMapController(_logManager, _natsClientFactory.Create(), _cts, _cacheHelper, _serverConfig, mapId);
            controller.Initialize();
            _commonMapControllerList.Add(controller);
        }

        var instanceController = new InstanceMapController(_logManager, _natsClientFactory.Create(), _cts, _cacheHelper, _serverConfig);
        instanceController.Initialize();
        _instanceControllerList.Add(instanceController);
    }

    private void StartMessageProcessing()
    {
        var messageProcessor = new PacketQueueService(_cacheHelper, ProcessMessage, _logManager);
        messageProcessor.StartAsync(_cts.Token);
        _messageProcessor = messageProcessor;
    }

    private async Task ProcessMessage(byte[] message)
    {
        using var packet = new Packet(message);
        var protocolId = (Protocol)packet.PopProtocolId();
        var playerId = packet.PopPlayerId();
        var body = packet.PopBody();

        if (!_protocolHandlers.TryGetValue(protocolId, out var handler))
        {
            throw new NotSupportedException($"Unsupported protocol: {protocolId}");
        }

        await handler(playerId, body);
    }

    private async Task HandleLogout(long playerId, byte[] body)
    {
        var msg = MessagePackSerializer.Deserialize<U_TO_G_LOGOUT>(body);
        
        await using var playerLock = await PlayerInfo.Lock(_cacheHelper.GetRedLockFactory(), playerId);
        var playerInfo = await PlayerInfo.Load(_cacheHelper, msg.PlayerId);
        if (playerInfo == null) return;
        
        // TODO: Implement logout logic
    }
}
