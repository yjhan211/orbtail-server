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
    public class PlayerCampTests
    {
        // 의존성 모킹
        private Mock<ICacheHelper> _mockCacheHelper;
        private Mock<IRedLockFactory> _mockRedLockFactory;
        private Mock<IRedLock> _mockRedLock;
        private Mock<ILogger> _mockLogger;
        
        private TestNatsClient _testNatsClient;
        private TestUserToken _userToken;
        private TestGameUser _gameUser;
        
        private PlayerInfo _playerInfo;
        private PlayerCamp _playerCamp;
        
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
            
            // 추적 상태 초기화
            _gameUser.ResetTrackers();
            
            // PlayerCamp 인스턴스 생성
            _playerCamp = new PlayerCamp(_gameUser, _playerInfo);
            
            // 테스트를 위한 아이템 추가
            var testItem = new ItemInfo { ItemUid = 1001, ItemId = 401000001, Count = 1 };
            _playerInfo.InventoryInfo.AddItem(testItem);
        }
        
        [Test]
        public async Task Encamp_WithValidItem_UpdatesCampInfoAndBroadcasts()
        {
            // Arrange
            var request = new C_TO_U_ENCAMP { ItemUid = 1001 };
            
            // Act
            await _playerCamp.Encamp(request);
            
            // Assert
            Assert.That(_playerInfo.CampInfo.IsIntall, Is.True, "캠프가 설치되어야 함");
            Assert.That(_playerInfo.CampInfo.PlayerName, Is.EqualTo(_playerInfo.Name), "캠프 소유자 이름이 설정되어야 함");
            Assert.That(_playerInfo.CampInfo.ObjectInfo.MapId, Is.EqualTo(_playerInfo.ObjectInfo.MapId), "캠프 맵 ID가 설정되어야 함");
            Assert.That(_playerInfo.CampInfo.ObjectInfo.CurrentCell.X, Is.EqualTo(_playerInfo.ObjectInfo.CurrentCell.X), "캠프 위치가 플레이어 위치와 일치해야 함");
            Assert.That(_playerInfo.CampInfo.ObjectInfo.CurrentCell.Y, Is.EqualTo(_playerInfo.ObjectInfo.CurrentCell.Y), "캠프 위치가 플레이어 위치와 일치해야 함");
            
            // 브로드캐스트가 호출되었는지 확인
            Assert.That(_gameUser.BroadcastUpdateInfoCalled, Is.True, "캠프 정보가 브로드캐스트되어야 함");
            
            // CampInfo.Save가 호출되었는지 검증
            _mockCacheHelper.Verify(
                x => x.HashSetAsync(
                    "CampInfo", 
                    1001L, 
                    It.IsAny<byte[]>(), 
                    It.IsAny<int>()
                ), 
                Times.Once
            );
        }
        
        [Test]
        public void Encamp_WithNonExistingItem_ThrowsException()
        {
            // Arrange
            var request = new C_TO_U_ENCAMP { ItemUid = 9999 }; // 존재하지 않는 아이템 UID
            
            // Act & Assert
            var ex = Assert.ThrowsAsync<Exception>(async () => await _playerCamp.Encamp(request));
            Assert.That(ex.Message, Does.Contain("not found item info"));
        }
        
        [Test]
        public void Encamp_WhenAlreadyEncamped_ThrowsException()
        {
            // Arrange
            var request = new C_TO_U_ENCAMP { ItemUid = 1001 };
            
            // 이미 캠프가 설치된 상태로 설정
            _playerInfo.CampInfo.IsIntall = true;
            
            // Act & Assert
            var ex = Assert.ThrowsAsync<Exception>(async () => await _playerCamp.Encamp(request));
            Assert.That(ex.Message, Does.Contain("already encamp"));
        }
        
        [Test]
        public async Task Decamp_WhenEncamped_RemovesCampAndBroadcasts()
        {
            // Arrange
            // 캠프가 설치된 상태로 설정
            _playerInfo.CampInfo.IsIntall = true;
            
            // Act
            await _playerCamp.Decamp();
            
            // Assert
            Assert.That(_playerInfo.CampInfo.IsIntall, Is.False, "캠프가 제거되어야 함");
            Assert.That(_gameUser.BroadcastObjectDestroyCalled, Is.True, "객체 제거가 브로드캐스트되어야 함");
            
            // CampInfo.Save가 호출되었는지 검증
            _mockCacheHelper.Verify(
                x => x.HashSetAsync(
                    "CampInfo", 
                    1001L, 
                    It.IsAny<byte[]>(), 
                    It.IsAny<int>()
                ), 
                Times.Once
            );
        }
        
        [Test]
        public async Task Decamp_WhenNotEncamped_ReturnsWithoutError()
        {
            // Arrange
            // 캠프가 설치되지 않은 상태로 설정
            _playerInfo.CampInfo.IsIntall = false;
            
            // Act
            await _playerCamp.Decamp();
            
            // Assert
            Assert.That(_gameUser.BroadcastObjectDestroyCalled, Is.False, "캠프가 설치되지 않았으므로 브로드캐스트가 호출되지 않아야 함");
            
            _mockCacheHelper.Verify(
                x => x.HashSetAsync(
                    "CampInfo", 
                    1001L, 
                    It.IsAny<byte[]>(), 
                    It.IsAny<int>()
                ), 
                Times.Never
            );
        }
        
        [Test]
        public async Task PutItem_WithValidParams_AddsItemToCampAndBroadcasts()
        {
            // Arrange
            var cell = new Cell(5, 5);
            var request = new C_TO_U_ITEM_PUT { ItemUid = 1001, Cell = cell };
            
            // 캠프가 설치된 상태로 설정
            _playerInfo.CampInfo.IsIntall = true;
            
            // 추적 상태 초기화
            _gameUser.ResetTrackers();
            
            // Act
            await _playerCamp.PutItem(request);
            
            // Assert
            Assert.That(_playerInfo.CampInfo.InteractPropDict.ContainsKey(cell), Is.True, "지정된 셀에 아이템이 배치되어야 함");
            Assert.That(_playerInfo.CampInfo.InteractPropDict[cell].InteractPropUid, Is.EqualTo(1001), "배치된 아이템의 UID가 일치해야 함");
            
            // 브로드캐스트가 호출되었는지 확인
            Assert.That(_gameUser.BroadcastUpdateInfoCalled, Is.True, "캠프 정보가 브로드캐스트되어야 함");
            
            // CampInfo.Save가 호출되었는지 검증
            _mockCacheHelper.Verify(
                x => x.HashSetAsync(
                    "CampInfo", 
                    1001L, 
                    It.IsAny<byte[]>(), 
                    It.IsAny<int>()
                ), 
                Times.Once
            );
        }
        
        [Test]
        public void PutItem_WithNonExistingItem_ThrowsException()
        {
            // Arrange
            var cell = new Cell(5, 5);
            var request = new C_TO_U_ITEM_PUT { ItemUid = 9999, Cell = cell }; // 존재하지 않는 아이템 UID
            
            // 캠프가 설치된 상태로 설정
            _playerInfo.CampInfo.IsIntall = true;
            
            // Act & Assert
            var ex = Assert.ThrowsAsync<Exception>(async () => await _playerCamp.PutItem(request));
            Assert.That(ex.Message, Does.Contain("not found item info"));
        }
        
        [Test]
        public void PutItem_WhenNotEncamped_ThrowsException()
        {
            // Arrange
            var cell = new Cell(5, 5);
            var request = new C_TO_U_ITEM_PUT { ItemUid = 1001, Cell = cell };
            
            // 캠프가 설치되지 않은 상태로 설정
            _playerInfo.CampInfo.IsIntall = false;
            
            // Act & Assert
            var ex = Assert.ThrowsAsync<Exception>(async () => await _playerCamp.PutItem(request));
            Assert.That(ex.Message, Does.Contain("not encamp"));
        }
        
        [Test]
        public void PutItem_WithAlreadyUsedItem_ThrowsException()
        {
            // Arrange
            var cell1 = new Cell(5, 5);
            var cell2 = new Cell(6, 6);
            var request = new C_TO_U_ITEM_PUT { ItemUid = 1001, Cell = cell2 };
            
            // 캠프가 설치된 상태로 설정
            _playerInfo.CampInfo.IsIntall = true;
            
            // 이미 아이템이 배치된 상태로 설정
            var gameObjectInfo = new GameObjectInfo(ObjectType.INTERACTPROP, 1001, _playerInfo.ObjectInfo.MapId,
                _playerInfo.ObjectInfo.MapSubId, cell1);
            var interactPropInfo = new InteractPropInfo(gameObjectInfo, 1001);
            _playerInfo.CampInfo.InteractPropDict.Add(cell1, interactPropInfo);
            
            // Act & Assert
            var ex = Assert.ThrowsAsync<Exception>(async () => await _playerCamp.PutItem(request));
            Assert.That(ex.Message, Does.Contain("already put item"));
        }
        
        [Test]
        public void PutItem_WithAlreadyOccupiedCell_ThrowsException()
        {
            // Arrange
            var cell = new Cell(5, 5);
            var request = new C_TO_U_ITEM_PUT { ItemUid = 1001, Cell = cell };
            
            // 캠프가 설치된 상태로 설정
            _playerInfo.CampInfo.IsIntall = true;
            
            // 이미 다른 아이템이 해당 셀에 배치된 상태로 설정
            var gameObjectInfo = new GameObjectInfo(ObjectType.INTERACTPROP, 401000001, _playerInfo.ObjectInfo.MapId,
                _playerInfo.ObjectInfo.MapSubId, cell);
            var interactPropInfo = new InteractPropInfo(gameObjectInfo, 401000001);
            _playerInfo.CampInfo.InteractPropDict.Add(cell, interactPropInfo);
            
            // Act & Assert
            var ex = Assert.ThrowsAsync<Exception>(async () => await _playerCamp.PutItem(request));
            Assert.That(ex.Message, Does.Contain("already cell full"));
        }
        
        private PlayerInfo CreateTestPlayerInfo()
        {
            var playerId = 1001;
            var name = "TestPlayer";
            var itemInfo = new ItemInfo(1001, 401000001, 1);
            var objectInfo = new GameObjectInfo()
            {
                MapId = MapId.Library,
                ObjectType = ObjectType.PLAYER,
                CurrentCell = new Cell(10, 10),
                TargetCell = new Cell(10, 10)
            };
            var playerInfo = new PlayerInfo(playerId, false)
            {
                Name = name,
                State = PlayerState.IDLE,
                ObjectInfo = objectInfo,
                // CampInfo 초기화
                CampInfo = new CampInfo(playerId, "TestPlayer", objectInfo, itemInfo),
                // InventoryInfo 초기화
                InventoryInfo = new InventoryInfo(InventoryOwnerType.PLAYER, playerId)
            };
            
            return playerInfo;
        }
    }
}