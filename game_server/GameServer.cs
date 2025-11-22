using System.Collections.Concurrent;
using System.Net;
using game_server.controllers;
using game_server.network;
using MessagePack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.helpers;
using network.common.data.models;
using network.core;
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
    private readonly INetworkService _networkService;
    private readonly IRedisConnectionPool _redisPool;
    private readonly Dictionary<Protocol, Func<long, byte[], Task>> _protocolHandlers;

    private readonly List<InstanceMapController> _instanceControllerList = [];
    private readonly ConcurrentDictionary<long, GameClientSession> _clientSessions = new();
    private CancellationTokenSource _cts = new();
    private INatsClient? _logoutNatsClient;

    private readonly ServerConfig _serverConfig;

    public GameServer(
        IConfiguration configuration,
        ILogger<GameServer> logger,
        INatsClientFactory natsClientFactory,
        ICacheHelper cacheHelper,
        INetworkService networkService,
        IRedisConnectionPool redisPool,
        ServerConfig serverConfig)
    {
        _configuration = configuration;
        _logger = logger;
        _natsClientFactory = natsClientFactory;
        _cacheHelper = cacheHelper;
        _networkService = networkService;
        _redisPool = redisPool;
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
            StartTcpServer();

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
        var instanceController = new InstanceMapController(_logger, _natsClientFactory.Create(), _cts, _cacheHelper, _serverConfig, _clientSessions);
        instanceController.Initialize();
        _instanceControllerList.Add(instanceController);
    }

    private void StartTcpServer()
    {
        var port = _configuration.GetValue<short>("clientPort", 9001);
        _networkService.SessionCreatedCallback += OnClientSessionCreated;
        _networkService.Listen(IPAddress.Any, port);
        _logger.LogInformation($"TCP server listening on port {port}");
    }

    private void OnClientSessionCreated(UserToken token)
    {
        try
        {
            var redLockFactory = _redisPool.GetRedLockFactory();
            var natsClient = _natsClientFactory.Create();
            var session = new GameClientSession(
                token,
                redLockFactory,
                natsClient,
                _logger,
                _cacheHelper,
                OnClientSessionLeave,
                _serverConfig);

            _logger.LogInformation("Game client session created");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create game client session");
        }
    }

    private void OnClientSessionLeave(GameClientSession session)
    {
        if (session.PlayerId.HasValue)
        {
            _clientSessions.TryRemove(session.PlayerId.Value, out _);
            _logger.LogInformation($"Game client session removed: PlayerId={session.PlayerId.Value}");
        }
    }

    public void RegisterClientSession(long playerId, GameClientSession session)
    {
        _clientSessions.TryAdd(playerId, session);
        _logger.LogInformation($"Game client session registered: PlayerId={playerId}");
    }
    
    private void SubscribeToLogoutEvents()
    {
        _logoutNatsClient = _natsClientFactory.Create();
        var logoutSubject = SubjectHelper.GetLogoutSubject(_serverConfig.ServerId);

        _logoutNatsClient.Subscribe(logoutSubject, async void (_, message) =>
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

        _logger.LogInformation($"로그아웃 요청: PlayerId={msg.PlayerId}");

        await using var playerLock = await PlayerInfo.Lock(_cacheHelper.GetRedLockFactory(), playerId);
        var playerInfo = await PlayerInfo.Load(_cacheHelper, msg.PlayerId);
        if (playerInfo == null)
        {
            _logger.LogWarning($"플레이어 {msg.PlayerId} 정보를 찾을 수 없음 (로그아웃)");
            return;
        }

        // 각 InstanceMapController에서 처리하도록 전달
        // 현재는 단일 InstanceMapController만 있으므로 직접 호출하지 않고
        // InstanceMapController가 자체적으로 U_TO_G_LOGOUT을 subscribe하도록 구현됨
        _logger.LogInformation($"플레이어 {msg.PlayerId} 로그아웃 처리 완료");
    }
}
