using Microsoft.Extensions.Logging;
using Moq;
using network.common;
using network.common.data.helpers;
using network.common.data.models;
using network.helpers;
using network.interfaces;
using RedLockNet;
using StackExchange.Redis;
using user_server.players;
using user_server.tests.components;

namespace user_server.tests.controllers
{
    [TestFixture]
    public class PlayerMailBoxTests
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
        private PlayerMailBox _playerMailBox;
        
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
            
            // StringIncrementAsync 설정 (메일 ID 생성용)
            _mockCacheHelper
                .Setup(x => x.StringIncrementAsync("mail_uid_key", 1))
                .ReturnsAsync(1001);
            
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
                .ReturnsAsync(new List<HashEntry>().ToArray());
                
            // PlayerMailBox 인스턴스 생성
            _playerMailBox = new PlayerMailBox(_gameUser, _playerInfo);
        }
        
        [Test]
        public async Task CreateMail_WithValidInput_ReturnsMailInfo()
        {
            // Arrange
            var mailId = 100001;
            var items = new List<(int, int)> { (2001, 10), (3001, 5) };
            
            // Act
            var result = await PlayerMailBox.CreateMail(_mockCacheHelper.Object, mailId, items);
            
            // Assert
            Assert.That(result, Is.Not.Null);
            Assert.That(result.MailId, Is.EqualTo(mailId));
            Assert.That(result.Items.Count, Is.EqualTo(2));
            Assert.That(result.Items[0].Item1, Is.EqualTo(2001));
            Assert.That(result.Items[0].Item2, Is.EqualTo(10));
            Assert.That(result.Items[1].Item1, Is.EqualTo(3001));
            Assert.That(result.Items[1].Item2, Is.EqualTo(5));
            
            // StringIncrementAsync가 호출되었는지 검증
            _mockCacheHelper.Verify(
                x => x.StringIncrementAsync("mail_uid_key", It.IsAny<int>()),
                Times.Once
            );
        }
        
        [Test]
        public async Task CreateMail_WithNoItems_ReturnsMailInfoWithEmptyItems()
        {
            // Arrange
            var mailId = 100001;
            
            // Act
            var result = await PlayerMailBox.CreateMail(_mockCacheHelper.Object, mailId);
            
            // Assert
            Assert.That(result, Is.Not.Null);
            Assert.That(result.MailId, Is.EqualTo(mailId));
            Assert.That(result.Items, Is.Empty);
            
            // StringIncrementAsync가 호출되었는지 검증
            _mockCacheHelper.Verify(
                x => x.StringIncrementAsync("mail_uid_key", It.IsAny<int>()),
                Times.Once
            );
        }
        
        [Test]
        public async Task SendMail_WithValidInput_AddsMailToMailBox()
        {
            // Arrange
            var mailId = 100001;
            var items = new List<(int, int)> { (2001, 10) };
            var mailInfo = new MailInfo(1001, mailId, items);
            
            // 시작 전 메일 개수 기록
            int initialCount = _playerInfo.MailBox.MailDict.Count;
            
            // Act
            await _playerMailBox.SendMail(mailInfo);
            
            // Assert
            // MailBox에 메일이 추가되었는지 확인
            Assert.That(_playerInfo.MailBox.MailDict.Count, Is.EqualTo(initialCount + 1));
            Assert.That(_playerInfo.MailBox.MailDict.ContainsKey(1001), Is.True);
            
            // MailBox.Save가 호출되었는지 검증
            _mockCacheHelper.Verify(
                x => x.HashSetAsync(
                    "MailBox",  // 실제 사용된 문자열로 수정
                    1001L,      // PlayerId
                    It.IsAny<byte[]>(), 
                    It.IsAny<int>()
                ), 
                Times.Once
            );
        }
        
        [Test]
        public async Task ReceiveMail_WithValidMail_UpdatesInventoryAndSendsPacket()
        {
            // Arrange
            var mailId = 100001;
            var mailUid = 1001L;
            var items = new List<(int, int)> { (101000001, 1) };
            var mailInfo = new MailInfo(mailUid, mailId, items);
            
            // 메일 추가
            _playerInfo.MailBox.AddMail(mailInfo);
            
            // 인벤토리 초기 개수
            int initialItemCount = _playerInfo.InventoryInfo.ItemDict.Count;
            
            // CreateItem 모킹
            var item = new ItemInfo { ItemId = 101000001, Count = 1 };
            _mockCacheHelper
                .Setup(x => x.StringIncrementAsync(It.IsAny<string>(), It.IsAny<int>()))
                .ReturnsAsync(101000001);
            
            // UserToken 패킷 전송 상태 초기화
            _userToken.PacketWasSent = false;
            
            // Act
            var request = new C_TO_U_MAIL_RECEIVE { MailUid = mailUid };
            await _playerMailBox.ReceiveMail(request);
            
            // Assert
            // 패킷이 전송되었는지 검증
            Assert.That(_userToken.PacketWasSent, Is.True, "메일 수신 패킷이 전송되어야 함");
            
            // 인벤토리에 아이템이 추가되었는지 확인
            Assert.That(_playerInfo.InventoryInfo.ItemDict.Count, Is.GreaterThan(initialItemCount), "인벤토리에 아이템이 추가되어야 함");
            
            // MailBox.Save가 호출되었는지 검증
            _mockCacheHelper.Verify(
                x => x.HashSetAsync(
                    "MailBox",  // 실제 사용된 문자열로 수정
                    1001L,      // PlayerId
                    It.IsAny<byte[]>(), 
                    It.IsAny<int>()
                ), 
                Times.Once
            );
        }
        
        [Test]
        public void ReceiveMail_WithNonexistentMail_ThrowsException()
        {
            // Arrange
            var mailUid = 9999L;
            var request = new C_TO_U_MAIL_RECEIVE { MailUid = mailUid };
            
            // Act & Assert
            var ex = Assert.ThrowsAsync<Exception>(async () => await _playerMailBox.ReceiveMail(request));
            Assert.That(ex.Message, Does.Contain("Invalid Mail Info"));
        }
        
        [Test]
        public void ReceiveMail_WithAlreadyRewardedMail_ThrowsException()
        {
            // Arrange
            var mailId = 100001;
            var mailUid = 1001L;
            var items = new List<(int, int)> { (2001, 10) };
            var mailInfo = new MailInfo(mailUid, mailId, items);
            
            // 메일 추가 및 이미 보상을 받은 상태로 설정
            mailInfo.State = MailState.REWARDED;
            _playerInfo.MailBox.AddMail(mailInfo);
            
            var request = new C_TO_U_MAIL_RECEIVE { MailUid = mailUid };
            
            // Act & Assert
            var ex = Assert.ThrowsAsync<Exception>(async () => await _playerMailBox.ReceiveMail(request));
            Assert.That(ex.Message, Does.Contain("Already Rewarded"));
        }
        
        [Test]
        public void SendCurrentMails_WithNoMails_DoesNotSendPacket()
        {
            // Arrange
            // MailBox는 비어 있는 상태로 설정됨
            _playerInfo.MailBox.MailDict.Clear();
            
            _userToken.PacketWasSent = false;
            
            // Act
            _playerMailBox.SendCurrentMails();
            
            // Assert
            // 메일이 없으므로 패킷이 전송되지 않아야 함
            Assert.That(_userToken.PacketWasSent, Is.False, "메일이 없으면 패킷이 전송되지 않아야 함");
        }
        
        [Test]
        public void SendCurrentMails_WithMails_SendsPackets()
        {
            // Arrange
            // 메일 데이터 준비
            var mail1 = new MailInfo(1001, 100001, new List<(int, int)> { (2001, 10) });
            var mail2 = new MailInfo(1002, 100002, new List<(int, int)> { (3001, 5) });
            _playerInfo.MailBox.AddMail(mail1);
            _playerInfo.MailBox.AddMail(mail2);
            
            _userToken.PacketWasSent = false;
            
            // Act
            _playerMailBox.SendCurrentMails();
            
            // Assert
            // 메일 목록 패킷이 전송되어야 함
            Assert.That(_userToken.PacketWasSent, Is.True, "메일 목록 패킷이 전송되어야 함");
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
                // MailBox 초기화
                MailBox = new MailBox(playerId),
                // InventoryInfo 초기화
                InventoryInfo = new InventoryInfo(InventoryOwnerType.PLAYER, playerId)
            };
            
            return playerInfo;
        }
    }
}