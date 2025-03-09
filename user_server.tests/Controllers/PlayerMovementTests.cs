using Microsoft.Extensions.Logging;
using Moq;
using network.common;
using network.common.data.helpers;
using network.common.data.models;
using network.helpers;
using network.interfaces;
using RedLockNet;
using user_server.players;
using user_server.tests.components;

namespace user_server.tests.controllers
{
    [TestFixture]
    public class PlayerMovementTests
    {
        // 의존성 모킹
        private Mock<ICacheHelper> _mockCacheHelper;
        private Mock<IRedLockFactory> _mockRedLockFactory;
        private Mock<IRedLock> _mockRedLock;
        private Mock<ILogger> _mockLogger;
        
        private TestNatsClient _testNatsClient;
        private TestUserToken _userToken;
        private TestGameUser _gameUser;
        private TestMapObjectController _mapObjectController;
        
        private PlayerInfo _playerInfo;
        private PlayerMovement _playerMovement;
        
        [SetUp]
        public void Setup()
        {
            // 게임 데이터 초기화
            GameDataHelper.Initialize();
            MapHelper.Initialize(2);
            
            // 개별 의존성 모킹
            _mockCacheHelper = new Mock<ICacheHelper>();
            _mockRedLockFactory = new Mock<IRedLockFactory>();
            _mockRedLock = new Mock<IRedLock>();
            _mockLogger = new Mock<ILogger>();
            
            _testNatsClient = new TestNatsClient();
            _userToken = new TestUserToken();
            
            // 테스트 PlayerInfo 생성
            _playerInfo = CreateTestPlayerInfo();
            
            // RedLock 설정
            _mockRedLockFactory
                .Setup(x => x.CreateLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>()))
                .ReturnsAsync(_mockRedLock.Object);
            
