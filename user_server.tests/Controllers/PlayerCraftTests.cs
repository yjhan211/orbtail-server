using Microsoft.Extensions.Logging;
using Moq;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;
using network.helpers;
using network.interfaces;
using RedLockNet;
using user_server.players;
using user_server.progress;
using user_server.tests.components;

namespace user_server.tests.controllers
{
    [TestFixture]
    public class PlayerCraftTests
    {
        // 의존성 모킹
        private ICacheHelper _mockCacheHelper;
        private IRedLockFactory _mockRedLockFactory;
        private INatsClient _mockNatsClient;
        private ILogger _mockLogger;
        
        private TestUserToken _userToken;
        private TestGameUser _gameUser;
        
        private TestPlayerQuest _testPlayerQuest;
        private TestPlayerInventory _testPlayerInventory;
        private TestPlayerProgress _testPlayerProgress;
        
        private PlayerInfo _playerInfo;
        private PlayerCraft _playerCraft;
        
        [SetUp]
        public void Setup()
        {
            // 게임 데이터 초기화
            GameDataHelper.Initialize();
            MapHelper.Initialize(2);
            
            // 개별 의존성 모킹
            var mockCacheHelper = new Mock<ICacheHelper>();
            var mockRedLockFactory = new Mock<IRedLockFactory>();
            var mockRedLock = new Mock<IRedLock>();
            var mockNatsClient = new Mock<INatsClient>();
            var mockLogger = new Mock<ILogger>();
            
            // 모킹된 객체 할당
            _mockCacheHelper = mockCacheHelper.Object;
            _mockRedLockFactory = mockRedLockFactory.Object;
            _mockNatsClient = mockNatsClient.Object;
            _mockLogger = mockLogger.Object;
            _userToken = new TestUserToken();
            
            // RedLock 설정
            mockRedLockFactory
                .Setup(x => x.CreateLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>()))
                .ReturnsAsync(mockRedLock.Object);
            
