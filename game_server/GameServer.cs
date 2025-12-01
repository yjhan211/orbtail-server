using System.Collections.Concurrent;
using System.Net;
using game_server.controllers;
using game_server.network;
using game_server.services;
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
    private readonly InteractableStateManager _interactableStateManager = new();
    private CancellationTokenSource _cts = new();
    // MMO 로그아웃 클라이언트 제거됨
    // private INatsClient? _logoutNatsClient;

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
            // Session-based game - no need for logout protocol
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
            // No logout subscription needed for session-based games

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
        // MMO 로그아웃 클라이언트 제거됨
        // _logoutNatsClient?.Close();

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
            _interactableStateManager.Initialize();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to initialize services.", ex);
        }
    }

    private void InitializeControllers()
    {
        var instanceController = new InstanceMapController(_logger, _natsClientFactory.Create(), _cacheHelper, _serverConfig, _clientSessions);
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
            _natsClientFactory.Create();
            _ = new GameClientSession(
                token,
                redLockFactory,
                _logger,
                _cacheHelper,
                OnClientSessionLeave,
                RegisterClientSession,
                GetSessionsByInstance,
                _interactableStateManager);

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
            _logger.LogInformation("Game client session removed: PlayerId={SessionPlayerId}", session.PlayerId.Value);
        }
    }

    private void RegisterClientSession(long playerId, GameClientSession session)
    {
        _clientSessions.TryAdd(playerId, session);
        _logger.LogInformation("Game client session registered: PlayerId={PlayerId}", playerId);
    }

    private List<GameClientSession> GetSessionsByInstance(MapId mapId, long mapSubId)
    {
        return _clientSessions.Values
            .Where(s => s.CurrentMapId == mapId && s.CurrentMapSubId == mapSubId)
            .ToList();
    }

    // MMO 로그아웃 프로토콜 제거됨 - 세션 기반 게임에서는 불필요
    // private void SubscribeToLogoutEvents() { ... }
    // private async Task ProcessMessage(byte[] message) { ... }
    // private async Task HandleLogout(long playerId, byte[] body) { ... }
}