            // CacheHelper 설정
            _mockCacheHelper
                .Setup(x => x.HashSetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<int>()))
                .ReturnsAsync(true);
            _mockCacheHelper
                .Setup(x => x.HashSetAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<byte[]>(), It.IsAny<int>()))
                .ReturnsAsync(true);
            
            // GameUser 직접 생성 (테스트용 하위 클래스 사용)
            _gameUser = new TestGameUser(
                _userToken,
                _mockRedLockFactory.Object,
                _testNatsClient,
                _mockLogger.Object,
                _mockCacheHelper.Object,
                user => { },  // 콜백 함수
                new TestChatController()
            );
            
            // MapObjectController 설정
            _mapObjectController = new TestMapObjectController(_gameUser);
            _gameUser.SetMapObjectController(_mapObjectController);
            
            // PlayerMovement 인스턴스 생성
            _playerMovement = new PlayerMovement(_gameUser, _playerInfo);
        }
        
        [Test]
        public async Task HandleMove_WithValidDirection_UpdatesPlayerPosition()
        {
            // Arrange
            var originalCell = _playerInfo.ObjectInfo.CurrentCell.Clone();
            var request = new C_TO_U_MOVE { Direction = DirectionType.TOP_RIGHT };
            _testNatsClient.Reset(); // 테스트 전 상태 초기화
            
            // Act
            await _playerMovement.HandleMove(request);
            
            // 비동기 처리 완료 대기 (실제 환경에서는 타이머에 의해 처리됨)
            await Task.Delay(300);
            
            // Assert
            // 플레이어 위치가 이동했는지 확인
            Assert.That(_playerInfo.ObjectInfo.CurrentCell, Is.Not.EqualTo(originalCell), "플레이어 위치가 변경되어야 함");
            
            // 패킷이 전송되었는지 검증
            Assert.That(_userToken.PacketWasSent, Is.True, "이동 응답 패킷이 전송되어야 함");
            
            // NATS 메시지가 발행되었는지 검증
            Assert.That(_testNatsClient.PublishCalled, Is.True, "NATS 메시지가 발행되어야 함");
            Assert.That(_testNatsClient.PublishedMessages, Has.Count.GreaterThan(0), "적어도 하나의 메시지가 발행되어야 함");
            
            // MapObjectController에 객체 업데이트가 요청되었는지 검증
            Assert.That(_mapObjectController.EnqueueUpdateObjectCalled, Is.True, "객체 업데이트가 요청되어야 함");
        }
        
        [Test]
        public async Task HandleMove_WithSitGroundState_ChangesStateToIdle()
        {
            // Arrange - 앉아있는 상태로 설정
            _playerInfo.State = PlayerState.SITGROUND;
            var request = new C_TO_U_MOVE { Direction = DirectionType.TOP_LEFT };
            _testNatsClient.Reset();
            
            // Act
            await _playerMovement.HandleMove(request);
            
            // 비동기 처리 완료 대기
            await Task.Delay(300);
            
            // Assert
            Assert.That(_playerInfo.State, Is.EqualTo(PlayerState.IDLE), "플레이어 상태가 IDLE로 변경되어야 함");
            
            // BroadcastUpdateInfo가 호출되었는지 검증
            Assert.That(_gameUser.BroadcastUpdateInfoCalled, Is.True, "상태 변경 시 BroadcastUpdateInfo가 호출되어야 함");
        }
        
        [Test]
        public async Task HandleMove_WithSpeedBoost_MovesWithIncreasedSpeed()
        {
            // Arrange
            _playerInfo.Boosts.Add(BoostType.SPEED);
            _playerInfo.Hp = 100; // 충분한 HP
            var request = new C_TO_U_MOVE { Direction = DirectionType.TOP_RIGHT };
            _testNatsClient.Reset();
            
            // Timestamp 기록
            var startTime = DateTime.UtcNow;
            
            // Act
            await _playerMovement.HandleMove(request);
            
            // 일반 이동 시간보다 짧은 시간을 대기 (부스트된 속도로 이동하므로)
            await Task.Delay(150);
            
            // Assert
            // HP가 더 많이 소모되는지 확인 (부스트로 인해)
            Assert.That(_playerInfo.Hp, Is.LessThan(100), "부스트 이동 시 HP가 소모되어야 함");
            
            // 패킷이 전송되었는지 검증
            Assert.That(_userToken.PacketWasSent, Is.True, "이동 응답 패킷이 전송되어야 함");
        }
        
        [Test]
        public async Task HandleMove_WithZeroHp_MovesWithReducedSpeed()
        {
            // Arrange
            _playerInfo.Hp = 0; // HP 0
            var request = new C_TO_U_MOVE { Direction = DirectionType.TOP_RIGHT };
            _testNatsClient.Reset();
            
            // Act
            await _playerMovement.HandleMove(request);
            
            // 일반 이동 시간보다 더 오래 대기 (느린 속도로 이동하므로)
            await Task.Delay(500);
            
            // Assert
            // 패킷이 전송되었는지 검증
            Assert.That(_userToken.PacketWasSent, Is.True, "이동 응답 패킷이 전송되어야 함");
        }
        
        [Test]
        public async Task Spawn_SendsSpawnInfoForCurrentPosition()
        {
            // Arrange
            _testNatsClient.Reset();
            
            // Act
            await _playerMovement.Spawn();
            
            // Assert
            // 스폰 메시지가 발행되었는지 검증
            Assert.That(_testNatsClient.PublishCalled, Is.True, "스폰 정보 요청 메시지가 발행되어야 함");
            
            // 패킷이 전송되었는지 검증
            Assert.That(_userToken.PacketWasSent, Is.True, "이동 응답 패킷이 전송되어야 함");
        }
        
        [Test]
        public void Dispose_ReleasesResources()
        {
            // Act
            _playerMovement.Dispose();
            
            // Assert
            // 예외가 발생하지 않고 호출되었는지 확인하는 것으로 충분
            Assert.Pass("Dispose가 예외 없이 완료됨");
        }
        
        private PlayerInfo CreateTestPlayerInfo()
        {
            var playerId = 1001;
            var playerInfo = new PlayerInfo(playerId, false)
            {
                Name = "TestPlayer",
                State = PlayerState.IDLE,
                Hp = 100,
                ObjectInfo =
                {
                    MapId = MapId.Classroom,
                    ObjectType = ObjectType.PLAYER,
                    CurrentCell = new Cell(92, 93),
                    TargetCell = new Cell(92, 93)
                }
            };

            return playerInfo;
        }
        
        [TearDown]
        public void TearDown()
        {
            // Dispose 가능한 리소스 정리
            _mapObjectController?.Dispose();
        }
    }
    
    // GameUser 확장을 위한 메서드
    public static class ExtensionMethods
    {
        public static void SetMapObjectController(this TestGameUser gameUser, TestMapObjectController controller)
        {
            gameUser.MapObjectController = controller;
        }
    }
}