            // CacheHelper 설정
            mockCacheHelper
                .Setup(x => x.HashSetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<int>()))
                .ReturnsAsync(true);
            mockCacheHelper
                .Setup(x => x.HashSetAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<byte[]>(), It.IsAny<int>()))
                .ReturnsAsync(true);
            mockCacheHelper
                .Setup(x => x.StringIncrementAsync(It.IsAny<string>(), It.IsAny<int>()))
                .ReturnsAsync(3); // 다음 아이템 UID
            
            // 테스트 PlayerInfo 생성
            _playerInfo = CreateTestPlayerInfo();
            
            // GameUser 직접 생성 (테스트용 하위 클래스 사용)
            _gameUser = new TestGameUser(
                _userToken,
                _mockRedLockFactory,
                _mockNatsClient,
                _mockLogger,
                _mockCacheHelper,
                user => { },  // 콜백 함수
                new TestChatController()
            );
            
            // 테스트용 클래스 생성
            _testPlayerProgress = new TestPlayerProgress(_gameUser);
            _testPlayerQuest = new TestPlayerQuest(_gameUser, _playerInfo);
            _testPlayerInventory = new TestPlayerInventory(_gameUser, _playerInfo, _testPlayerQuest);
            
            // PlayerCraft 인스턴스 생성
            _playerCraft = new PlayerCraft(
                _gameUser,
                _playerInfo,
                _testPlayerQuest,
                _testPlayerInventory,
                _testPlayerProgress
            );
            
            // 테스트 데이터 설정
            _playerInfo.Stamina = 100; // 충분한 스태미나
            _playerInfo.CraftInfo.Manuals.Add(1); // 기본 매뉴얼 추가
        }
        
        [Test]
        public async Task Craft_WithValidCraftId_AddsProgressItemAndUpdatesPlayerState()
        {
            // Arrange
            var craftId = 1; // 유효한 제작 ID
            var request = new C_TO_U_CRAFT { CraftId = craftId };
            
            // 유효한 제작 매뉴얼을 가지도록 설정
            _playerInfo.CraftInfo.Manuals.Add(GameCraftData.Get(craftId).ManualId);
            _playerInfo.Stamina = 100; // 충분한 스태미나
            
            // Act
            await _playerCraft.Craft(request);
            
            // Assert
            Assert.That(_playerInfo.State, Is.EqualTo(PlayerState.CRAFT_1), "플레이어 상태가 CRAFT_1으로 변경되어야 함");
            Assert.That(_playerInfo.Stamina, Is.LessThan(100), "스태미나가 소모되어야 함");
            
            // PlayerProgress.AddProgressItem이 호출되었는지 검증
            Assert.That(_testPlayerProgress.AddProgressItemCalled, Is.True, "AddProgressItem 메서드가 호출되어야 함");
            Assert.That(_testPlayerProgress.LastProgressItem, Is.Not.Null, "Progress 아이템이 전달되어야 함");
            Assert.That(((CraftProgressInfo)_testPlayerProgress.LastProgressItem).CraftId, Is.EqualTo(craftId), "올바른 CraftID가 전달되어야 함");
            
            // 응답 패킷이 전송되었는지 검증
            Assert.That(_userToken.PacketWasSent, Is.True, "응답 패킷이 전송되어야 함");
            
            // BroadcastUpdateInfo가 호출되었는지 검증
            Assert.That(_gameUser.BroadcastUpdateInfoCalled, Is.True, "BroadcastUpdateInfo should have been called");
        }
        
        [Test]
        public async Task Craft_WithInsufficientStamina_ReturnsFatalError()
        {
            // Arrange
            var craftId = 1;
            var request = new C_TO_U_CRAFT { CraftId = craftId };
            
            // 스태미나 부족 상태로 설정
            _playerInfo.CraftInfo.Manuals.Add(GameCraftData.Get(craftId).ManualId);
            _playerInfo.Stamina = 0;
            
            // Act
            await _playerCraft.Craft(request);
            
            // Assert
            Assert.That(_userToken.PacketWasSent, Is.True, "에러 패킷이 전송되어야 함");
            
            // 상태가 변경되지 않았는지 확인
            Assert.That(_playerInfo.State, Is.Not.EqualTo(PlayerState.CRAFT_1), "플레이어 상태가 변경되지 않아야 함");
            
            // PlayerProgress.AddProgressItem이 호출되지 않았는지 검증
            Assert.That(_testPlayerProgress.AddProgressItemCalled, Is.False, "AddProgressItem 메서드가 호출되지 않아야 함");
        }
        
        [Test]
        public async Task Craft_WithoutManual_ReturnsFatalError()
        {
            // Arrange
            var craftId = 1;
            var request = new C_TO_U_CRAFT { CraftId = craftId };

            // 매뉴얼이 없는 상태로 설정
            _playerInfo.CraftInfo.Manuals.Clear();
            _playerInfo.Stamina = 100;
            
            // Act
            await _playerCraft.Craft(request);
            
            // Assert
            Assert.That(_userToken.PacketWasSent, Is.True, "에러 패킷이 전송되어야 함");
            
            // 상태가 변경되지 않았는지 확인
            Assert.That(_playerInfo.State, Is.Not.EqualTo(PlayerState.CRAFT_1), "플레이어 상태가 변경되지 않아야 함");
            
            // PlayerProgress.AddProgressItem이 호출되지 않았는지 검증
            Assert.That(_testPlayerProgress.AddProgressItemCalled, Is.False, "AddProgressItem 메서드가 호출되지 않아야 함");
        }
        
        private PlayerInfo CreateTestPlayerInfo()
        {
            var playerId = 1001;
            var playerInfo = new PlayerInfo(playerId, false)
            {
                Name = "TestPlayer",
                State = PlayerState.IDLE,
                Stamina = 100,
                ObjectInfo =
                {
                    MapId = MapId.Library,
                    ObjectType = ObjectType.PLAYER,
                    CurrentCell = new Cell(10, 10)
                }
            };

            return playerInfo;
        }
        
        [TearDown]
        public void TearDown()
        {
            // Dispose 가능한 리소스 정리
            _testPlayerProgress?.Dispose();
        }
    }
}