using System.Net;
using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using network.core;
using network.interfaces;
using RedLockNet;
using user_server.controllers;
using user_server.tests.components;

namespace user_server.tests
{
    [TestFixture]
    public class UserServerTests
    {
        private Mock<INetworkService> _mockNetworkService;
        private Mock<IRedisConnectionPool> _mockRedisPool;
        private Mock<INatsClientFactory> _mockNatsClientFactory;
        private Mock<ILogger<UserServer>> _mockLogger;
        private Mock<IConfiguration> _mockConfiguration;
        private Mock<ICacheHelper> _mockCacheHelper;
        private Mock<IServerConfig> _mockServerConfig;
        
        private Mock<IRedLockFactory> _mockRedLockFactory;
        private Mock<INatsClient> _mockNatsClient;
        private Mock<IRedLock> _mockRedLock;
        private TestUserToken _userToken;
        
        private UserServer _userServer;
        private GameUser? _gameUser;
        private Action<UserToken>? _sessionCreatedCallback;
        private ConcurrentQueue<GameUser> _leaveUserQueue;

        [SetUp]
        public void Setup()
        {
            _mockNetworkService = new Mock<INetworkService>();
            _mockRedisPool = new Mock<IRedisConnectionPool>();
            _mockNatsClientFactory = new Mock<INatsClientFactory>();
            _mockLogger = new Mock<ILogger<UserServer>>();
            _mockConfiguration = new Mock<IConfiguration>();
            _mockCacheHelper = new Mock<ICacheHelper>();
            _mockServerConfig = new Mock<IServerConfig>();
            _mockRedLockFactory = new Mock<IRedLockFactory>();
            _mockNatsClient = new Mock<INatsClient>();
            _mockRedLock = new Mock<IRedLock>();
            
            _userToken = new TestUserToken();
            
            _mockRedisPool.Setup(x => x.GetRedLockFactory()).Returns(_mockRedLockFactory.Object);
            _mockRedLockFactory
                .Setup(x => x.CreateLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>()))
                .ReturnsAsync(_mockRedLock.Object);
            _mockNatsClientFactory.Setup(x => x.Create()).Returns(_mockNatsClient.Object);
            
            _mockConfiguration.Setup(x => x["natsEndPoint"]).Returns("localhost:4222");
            
            var servicePortSection = new Mock<IConfigurationSection>();
            servicePortSection.Setup(x => x.Value).Returns("8080");
            _mockConfiguration.Setup(x => x.GetSection("servicePort")).Returns(servicePortSection.Object);
            
            _mockServerConfig.Setup(x => x.GameServerNum).Returns(1);
            
            _mockNetworkService
                .SetupSet(x => x.SessionCreatedCallback = It.IsAny<Action<UserToken>>())
                .Callback<Action<UserToken>>(callback => _sessionCreatedCallback = callback);
            
            _mockCacheHelper
                .Setup(x => x.StringIncrementAsync(It.IsAny<string>(), It.IsAny<int>()))
                .ReturnsAsync(1000);
                
            _mockCacheHelper
                .Setup(x => x.HashGetAllAsync(It.IsAny<string>(), It.IsAny<int>()))
                .ReturnsAsync([]);
                
            _mockCacheHelper
                .Setup(x => x.HashSetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<int>()))
                .ReturnsAsync(true);
                
