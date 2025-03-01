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
    public class PlayerMapTests
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
        private PlayerMap _playerMap;
        
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
            
            // PlayerMap 인스턴스 생성
            _playerMap = new PlayerMap(_gameUser, _playerInfo);
        }
        
        [Test]
        public async Task ChangeMap_ToCommonMap_UpdatesPlayerMapInfo()
        {
            _playerInfo.ObjectInfo.MapId = MapId.Library;
            _playerInfo.ObjectInfo.CurrentCell = new Cell(102, 91);
            _playerInfo.ObjectInfo.TargetCell = new Cell(102, 91);
            
            // Arrange
            var originalMapId = _playerInfo.ObjectInfo.MapId;
            var originalCell = _playerInfo.ObjectInfo.CurrentCell.Clone();
            
            // 다른 맵으로 이동하는 요청 생성
            var request = new C_TO_U_CHANGE_MAP
            {
                MapId = MapId.Gym // 다른 공통 맵
            };
            
            _testNatsClient.Reset();
            
            // Act
            await _playerMap.ChangeMap(request);
            using (Assert.EnterMultipleScope())
            {
                // Assert
                // 맵 변경 시 PublishDestroy가 호출되는지 확인
                Assert.That(_testNatsClient.PublishCalled, Is.True, "맵 변경 시 메시지가 전송되어야 함");

                // 이전 맵 정보가 저장되었는지 확인
                Assert.That(_playerInfo.LastMapId, Is.EqualTo(originalMapId), "이전 맵 ID가 저장되어야 함");
                Assert.That(_playerInfo.LastCell.X, Is.EqualTo(originalCell.X), "이전 위치가 저장되어야 함");
                Assert.That(_playerInfo.LastCell.Y, Is.EqualTo(originalCell.Y), "이전 위치가 저장되어야 함");
            }
        }
        
        [Test]
        public async Task ChangeMap_ToCampMap_SendsEnterInstanceMessage()
        {
            // Arrange
            _playerInfo.ObjectInfo.MapId = MapId.Library; // 공통 맵에서 시작
            var originalMapId = _playerInfo.ObjectInfo.MapId;
            
            // 캠프 맵으로 이동하는 요청 생성
            var request = new C_TO_U_CHANGE_MAP
            {
                MapId = MapId.Camp,
                MapSubId = 1001 // 플레이어 ID와 동일
            };
            
            _testNatsClient.Reset();
            
            // Act
            await _playerMap.ChangeMap(request);
            
            // Assert
            // 인스턴스 입장 메시지가 전송되었는지 확인
            Assert.That(_testNatsClient.PublishCalled, Is.True, "인스턴스 입장 메시지가 전송되어야 함"); 
            
            // 특정 키워드가 포함된 주제로 메시지가 전송되었는지 확인
            var instanceMessageSent = _testNatsClient.PublishedMessages.Exists(m => 
                m.Subject.Contains("enter_instance"));
            
            using (Assert.EnterMultipleScope())
            {
                Assert.That(instanceMessageSent, Is.True, "인스턴스 입장 주제로 메시지가 전송되어야 함");

                // 이전 맵 정보가 저장되었는지 확인
                Assert.That(_playerInfo.LastMapId, Is.EqualTo(originalMapId), "이전 맵 ID가 저장되어야 함");
            }
        }
        
        [Test]
        public async Task ChangeMap_FromCampMap_EntersLastMapPosition()
        {
            // Arrange
            // 캠프 맵에서 시작
            _playerInfo.ObjectInfo.MapId = MapId.Camp;
            _playerInfo.ObjectInfo.MapSubId = 1001;
            
            // 마지막 위치 설정 (필드 맵)
            _playerInfo.LastMapId = MapId.Gym;
            _playerInfo.LastCell = new Cell(15, 15);
            
            // CampInfo 설정
            _playerInfo.CampInfo.ObjectInfo.MapId = MapId.Gym;
            _playerInfo.CampInfo.ObjectInfo.CurrentCell = new Cell(20, 20);
            
            // 캠프에서 나가는 요청 생성
            var request = new C_TO_U_CHANGE_MAP
            {
                MapId = MapId.None // 캠프에서 나감
            };
            
            _testNatsClient.Reset();
            
            // Act
            await _playerMap.ChangeMap(request);
            using (Assert.EnterMultipleScope())
            {
                // Assert
                // 메시지가 전송되었는지 확인
                Assert.That(_testNatsClient.PublishCalled, Is.True, "맵 변경 메시지가 전송되어야 함");

                // 플레이어가 캠프 위치로 이동했는지 확인
                Assert.That(_playerInfo.ObjectInfo.MapId, Is.EqualTo(MapId.Gym), "캠프가 있는 맵으로 이동해야 함");
                Assert.That(_playerInfo.ObjectInfo.CurrentCell.X, Is.EqualTo(_playerInfo.CampInfo.ObjectInfo.CurrentCell.X),
                    "캠프 위치로 이동해야 함");
                Assert.That(_playerInfo.ObjectInfo.CurrentCell.Y, Is.EqualTo(_playerInfo.CampInfo.ObjectInfo.CurrentCell.Y),
                    "캠프 위치로 이동해야 함");
            }
        }
        
        [Test]
        public async Task EnterMap_CommonMap_UpdatesPlayerPositionAndSendsPacket()
        {
            // Arrange
            var targetMapId = MapId.Gym;
            var targetPosition = new Cell(25, 25);
            var isFlip = true;
            
            _testNatsClient.Reset();
            _userToken.PacketWasSent = false;
            
            // Act
            await _playerMap.EnterMap(targetMapId, targetPosition, isFlip, false);
            
            // Assert
            // 플레이어 정보가 업데이트되었는지 확인
            Assert.That(_playerInfo.ObjectInfo.MapId, Is.EqualTo(targetMapId), "맵 ID가 업데이트되어야 함");
            Assert.That(_playerInfo.ObjectInfo.CurrentCell, Is.EqualTo(targetPosition), "위치가 업데이트되어야 함");
            Assert.That(_playerInfo.ObjectInfo.IsFlip, Is.EqualTo(isFlip), "방향이 업데이트되어야 함");
            
            // 변경 패킷이 전송되었는지 확인
            Assert.That(_userToken.PacketWasSent, Is.True, "맵 변경 패킷이 전송되어야 함");
        }
        
        [Test]
        public async Task EnterMap_InstanceMap_SendsEnterInstanceMessage()
        {
            // Arrange
            var targetMapId = MapId.TutorialLibrary; // 인스턴스 맵
            var targetPosition = new Cell(5, 5);
            var isFlip = false;
            
            _testNatsClient.Reset();
            
            // Act
            await _playerMap.EnterMap(targetMapId, targetPosition, isFlip, false);
            
            // Assert
            // 인스턴스 입장 메시지가 전송되었는지 확인
            Assert.That(_testNatsClient.PublishCalled, Is.True, "인스턴스 입장 메시지가 전송되어야 함");
            
            // 플레이어 정보가 업데이트되었는지 확인
            Assert.That(_playerInfo.ObjectInfo.MapId, Is.EqualTo(targetMapId), "맵 ID가 업데이트되어야 함");
            Assert.That(_playerInfo.ObjectInfo.CurrentCell, Is.EqualTo(targetPosition), "위치가 업데이트되어야 함");
            
            // MapSubId가 올바르게 설정되었는지 확인 (인스턴스 맵이므로 플레이어 ID와 같아야 함)
            Assert.That(_playerInfo.ObjectInfo.MapSubId, Is.EqualTo(_playerInfo.PlayerId), 
                "인스턴스 맵의 SubId는 플레이어 ID와 같아야 함");
        }
        
        [Test]
        public async Task EnterMap_WithLoginFlag_DoesNotSendPacket()
        {
            // Arrange
            var targetMapId = MapId.Library;
            var targetPosition = new Cell(10, 10);
            var isFlip = false;
            var isLogin = true; // 로그인 시 맵 입장
            
            _testNatsClient.Reset();
            _userToken.PacketWasSent = false;
            
            // Act
            await _playerMap.EnterMap(targetMapId, targetPosition, isFlip, isLogin);
            using (Assert.EnterMultipleScope())
            {
                // Assert
                // 인스턴스 입장 메시지가 전송되었는지 확인
                Assert.That(_testNatsClient.PublishCalled, Is.True, "인스턴스 입장 메시지가 전송되어야 함");

                // 로그인 시에는 클라이언트에 변경 패킷을 보내지 않음
                Assert.That(_userToken.PacketWasSent, Is.False, "로그인 시에는 맵 변경 패킷을 보내지 않아야 함");
            }
        }
        
        [Test]
        public async Task EnterCamp_UpdatesPlayerPositionToCamp()
        {
            // Arrange
            var mapSubId = 1001L; // 캠프 인스턴스 ID
            
            // Act
            await _playerMap.EnterCamp(mapSubId);
            using (Assert.EnterMultipleScope())
            {
                // Assert
                // 플레이어 정보가 업데이트되었는지 확인
                Assert.That(_playerInfo.ObjectInfo.MapId, Is.EqualTo(MapId.Camp), "맵 ID가 캠프로 변경되어야 함");
                Assert.That(_playerInfo.ObjectInfo.MapSubId, Is.EqualTo(mapSubId), "맵 SubId가 설정되어야 함");

                // 스폰 위치가 설정되었는지 확인
                Assert.That(_playerInfo.ObjectInfo.CurrentCell, Is.Not.Null, "스폰 위치가 설정되어야 함");
                Assert.That(_playerInfo.ObjectInfo.TargetCell, Is.EqualTo(_playerInfo.ObjectInfo.CurrentCell),
                    "현재 위치와 목표 위치가 같아야 함");
            }
        }
        
        [Test]
        public async Task PublishDestroy_SendsDestroyMessage()
        {
            // Arrange
            _testNatsClient.Reset();
            
            // Act
            await _playerMap.PublishDestroy();
            
            // Assert
            // 객체 삭제 메시지가 전송되었는지 확인
            Assert.That(_testNatsClient.PublishCalled, Is.True, "객체 삭제 메시지가 전송되어야 함");
            
            // 특정 키워드가 포함된 주제로 메시지가 전송되었는지 확인
            bool destroyMessageSent = _testNatsClient.PublishedMessages.Exists(m => 
                m.Subject.Contains("destroy"));
            Assert.That(destroyMessageSent, Is.True, "삭제 주제로 메시지가 전송되어야 함");
        }
        
        [Test]
        public void CurrentMapInfo_ReturnsCorrectInfo()
        {
            // Arrange
            _playerInfo.ObjectInfo.MapId = MapId.TutorialLibrary;
            _playerInfo.ObjectInfo.MapSubId = 5000;
            _playerInfo.ObjectInfo.CurrentCell = new Cell(15, 20);
            _playerInfo.ObjectInfo.IsFlip = true;
            
            // Act
            var (mapId, mapSubId, cell, isFlip) = _playerMap.CurrentMapInfo;
            using (Assert.EnterMultipleScope())
            {

                // Assert
                Assert.That(mapId, Is.EqualTo(_playerInfo.ObjectInfo.MapId), "맵 ID가 일치해야 함");
                Assert.That(mapSubId, Is.EqualTo(_playerInfo.ObjectInfo.MapSubId), "맵 SubId가 일치해야 함");
                Assert.That(cell, Is.EqualTo(_playerInfo.ObjectInfo.CurrentCell), "위치가 일치해야 함");
                Assert.That(isFlip, Is.EqualTo(_playerInfo.ObjectInfo.IsFlip), "방향이 일치해야 함");
            }
        }
        
        [Test]
        public void LastMapInfo_ReturnsCorrectInfo()
        {
            // Arrange
            _playerInfo.LastMapId = MapId.Gym;
            _playerInfo.LastCell = new Cell(25, 30);
            
            // Act
            var (mapId, cell) = _playerMap.LastMapInfo;
            using (Assert.EnterMultipleScope())
            {

                // Assert
                Assert.That(mapId, Is.EqualTo(_playerInfo.LastMapId), "마지막 맵 ID가 일치해야 함");
                Assert.That(cell, Is.EqualTo(_playerInfo.LastCell), "마지막 위치가 일치해야 함");
            }
        }
        
        private PlayerInfo CreateTestPlayerInfo()
        {
            var playerId = 1001;
            var playerInfo = new PlayerInfo(playerId, false)
            {
                Name = "TestPlayer",
                State = PlayerState.IDLE,
                ObjectInfo =
                {
                    MapId = MapId.Library,
                    ObjectType = ObjectType.PLAYER,
                    CurrentCell = new Cell(10, 10),
                    TargetCell = new Cell(10, 10)
                }
            };
            
            // 캠프 정보 설정
            playerInfo.CampInfo.ObjectInfo.MapId = MapId.None;
            playerInfo.CampInfo.ObjectInfo.CurrentCell = new Cell(20, 20);
            
            return playerInfo;
        }
    }
}