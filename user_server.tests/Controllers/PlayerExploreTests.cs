using System.Reflection;
using Microsoft.Extensions.Logging;
using Moq;
using network.common;
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
    public class PlayerExploreTests
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
        private PlayerExplore _playerExplore;
        
        // 테스트 전용 의존성
        private TestPlayerQuest _testPlayerQuest;
        private TestPlayerInventory _testPlayerInventory;
        private TestPlayerProgress _testPlayerProgress;
        
        private ExploreTargetInfo _exploreTargetInfo;
        
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
            
            // 테스트 ExploreTargetInfo 생성
            _exploreTargetInfo = CreateTestExploreTargetInfo();
            
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
                
            _mockCacheHelper
                .Setup(x => x.HashDeleteAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<int>()))
                .ReturnsAsync(true);
                
            // ItemInfo 생성 모킹
            _mockCacheHelper
                .Setup(x => x.StringIncrementAsync(It.IsAny<string>(), It.IsAny<int>()))
                .ReturnsAsync(2001);
            
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
            
            // 테스트 전용 의존성 생성
            _testPlayerQuest = new TestPlayerQuest(_gameUser, _playerInfo);
            _testPlayerInventory = new TestPlayerInventory(_gameUser, _playerInfo, _testPlayerQuest);
            _testPlayerProgress = new TestPlayerProgress(_gameUser);
            
            // PlayerExplore 인스턴스 생성 (테스트용 버전)
            _playerExplore = new TestPlayerExplore(
                _gameUser, 
                _playerInfo, 
                _testPlayerQuest, 
                _testPlayerInventory, 
                _testPlayerProgress,
                _exploreTargetInfo
            );
        }
        
        [Test]
        public async Task Explore_WithValidParams_StartsExplorationAndUpdatesState()
        {
            // Arrange
            var request = new C_TO_U_EXPLORE { ExploreTargetUid = 1001 };
            
            // 스태미나 충분히 설정
            _playerInfo.Stamina = 20;
            
            // UserToken 패킷 전송 상태 초기화
            _userToken.PacketWasSent = false;
            
            // Act
            await _playerExplore.Explore(request);
            
            // Assert
            // 플레이어 상태가 탐험 중으로 변경되었는지 확인
            Assert.That(_playerInfo.State, Is.EqualTo(PlayerState.EXPLORE_1), "플레이어 상태가 탐험 중으로 변경되어야 함");
            
            // 스태미나가 차감되었는지 확인
            Assert.That(_playerInfo.Stamina, Is.EqualTo(15), "스태미나가 5 차감되어야 함");
            
            // ExploreTargetInfo가 업데이트되었는지 확인
            Assert.That(_exploreTargetInfo.PlayerId, Is.EqualTo(_playerInfo.PlayerId), "조사 대상에 플레이어 ID가 설정되어야 함");
            Assert.That(_exploreTargetInfo.EndTimestamp, Is.Not.EqualTo(default(DateTime)), "완료 시간이 설정되어야 함");
            
            // 패킷이 전송되었는지 확인
            Assert.That(_userToken.PacketWasSent, Is.True, "탐험 시작 패킷이 전송되어야 함");
            
            // 브로드캐스트가 호출되었는지 확인
            Assert.That(_gameUser.BroadcastUpdateInfoCalled, Is.True, "플레이어 정보가 브로드캐스트되어야 함");
            
            // PlayerInfo.Save가 호출되었는지 검증
            _mockCacheHelper.Verify(
                x => x.HashSetAsync(
                    "PlayerInfo", 
                    It.IsAny<long>(), 
                    It.IsAny<byte[]>(), 
                    It.IsAny<int>()
                ), 
                Times.Once
            );
            
            // ExploreTargetInfo.Save가 호출되었는지 검증
            _mockCacheHelper.Verify(
                x => x.HashSetAsync(
                    "ExploreTargetInfo", 
                    It.IsAny<long>(), 
                    It.IsAny<byte[]>(), 
                    It.IsAny<int>()
                ), 
                Times.Once
            );
            
            // AddProgressItem이 호출되었는지 확인
            Assert.That(_testPlayerProgress.AddProgressItemCalled, Is.True, "AddProgressItem이 호출되어야 함");
            Assert.That(_testPlayerProgress.LastProgressItem, Is.Not.Null, "LastProgressItem이 설정되어야 함");
            Assert.That(_testPlayerProgress.LastProgressItem, Is.TypeOf<ExploreProgressInfo>(), "LastProgressItem이 ExploreProgressInfo 타입이어야 함");
        }
        
        [Test]
        public async Task Explore_WithoutStamina_ReturnsFatalError()
        {
            // Arrange
            var request = new C_TO_U_EXPLORE { ExploreTargetUid = 1001 };
            
            // 스태미나 부족하게 설정
            _playerInfo.Stamina = 0;
            
            // UserToken 패킷 전송 상태 초기화
            _userToken.PacketWasSent = false;
            
            // Act
            await _playerExplore.Explore(request);
            
            // Assert
            // 패킷이 전송되었는지 확인 (오류 패킷)
            Assert.That(_userToken.PacketWasSent, Is.True, "오류 패킷이 전송되어야 함");
            Assert.That(_userToken.LastSentPacketId, Is.EqualTo(Protocol.U_TO_C_EXPLORE), "U_TO_C_EXPLORE 패킷이 전송되어야 함");
            
            // 플레이어 상태와 스태미나가 변경되지 않았는지 확인
            Assert.That(_playerInfo.State, Is.EqualTo(PlayerState.IDLE), "플레이어 상태가 변경되지 않아야 함");
            Assert.That(_playerInfo.Stamina, Is.EqualTo(0), "스태미나가 변경되지 않아야 함");
            
            // PlayerInfo.Save가 호출되지 않았는지 검증
            _mockCacheHelper.Verify(
                x => x.HashSetAsync(
                    "PlayerInfo", 
                    It.IsAny<long>(), 
                    It.IsAny<byte[]>(), 
                    It.IsAny<int>()
                ), 
                Times.Never
            );
        }
        
        [Test]
        public async Task Explore_WithAlreadyUsedTarget_ReturnsAlreadyInUseError()
        {
            // Arrange
            var request = new C_TO_U_EXPLORE { ExploreTargetUid = 1001 };
            
            // 스태미나 충분히 설정
            _playerInfo.Stamina = 20;
            
            // 이미 다른 플레이어가 사용 중으로 설정
            _exploreTargetInfo.PlayerId = 9999; // 다른 플레이어 ID
            
            // UserToken 패킷 전송 상태 초기화
            _userToken.PacketWasSent = false;
            
            // Act
            await _playerExplore.Explore(request);
            
            // Assert
            // 패킷이 전송되었는지 확인 (오류 패킷)
            Assert.That(_userToken.PacketWasSent, Is.True, "오류 패킷이 전송되어야 함");
            Assert.That(_userToken.LastSentPacketId, Is.EqualTo(Protocol.U_TO_C_EXPLORE), "U_TO_C_EXPLORE 패킷이 전송되어야 함");
            
            // 플레이어 상태와 스태미나가 변경되지 않았는지 확인
            Assert.That(_playerInfo.State, Is.EqualTo(PlayerState.IDLE), "플레이어 상태가 변경되지 않아야 함");
            Assert.That(_playerInfo.Stamina, Is.EqualTo(20), "스태미나가 변경되지 않아야 함");
            
            // PlayerInfo.Save가 호출되지 않았는지 검증
            _mockCacheHelper.Verify(
                x => x.HashSetAsync(
                    "PlayerInfo", 
                    It.IsAny<long>(), 
                    It.IsAny<byte[]>(), 
                    It.IsAny<int>()
                ), 
                Times.Never
            );
        }
        
        [Test]
        public async Task OnExploreComplete_WithReusableTarget_UpdatesTargetAndGivesReward()
        {
            // Arrange
            // 재사용 가능한 탐험 대상 설정 (ID 1, 2, 3은 reusable=true로 간주)
            _exploreTargetInfo.ExploreTargetId = 1; // 재사용 가능한 대상
            
            // ExploreProgressInfo 생성
            var exploreProgressInfo = new ExploreProgressInfo(_exploreTargetInfo);
            
            // 준비 - 리플렉션을 통해 private 메서드 호출 (OnExploreComplete)
            var method = typeof(PlayerExplore).GetMethod("OnExploreComplete", 
                BindingFlags.NonPublic | BindingFlags.Instance);
            
            // UserToken 패킷 전송 상태 초기화
            _userToken.PacketWasSent = false;
            _gameUser.ResetTrackers();
            _testPlayerInventory.SendUpdateItemsCalled = false;
            
            // Act
            await (Task)method?.Invoke(_playerExplore, [exploreProgressInfo])!;
            
            // Assert
            // 탐험 대상이 재사용 가능한 상태로 업데이트되었는지 확인
            Assert.That(_exploreTargetInfo.PlayerId, Is.EqualTo(-1), "재사용 가능한 대상은 PlayerId가 -1로 설정되어야 함");
            
            // ExploreTargetInfo.Save가 호출되었는지 검증
            _mockCacheHelper.Verify(
                x => x.HashSetAsync(
                    "ExploreTargetInfo", 
                    It.IsAny<long>(), 
                    It.IsAny<byte[]>(), 
                    It.IsAny<int>()
                ), 
                Times.Once
            );
            
            // ExploreTargetInfo.Delete가 호출되지 않았는지 검증
            _mockCacheHelper.Verify(
                x => x.HashDeleteAsync(
                    "ExploreTargetInfo", 
                    It.IsAny<long>(), 
                    It.IsAny<int>()
                ), 
                Times.Never
            );
            
            // 브로드캐스트가 호출되었는지 확인
            Assert.That(_gameUser.BroadcastUpdateInfoCalled, Is.True, "탐험 대상 정보가 브로드캐스트되어야 함");
            
            // 플레이어 상태가 IDLE로 변경되었는지 확인
            Assert.That(_playerInfo.State, Is.EqualTo(PlayerState.IDLE), "플레이어 상태가 IDLE로 변경되어야 함");
            
            // 패킷이 전송되었는지 확인
            Assert.That(_userToken.PacketWasSent, Is.True, "완료 패킷이 전송되어야 함");
            
            // PlayerInfo.Save가 호출되었는지 검증
            _mockCacheHelper.Verify(
                x => x.HashSetAsync(
                    "PlayerInfo", 
                    It.IsAny<long>(), 
                    It.IsAny<byte[]>(), 
                    It.IsAny<int>()
                ), 
                Times.Once
            );
            
            // 인벤토리 아이템 추가 호출 확인
            Assert.That(_testPlayerInventory.SendUpdateItemsCalled, Is.True, "SendUpdateItems가 호출되어야 함");
        }
        
        [Test]
        public async Task OnExploreComplete_WithNonReusableTarget_DeletesTargetAndGivesReward()
        {
            // Arrange
            _exploreTargetInfo.ExploreTargetId = 100000001; // 재사용 불가능한 대상
            
            // ExploreProgressInfo 생성
            var exploreProgressInfo = new ExploreProgressInfo(_exploreTargetInfo);
            
            // 준비 - 리플렉션을 통해 private 메서드 호출 (OnExploreComplete)
            var method = typeof(PlayerExplore).GetMethod("OnExploreComplete", 
                BindingFlags.NonPublic | BindingFlags.Instance);
            
            // UserToken 패킷 전송 상태 초기화
            _userToken.PacketWasSent = false;
            _gameUser.ResetTrackers();
            _testPlayerInventory.SendUpdateItemsCalled = false;
            
            // Act
            await (Task)method?.Invoke(_playerExplore, [exploreProgressInfo])!;
            
            // Assert
            // ExploreTargetInfo.Save가 호출되지 않았는지 검증
            _mockCacheHelper.Verify(
                x => x.HashSetAsync(
                    "ExploreTargetInfo", 
                    It.IsAny<long>(), 
                    It.IsAny<byte[]>(), 
                    It.IsAny<int>()
                ), 
                Times.Never
            );
            
            // ExploreTargetInfo.Delete가 호출되었는지 검증
            _mockCacheHelper.Verify(
                x => x.HashDeleteAsync(
                    "ExploreTargetInfo", 
                    It.IsAny<long>(), 
                    It.IsAny<int>()
                ), 
                Times.AtLeastOnce
            );
            
            // 객체 제거 브로드캐스트가 호출되었는지 확인
            Assert.That(_gameUser.BroadcastObjectDestroyCalled, Is.True, "탐험 대상 객체 제거가 브로드캐스트되어야 함");
            
            // 플레이어 상태가 IDLE로 변경되었는지 확인
            Assert.That(_playerInfo.State, Is.EqualTo(PlayerState.IDLE), "플레이어 상태가 IDLE로 변경되어야 함");
            
            // 패킷이 전송되었는지 확인
            Assert.That(_userToken.PacketWasSent, Is.True, "완료 패킷이 전송되어야 함");
            
            // PlayerInfo.Save가 호출되었는지 검증
            _mockCacheHelper.Verify(
                x => x.HashSetAsync(
                    "PlayerInfo", 
                    It.IsAny<long>(), 
                    It.IsAny<byte[]>(), 
                    It.IsAny<int>()
                ), 
                Times.Once
            );
            
            // 인벤토리 아이템 추가 호출 확인
            Assert.That(_testPlayerInventory.SendUpdateItemsCalled, Is.True, "SendUpdateItems가 호출되어야 함");
        }
        
        [Test]
        public async Task OnExploreComplete_WithSpecialTarget_UpdatesQuestsCorrectly()
        {
            // Arrange
            // 특별한 탐험 대상 설정 (퀘스트 진행과 새 퀘스트 시작)
            _exploreTargetInfo.ExploreTargetId = 7; // 특별한 대상
            
            // ExploreProgressInfo 생성
            var exploreProgressInfo = new ExploreProgressInfo(_exploreTargetInfo);
            
            // 준비 - 리플렉션을 통해 private 메서드 호출 (OnExploreComplete)
            var method = typeof(PlayerExplore).GetMethod("OnExploreComplete", 
                BindingFlags.NonPublic | BindingFlags.Instance);
            
            // UserToken 패킷 전송 상태 초기화
            _userToken.PacketWasSent = false;
            _testPlayerInventory.SendUpdateItemsCalled = false;
            
            // Act
            await ((Task)method?.Invoke(_playerExplore, [exploreProgressInfo])!)!;
            
            // Assert
            // 패킷이 전송되었는지 확인
            Assert.That(_userToken.PacketWasSent, Is.True, "완료 패킷이 전송되어야 함");
            
            // 인벤토리 아이템 추가 호출 확인
            Assert.That(_testPlayerInventory.SendUpdateItemsCalled, Is.True, "SendUpdateItems가 호출되어야 함");
        }
        
        private PlayerInfo CreateTestPlayerInfo()
        {
            var playerId = 1001;
            var playerInfo = new PlayerInfo(playerId, false)
            {
                Name = "TestPlayer",
                State = PlayerState.IDLE,
                Stamina = 20,
                ObjectInfo =
                {
                    MapId = MapId.Library,
                    ObjectType = ObjectType.PLAYER,
                    CurrentCell = new Cell(10, 10),
                    TargetCell = new Cell(10, 10)
                },
                // 필요한 정보 초기화
                InventoryInfo = new InventoryInfo(InventoryOwnerType.PLAYER, playerId),
                QuestDiary = new QuestDiary(playerId)
            };
            
            return playerInfo;
        }
        
        private ExploreTargetInfo CreateTestExploreTargetInfo()
        {
            var targetUid = 1001L;
            var exploreTargetInfo = new ExploreTargetInfo
            {
                ExploreTargetUid = targetUid,
                ExploreTargetId = 1,
                PlayerId = 0, // 아직 사용 중이지 않음
                ObjectInfo = new GameObjectInfo(ObjectType.EXPLORETARGET, targetUid, MapId.Library, 0, new Cell(11, 11))
            };
            
            return exploreTargetInfo;
        }
        
        [TearDown]
        public void TearDown()
        {
            // Dispose 가능한 리소스 정리
            _testPlayerProgress?.Dispose();
        }
    }
    
    // PlayerExplore 테스트를 위한 확장 클래스
    public class TestPlayerExplore(
        GameUser user,
        PlayerInfo playerInfo,
        PlayerQuest playerQuest,
        PlayerInventory playerInventory,
        PlayerProgress playerProgress,
        ExploreTargetInfo testExploreTargetInfo)
        : PlayerExplore(user, playerInfo, playerQuest, playerInventory, playerProgress)
    {
        // Explore 메서드 내부에서 ExploreTargetInfo.Load를 대체
        protected override Task<ExploreTargetInfo> LoadExploreTargetInfo(long exploreTargetUid)
        {
            return Task.FromResult(testExploreTargetInfo);
        }
    }
}