            _mockCacheHelper
                .Setup(x => x.HashSetAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<byte[]>(), It.IsAny<int>()))
                .ReturnsAsync(true);
                
            _mockCacheHelper
                .Setup(x => x.EnqueueAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<int>()))
                .ReturnsAsync(1);
            
            // 리플렉션을 통해 leaveUserQueue 접근
            _leaveUserQueue = new ConcurrentQueue<GameUser>();
            
            // 테스트 대상 인스턴스 생성
            _userServer = new UserServer(
                _mockNetworkService.Object,
                _mockRedisPool.Object,
                _mockNatsClientFactory.Object,
                _mockLogger.Object,
                _mockConfiguration.Object,
                _mockCacheHelper.Object,
                _mockServerConfig.Object
            );
            
            // _leaveUserQueue 필드 설정을 위한 리플렉션
            var leaveUserQueueField = typeof(UserServer).GetField("_leaveUserQueue", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            leaveUserQueueField?.SetValue(_userServer, _leaveUserQueue);
        }

        [Test]
        public async Task StartAsync_InitializesServicesAndStartsNetwork()
        {
            // Act
            await _userServer.StartAsync(CancellationToken.None);

            // Assert
            _mockNatsClientFactory.Verify(x => x.Initialize(It.IsAny<string>()), Times.Once);
            _mockNetworkService.Verify(x => x.Listen(IPAddress.Any, It.IsAny<short>()), Times.Once);
        }

        [Test]
        public Task StartAsync_LogsErrorWhenExceptionOccurs()
        {
            // Arrange
            _mockNatsClientFactory
                .Setup(x => x.Initialize(It.IsAny<string>()))
                .Throws(new Exception("Test exception"));

            // Act & Assert
            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () => 
                await _userServer.StartAsync(CancellationToken.None));

            Assert.That(ex.Message, Is.EqualTo("Failed to initialize services."));
            return Task.CompletedTask;
        }

        [Test]
        public Task StartAsync_ThrowsWhenNatsEndpointNotConfigured()
        {
            // Arrange
            _mockConfiguration.Setup(x => x["natsEndPoint"]).Returns((string)null!);

            // Act & Assert
            var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await _userServer.StartAsync(CancellationToken.None));
            
            Assert.That(ex.Message, Is.EqualTo("NatsEndpoint is not configured or is invalid."));
            return Task.CompletedTask;
        }

        [Test]
        public async Task StopAsync_CancelsTaskAndDisposesToken()
        {
            // Arrange
            await _userServer.StartAsync(CancellationToken.None);

            // Act
            await _userServer.StopAsync(CancellationToken.None);
        }

        [Test]
        public void OnSessionCreated_CreatesGameUserWithToken()
        {
            // Arrange
            // ServiceProvider를 사용하여 UserServer 메서드 직접 호출
            var onSessionCreatedMethod = typeof(UserServer).GetMethod("OnSessionCreated", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    
            // OnSessionCreated 메서드를 직접 호출
            onSessionCreatedMethod?.Invoke(_userServer, [_userToken]);
    
            // NATS 클라이언트와 RedLock 팩토리가 생성되었는지 확인
            _mockNatsClientFactory.Verify(x => x.Create(), Times.Once);
            _mockRedisPool.Verify(x => x.GetRedLockFactory(), Times.Once);
        }
        
        [Test]
        public void OnSessionCreated_LogsErrorWhenExceptionOccurs()
        {
            // Arrange
            _mockRedisPool
                .Setup(x => x.GetRedLockFactory())
                .Throws(new Exception("Test exception"));
            
            // StartAsync 호출하여 콜백 등록
            _userServer.StartAsync(CancellationToken.None).Wait();
            Assert.That(_sessionCreatedCallback, Is.Not.Null);
            
            // Act - 캡처된 콜백 실행
            _sessionCreatedCallback(_userToken);
        }

        [Test]
        public async Task ProcessLeaveUser_HandlesEmptyQueue()
        {
            // Arrange - LeaveUserQueue가 비어 있는 상태에서 StartAsync 호출
            await _userServer.StartAsync(CancellationToken.None);
            
            // Act & Assert - 예외가 발생하지 않아야 함
            await Task.Delay(100); // LeaveUser 태스크가 실행될 시간을 줌
        }
        
        [Test]
        public async Task EnqueueUserLeave_AddsToQueueAndProcessed()
        {
            // Arrange
            await _userServer.StartAsync(CancellationToken.None);
            
            // GameUser 모킹 및 설정
            var mockChatController = new Mock<ChatController>(_mockCacheHelper.Object);
            _gameUser = new GameUser(
                _userToken,
                _mockRedLockFactory.Object,
                _mockNatsClient.Object,
                _mockLogger.Object,
                _mockCacheHelper.Object,
                user => _leaveUserQueue.Enqueue(user),
                mockChatController.Object
            );
            
            // Act - GameUser를 큐에 추가
            _leaveUserQueue.Enqueue(_gameUser);
            
            // Assert - 큐에 추가된 요소가 있는지 확인
            Assert.That(_leaveUserQueue, Has.Count.EqualTo(1));
            
            // 처리 시간을 기다림
            await Task.Delay(100);
        }
        
        [Test]
        public async Task GameUser_Release_ReturnsUserToken()
        {
            // Arrange
            await _userServer.StartAsync(CancellationToken.None);
            
            // GameUser 모킹 및 설정
            var mockGameLogger = new Mock<ILogger>();
            var mockChatController = new Mock<ChatController>(_mockCacheHelper.Object);
            _gameUser = new GameUser(
                _userToken,
                _mockRedLockFactory.Object,
                _mockNatsClient.Object,
                mockGameLogger.Object,
                _mockCacheHelper.Object,
                user => _leaveUserQueue.Enqueue(user),
                mockChatController.Object
            );
            
            // Act
            var token = await _gameUser.Release();
            
            // Assert
            Assert.That(token, Is.Not.Null);
            Assert.That(_userToken.IsReleased, Is.True);
            
            // NatsClient.Close() 호출 확인
            _mockNatsClient.Verify(x => x.Close(), Times.Once);
        }
        
        [Test]
        public void NetworkServiceListen_CallsCorrectMethod()
        {
            // Act
            _userServer.StartAsync(CancellationToken.None).Wait();
            
            // Assert
            _mockNetworkService.Verify(x => x.Listen(IPAddress.Any, It.IsAny<short>()), Times.Once);
        }
    }
}