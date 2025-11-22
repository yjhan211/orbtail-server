using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using network.config;
using network.helpers;
using network.interfaces;
using Xunit;

namespace game_server.tests;

public class GameServerTests
{
    private readonly Mock<IConfiguration> _mockConfiguration;
    private readonly Mock<ILogger<GameServer>> _mockLogger;
    private readonly Mock<INatsClientFactory> _mockNatsFactory;
    private readonly Mock<ICacheHelper> _mockCacheHelper;
    private readonly Mock<INetworkService> _mockNetworkService;
    private readonly Mock<IRedisConnectionPool> _mockRedisPool;
    private readonly ServerConfig _serverConfig;

    public GameServerTests()
    {
        _mockConfiguration = new Mock<IConfiguration>();
        _mockLogger = new Mock<ILogger<GameServer>>();
        _mockNatsFactory = new Mock<INatsClientFactory>();
        _mockCacheHelper = new Mock<ICacheHelper>();
        _mockNetworkService = new Mock<INetworkService>();
        _mockRedisPool = new Mock<IRedisConnectionPool>();

        _serverConfig = new ServerConfig
        {
            ServerType = "GameServer",
            GameServerNum = 2,
            ServerId = 1
        };

        _mockConfiguration.Setup(x => x["natsEndPoint"]).Returns("nats://localhost:4222");
        _mockConfiguration.Setup(x => x["redisEndPoints"]).Returns("localhost:6379");
    }

    [Fact]
    public void ServerConfig_Should_BeValid()
    {
        // Arrange & Act
        Action act = () => _serverConfig.Validate();

        // Assert
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void ServerConfig_Should_ThrowException_When_ServerTypeIsInvalid(string? serverType)
    {
        // Arrange
        var config = new ServerConfig
        {
            ServerType = serverType!,
            GameServerNum = 2,
            ServerId = 1
        };

        // Act
        Action act = () => config.Validate();

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void GameServer_Should_Initialize_Successfully()
    {
        // Arrange
        var gameServer = new GameServer(
            _mockConfiguration.Object,
            _mockLogger.Object,
            _mockNatsFactory.Object,
            _mockCacheHelper.Object,
            _mockNetworkService.Object,
            _mockRedisPool.Object,
            _serverConfig
        );

        // Act & Assert
        gameServer.Should().NotBeNull();
    }
}
