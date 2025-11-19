using game_server.controllers;
using MessagePack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;
using network.helpers;
using network.packets;
using network.config;
using network.interfaces;

namespace game_server;
public class GameServer : IHostedService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<GameServer> _logger;
    private readonly INatsClientFactory _natsClientFactory;
    private readonly ICacheHelper _cacheHelper;
    private readonly Dictionary<Protocol, Func<long, byte[], Task>> _protocolHandlers;

    private readonly List<InstanceMapController> _instanceControllerList = [];
    private CancellationTokenSource _cts = new();
    private INatsClient? _logoutNatsClient;

    private readonly ServerConfig _serverConfig;

    public GameServer(
        IConfiguration configuration,
        ILogger<GameServer> logger,
        INatsClientFactory natsClientFactory,
        ICacheHelper cacheHelper,
        ServerConfig serverConfig)
    {
        _configuration = configuration;
        _logger = logger;
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
            _logger.LogInformation("Game server starting...");

            InitializeServices();
            InitializeControllers();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            SubscribeToLogoutEvents();

            _logger.LogInformation("Game server started successfully.");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Game server starting failed.");
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Game server stopping...");

        await _cts.CancelAsync();
        _logoutNatsClient?.Close();

        await Task.WhenAll(_instanceControllerList.Select(c => c.ShutdownAsync()));

        _cts.Dispose();

        _logger.LogInformation("Game server stopped.");
    }

    private void InitializeServices()
    {
        var natsEndpoint = _configuration.GetRequiredString("natsEndPoint");

        try
        {
            _natsClientFactory.Initialize(natsEndpoint);
            GameDataHelper.Initialize();
            MapHelper.Initialize(_serverConfig.GameServerNum);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to initialize services.", ex);
        }
    }

    private void InitializeControllers()
    {
        var instanceController = new InstanceMapController(_logger, _natsClientFactory.Create(), _cts, _cacheHelper, _serverConfig);
        instanceController.Initialize();
        _instanceControllerList.Add(instanceController);
    }

    /// <summary>
    /// NATS를 통한 로그아웃 이벤트 구독 (Redis Queue 대체)
    /// </summary>
    private void SubscribeToLogoutEvents()
    {
        _logoutNatsClient = _natsClientFactory.Create();
        var logoutSubject = SubjectHelper.GetLogoutSubject(_serverConfig.ServerId);

        _logoutNatsClient.Subscribe(logoutSubject, async (_, message) =>
        {
            try
            {
                await ProcessMessage(message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing logout message");
            }
        });

        _logger.LogInformation("Subscribed to logout events: {Subject}", logoutSubject);
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
