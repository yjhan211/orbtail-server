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
using user_server.tests.components;

namespace user_server.tests.controllers
{
    [TestFixture]
    public class PlayerQuestTests
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
        private PlayerQuest _playerQuest;
        
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
            
            // MailBox.Load 모킹
            _mockCacheHelper
                .Setup(x => x.HashGetAllAsync("mail_box:1001", It.IsAny<int>()))
                .ReturnsAsync([]);
                
            // PlayerQuest 인스턴스 생성
            _playerQuest = new PlayerQuest(_gameUser, _playerInfo);
        }
        
        [Test]
        public async Task StartQuest_WithValidQuestId_AddsQuestToQuestDiary()
        {
            // Arrange
            var questId = 100000001; // 유효한 퀘스트 ID
            
            // 테스트를 위한 퀘스트 데이터가 존재하는지 확인
            Assert.That(GameQuestData.Get(questId), Is.Not.Null, "테스트를 위한 퀘스트 데이터가 존재해야 함");
            
            // 시작 전 퀘스트 개수 기록
            int initialCount = _playerInfo.QuestDiary.QuestDict.Count;
            
            // Act
            await _playerQuest.StartQuest(questId);
            
            // Assert
            // QuestDiary에 퀘스트가 추가되었는지 확인
            Assert.That(_playerInfo.QuestDiary.QuestDict.Count, Is.EqualTo(initialCount + 1), "퀘스트가 추가되어야 함");
            Assert.That(_playerInfo.QuestDiary.QuestDict.ContainsKey(questId), Is.True, "추가된 퀘스트 ID가 존재해야 함");
            
            // QuestDiary.Save가 호출되었는지 검증
            _mockCacheHelper.Verify(
                x => x.HashSetAsync(
                    It.Is<string>(s => s == "QuestDiary"),
                    It.Is<long>(id => id == _playerInfo.PlayerId),
                    It.IsAny<byte[]>(), 
                    It.IsAny<int>()
                ), 
                Times.AtLeastOnce
            );
        }
        
        [Test]
        public async Task StartQuest_WithUpdateQuestsList_AddsQuestToList()
        {
            // Arrange
            var questId = 100000001; // 유효한 퀘스트 ID
            var updateQuests = new List<QuestInfo>();
            
            // Act
            await _playerQuest.StartQuest(questId, updateQuests);
            
            // Assert
            // 퀘스트가 리스트에 추가되었는지 확인
            Assert.That(updateQuests, Has.Count.EqualTo(1), "업데이트 리스트에 퀘스트가 추가되어야 함");
            Assert.That(updateQuests[0].QuestId, Is.EqualTo(questId), "추가된 퀘스트 ID가 일치해야 함");
        }
        
        [Test]
        public async Task IncreaseQuestCount_WithValidQuestId_UpdatesQuestProgress()
        {
            // Arrange
            var questId = 100000004; // 유효한 퀘스트 ID
            var count = 2; // 증가할 수량
            var updateQuests = new List<QuestInfo>();
            
            // 퀘스트 데이터 준비
            var quest = new QuestInfo(_playerInfo.PlayerId, questId);
            _playerInfo.QuestDiary.AddQuest(quest);
            
            // Act
            await _playerQuest.IncreaseQuestCount(questId, count, updateQuests);
            
            // Assert
            // 퀘스트가 업데이트 리스트에 추가되었는지 확인
            Assert.That(updateQuests, Has.Count.EqualTo(1), "업데이트 리스트에 퀘스트가 추가되어야 함");
            Assert.That(updateQuests[0].QuestId, Is.EqualTo(questId), "추가된 퀘스트 ID가 일치해야 함");
            Assert.That(updateQuests[0].Count, Is.EqualTo(count), "퀘스트 진행도가 증가해야 함");
            
            // QuestDiary.Save가 호출되었는지 검증
            _mockCacheHelper.Verify(
                x => x.HashSetAsync(
                    It.Is<string>(s => s == "QuestDiary"),
                    It.Is<long>(id => id == _playerInfo.PlayerId),
                    It.IsAny<byte[]>(), 
                    It.IsAny<int>()
                ), 
                Times.AtLeastOnce
            );
        }
        
        [Test]
        public async Task IncreaseQuestCount_WithRequest_UpdatesQuestAndSendsPacket()
        {
            // Arrange
            var questId = 100000004; // 유효한 퀘스트 ID
            var count = 3; // 증가할 수량
            var request = new C_TO_U_QUEST_INCREASE { QuestId = questId, Count = count };
            
            // 퀘스트 데이터 준비
            var quest = new QuestInfo(_playerInfo.PlayerId, questId);
            _playerInfo.QuestDiary.AddQuest(quest);
            
            // UserToken 패킷 전송 상태 초기화
            _userToken.PacketWasSent = false;
            
            // Act
            await _playerQuest.IncreaseQuestCount(request);
            
            // Assert
            // 패킷이 전송되었는지 검증
            Assert.That(_userToken.PacketWasSent, Is.True, "퀘스트 업데이트 패킷이 전송되어야 함");
            
            // 퀘스트 진행도가 증가했는지 확인
            Assert.That(_playerInfo.QuestDiary.QuestDict[questId].Count, Is.EqualTo(count), "퀘스트 진행도가 증가해야 함");
            
            // QuestDiary.Save가 호출되었는지 검증
            _mockCacheHelper.Verify(
                x => x.HashSetAsync(
                    It.Is<string>(s => s == "QuestDiary"),
                    It.Is<long>(id => id == _playerInfo.PlayerId),
                    It.IsAny<byte[]>(), 
                    It.IsAny<int>()
                ), 
                Times.AtLeastOnce
            );
        }
        
        [Test]
        public void IncreaseQuestCount_WithNonexistentQuest_ThrowsException()
        {
            // Arrange
            var questId = 999999; // 존재하지 않는 퀘스트 ID
            var request = new C_TO_U_QUEST_INCREASE { QuestId = questId, Count = 1 };
            
            // Act & Assert
            var ex = Assert.ThrowsAsync<Exception>(async () => await _playerQuest.IncreaseQuestCount(request));
            Assert.That(ex.Message, Does.Contain("Not Progressed Quest"));
        }
        
        [Test]
        public async Task CompleteQuest_WithCompletedQuest_UpdatesStateAndSendsPacket()
        {
            // Arrange
            var questId = 100000001; // 유효한 퀘스트 ID
            var request = new C_TO_U_QUEST_SUCCESS { QuestId = questId };
            
            // 퀘스트 데이터 준비 (이미 완료 조건 충족)
            var quest = new QuestInfo(_playerInfo.PlayerId, questId);
            var questData = GameQuestData.Get(questId);
            quest.Count = questData.RequireCount; // 완료 조건 충족
            _playerInfo.QuestDiary.AddQuest(quest);
            
            // UserToken 패킷 전송 상태 초기화
            _userToken.PacketWasSent = false;
            
            // Act
            await _playerQuest.CompleteQuest(request);
            
            // Assert
            // 패킷이 전송되었는지 검증
            Assert.That(_userToken.PacketWasSent, Is.True, "퀘스트 완료 패킷이 전송되어야 함");
            
            // 퀘스트 상태가 END로 변경되었는지 확인
            Assert.That(_playerInfo.QuestDiary.QuestDict[questId].State, Is.EqualTo(QuestState.END), "퀘스트 상태가 END로 변경되어야 함");
            
            _mockCacheHelper.Verify(
                x => x.HashSetAsync(
                    It.Is<string>(s => s == "QuestDiary"),
                    It.Is<long>(id => id == _playerInfo.PlayerId),
                    It.IsAny<byte[]>(), 
                    It.IsAny<int>()
                ), 
                Times.AtLeastOnce
            );
        }
        
        [Test]
        public void CompleteQuest_WithInsufficientProgress_ThrowsException()
        {
            // Arrange
            var questId = 100000001; // 유효한 퀘스트 ID
            var request = new C_TO_U_QUEST_SUCCESS { QuestId = questId };
            
            // 퀘스트 데이터 준비 (완료 조건 미충족)
            var quest = new QuestInfo(_playerInfo.PlayerId, questId);
            quest.Count = 0; // 완료 조건 미충족
            _playerInfo.QuestDiary.AddQuest(quest);
            
            // Act & Assert
            var ex = Assert.ThrowsAsync<Exception>(async () => await _playerQuest.CompleteQuest(request));
            Assert.That(ex.Message, Does.Contain("Invalid State"));
        }
        
        [Test]
        public void CompleteQuest_WithNonexistentQuest_ThrowsException()
        {
            // Arrange
            var questId = 999999; // 존재하지 않는 퀘스트 ID
            var request = new C_TO_U_QUEST_SUCCESS { QuestId = questId };
            
            // Act & Assert
            var ex = Assert.ThrowsAsync<Exception>(async () => await _playerQuest.CompleteQuest(request));
            Assert.That(ex.Message, Does.Contain("Not Started Quest"));
        }
        
        [Test]
        public void CompleteQuest_WithTutorialQuest_UpdatesPlayerInfo()
        {
            // Arrange
            var questId = 100000015; // 튜토리얼 퀘스트 ID
            var request = new C_TO_U_QUEST_SUCCESS { QuestId = questId };
            
            // 퀘스트 데이터 준비 (완료 조건 충족)
            var quest = new QuestInfo(_playerInfo.PlayerId, questId);
            var questData = GameQuestData.Get(questId);
            quest.Count = questData.RequireCount; // 완료 조건 충족
            _playerInfo.QuestDiary.AddQuest(quest);
            
            _playerInfo.IsTutorial = true; // 튜토리얼 상태로 설정
            
            // Act - 비동기 메서드 호출을 동기적으로 처리
            Assert.DoesNotThrowAsync(async () => await _playerQuest.CompleteQuest(request));
            
            // Assert
            Assert.That(_playerInfo.IsTutorial, Is.False, "튜토리얼 상태가 false로 변경되어야 함");
            Assert.That(_playerInfo.LastMapId, Is.EqualTo(MapId.Gym), "마지막 맵 ID가 Gym으로 설정되어야 함");
        }
        
        [Test]
        public void SendCurrentQuests_WithNoQuests_DoesNotSendPacket()
        {
            // Arrange
            // QuestDiary는 비어 있는 상태로 설정됨
            _playerInfo.QuestDiary.QuestDict.Clear();
            
            _userToken.PacketWasSent = false;
            
            // Act
            _playerQuest.SendCurrentQuests();
            
            // Assert
            // 퀘스트가 없으므로 패킷이 전송되지 않아야 함
            Assert.That(_userToken.PacketWasSent, Is.False, "퀘스트가 없으면 패킷이 전송되지 않아야 함");
        }
        
        [Test]
        public void SendCurrentQuests_WithQuests_SendsPackets()
        {
            // Arrange
            // 퀘스트 데이터 준비
            var quest1 = new QuestInfo(_playerInfo.PlayerId, 100000001);
            var quest2 = new QuestInfo(_playerInfo.PlayerId, 100000004);
            _playerInfo.QuestDiary.AddQuest(quest1);
            _playerInfo.QuestDiary.AddQuest(quest2);
            
            _userToken.PacketWasSent = false;
            
            // Act
            _playerQuest.SendCurrentQuests();
            
            // Assert
            // 퀘스트 목록 패킷이 전송되어야 함
            Assert.That(_userToken.PacketWasSent, Is.True, "퀘스트 목록 패킷이 전송되어야 함");
        }
        
        [Test]
        public void StartQuest_WithExistingQuest_ThrowsException()
        {
            // Arrange
            var questId = 100000001; // 유효한 퀘스트 ID
            
            // 이미 존재하는 퀘스트 설정
            var existingQuest = new QuestInfo(_playerInfo.PlayerId, questId);
            _playerInfo.QuestDiary.AddQuest(existingQuest);
            
            // Act & Assert
            var ex = Assert.ThrowsAsync<Exception>(async () => await _playerQuest.StartQuest(questId));
            Assert.That(ex.Message, Does.Contain("Already Started Quest"));
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
                },
                // QuestDiary 초기화
                QuestDiary = new QuestDiary(playerId)
            };
            
            return playerInfo;
        }
    }
}