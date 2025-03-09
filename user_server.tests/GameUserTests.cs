using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Moq;
using network.common;
using network.interfaces;
using network.packets;
using network.utils;
using RedLockNet;
using user_server.controllers;
using user_server.tests.components;

namespace user_server.tests;

[TestFixture]
public class GameUserTests
{
    private TestUserToken _userToken;
    private Mock<IRedLockFactory> _mockRedLockFactory;
    private Mock<INatsClient> _mockNatsClient;
    private Mock<ILogger> _mockLogger;
    private Mock<ICacheHelper> _mockCacheHelper;
    private Mock<IRedLock> _mockRedLock;
    private Mock<ChatController> _mockChatController;
    
    private GameUser _gameUser;
    private ConcurrentQueue<GameUser> _leaveUserQueue;
    
    [SetUp]
    public void Setup()
    {
        _userToken = new TestUserToken();
        _mockRedLockFactory = new Mock<IRedLockFactory>();
        _mockNatsClient = new Mock<INatsClient>();
        _mockLogger = new Mock<ILogger>();
        _mockCacheHelper = new Mock<ICacheHelper>();
        _mockRedLock = new Mock<IRedLock>();
        _mockChatController = new Mock<ChatController>(_mockCacheHelper.Object);
        _leaveUserQueue = new ConcurrentQueue<GameUser>();
        
        _mockRedLockFactory
            .Setup(x => x.CreateLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>()))
            .ReturnsAsync(_mockRedLock.Object);
            
        // CacheHelper 메소드 모킹
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
        
        // GameUser 인스턴스 생성
        _gameUser = new GameUser(
            _userToken,
            _mockRedLockFactory.Object,
            _mockNatsClient.Object,
            _mockLogger.Object,
            _mockCacheHelper.Object,
            user => _leaveUserQueue.Enqueue(user),
            _mockChatController.Object
        );
    }
    
    [Test]
    public void Constructor_InitializesCorrectly()
    {
        // 기본 검증
        Assert.That(_gameUser, Is.Not.Null);
        Assert.That(_gameUser.Cts, Is.Not.Null);
        Assert.That(_gameUser.MapObjectController, Is.Not.Null);
        Assert.That(_gameUser.CacheHelper, Is.Not.Null);
        Assert.That(_gameUser.NatsClient, Is.Not.Null);
        Assert.That(_gameUser.RedLock, Is.Not.Null);
        Assert.That(_gameUser.Logger, Is.Not.Null);
        Assert.That(_userToken.GetPeer(), Is.Not.Null);
        
        // 원본 _gameUser 인스턴스와 참조 동일성 비교
        Assert.That(_userToken.GetPeer(), Is.SameAs(_gameUser));
    }
    
    [Test]
    public async Task OnMessageFromClient_HandlesHeartBeat()
    {
        // Arrange
        var packet = Packet.Create((int)Protocol.C_TO_U_HEART_BEAT);
        
        // Const<byte[]> 생성
        var constBuffer = new Const<byte[]>(packet.ToBytes());
        
        // Act
        await _gameUser.OnMessageFromClient(constBuffer);
        
        // Assert
        Assert.That(_userToken.PacketWasSent, Is.True);
    }
    
    [Test]
    public async Task Release_DisposesResourcesCorrectly()
    {
        // Act
        var result = await _gameUser.Release();
        
        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(_userToken.IsReleased, Is.True);
        
        // 리소스가 제대로 해제되었는지 확인
        _mockNatsClient.Verify(x => x.Close(), Times.Once);
    }
    
    [Test]
    public void OnRemoved_EnqueuesUserForLeave()
    {
        // Act
        _gameUser.OnRemoved();
        
        // Assert
        Assert.That(_leaveUserQueue, Has.Count.EqualTo(1));
        Assert.That(_leaveUserQueue.TryPeek(out var user), Is.True);
        Assert.That(user, Is.EqualTo(_gameUser));
    }
    
    [Test]
    public void Send_ForwardsToToken()
    {
        // Arrange
        var packet = Packet.Create((int)Protocol.U_TO_C_HEART_BEAT);
        
        // Act
        _gameUser.Send(packet);
        
        // Assert
        Assert.That(_userToken.PacketWasSent, Is.True);
        Assert.That(_userToken.LastSentPacketId, Is.EqualTo(Protocol.U_TO_C_HEART_BEAT));
    }
}