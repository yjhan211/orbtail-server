using Microsoft.Extensions.Logging;
using Moq;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;
using network.helpers;
using network.interfaces;
using user_server.controllers;
using user_server.tests.components;

namespace user_server.tests.controllers;

[TestFixture]
public class PlayerControllerTests
{
    // 의존성 모킹
    private ICacheHelper _mockCacheHelper;
    private IRedLockFactory _mockRedLockFactory;
    private INatsClient _mockNatsClient;
    private ILogger _mockLogger;
    
    private TestChatController _mockChatController;
    private TestUserToken _userToken;
    private TestGameUser _gameUser;
    
    // 테스트 대상 객체
    private PlayerController _playerController;
    private PlayerInfo _playerInfo;
    
    [SetUp]
    public void Setup()
    {
        // 게임 데이터 초기화
        GameDataHelper.Initialize();
        MapHelper.Initialize(2);
        
        // 개별 의존성 모킹
        var mockCacheHelper = new Mock<ICacheHelper>();
        var mockRedLockFactory = new Mock<IRedLockFactory>();
        var mockNatsClient = new Mock<INatsClient>();
        var mockLogger = new Mock<ILogger>();
        
        _mockCacheHelper = mockCacheHelper.Object;
        _mockRedLockFactory = mockRedLockFactory.Object;
        _mockNatsClient = mockNatsClient.Object;
        _mockLogger = mockLogger.Object;
        _mockChatController = new TestChatController();
        _userToken = new TestUserToken();
        
        // 기본 테스트 데이터 준비 (CommonMap)
        _playerInfo = CreateTestPlayerInfo(MapId.Library);
        
        // GameUser 직접 생성 (모킹 대신 테스트용 하위 클래스 사용)
        _gameUser = new TestGameUser(
            _userToken,
            _mockRedLockFactory,
            _mockNatsClient,
            _mockLogger,
            _mockCacheHelper,
            user => { },  // 콜백 함수
            _mockChatController
        );
        
        // CacheHelper 설정
        mockCacheHelper
            .Setup(x => x.HashSetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<int>()))
            .ReturnsAsync(true);
        
        // PlayerController 인스턴스 생성
        _playerController = new PlayerController(_gameUser, _playerInfo);
    }
    
    [Test]
    public void Constructor_InitializesCorrectly()
    {
        // Assert
        Assert.That(_playerController, Is.Not.Null);
        Assert.That(_playerController.PlayerId, Is.EqualTo(_playerInfo.PlayerId));
        Assert.That(_playerController.PlayerName, Is.EqualTo(_playerInfo.Name));
        Assert.That(_playerController.ObjectKey, Is.EqualTo(_playerInfo.ObjectInfo.GetGameObjectKey()));
        
        // PlayerInfo 상태가 초기화되었는지 확인
        Assert.That(_playerInfo.State, Is.EqualTo(PlayerState.IDLE));
        Assert.That(_playerInfo.ObjectInfo.CurrentCell, Is.EqualTo(_playerInfo.ObjectInfo.TargetCell));
    }
    
    [Test]
    public async Task SetName_UpdatesPlayerNameAndBroadcasts()
    {
        // Arrange
        var request = new C_TO_U_SET_NAME
        {
            Name = "TestPlayer123"
        };

        // Act
        await _playerController.SetName(request);
        
        // Assert
        Assert.That(_playerInfo.Name, Is.EqualTo(request.Name));
        
        // 응답 패킷이 전송되었는지 확인
        Assert.That(_userToken.PacketWasSent, Is.True, "Response packet should have been sent");
        
        // BroadcastUpdateInfo가 호출되었는지 확인
        Assert.That(_gameUser.BroadcastUpdateInfoCalled, Is.True, "BroadcastUpdateInfo should have been called");
    }
    
    [Test]
    public async Task SocialAction_SitGround_TogglesPlayerState()
    {
        // Arrange - 초기 상태는 IDLE
        Assert.That(_playerInfo.State, Is.EqualTo(PlayerState.IDLE));
        
        // Act - SitGround 액션
        var request = new C_TO_U_SOCIAL_ACTION()
        {
            SocialActionType = SocialActionType.SITGROUND
        };
        await _playerController.SocialAction(request);
        
        // Assert - 상태가 SITGROUND로 변경됨
        Assert.That(_playerInfo.State, Is.EqualTo(PlayerState.SITGROUND));
        
        // Act - 다시 SitGround 액션
        await _playerController.SocialAction(request);
        
        // Assert - 상태가 다시 IDLE로 변경됨
        Assert.That(_playerInfo.State, Is.EqualTo(PlayerState.IDLE));
        
        // BroadcastUpdateInfo가 호출되었는지 확인 (총 2번)
        Assert.That(_gameUser.BroadcastUpdateInfoCallCount, Is.EqualTo(2), 
            "BroadcastUpdateInfo should have been called twice");
    }
    
    [Test]
    public async Task SocialAction_NonSitGround_BroadcastsOnly()
    {
        // Arrange - 초기 상태
        PlayerState initialState = _playerInfo.State;
        
        // Act - 다른 소셜 액션 (예: Laugh)
        var request = new C_TO_U_SOCIAL_ACTION{
            SocialActionType = SocialActionType.LAUGH
        };
        await _playerController.SocialAction(request);
        
        // Assert - 상태가 변경되지 않아야 함
        Assert.That(_playerInfo.State, Is.EqualTo(initialState));
        
        // BroadcastSocialAction이 호출되었는지 확인
        Assert.That(_gameUser.BroadcastSocialActionCalled, Is.True, "BroadcastSocialAction should have been called");
        Assert.That(_gameUser.LastSocialActionType, Is.EqualTo(SocialActionType.LAUGH));
    }
    
    [Test]
    public async Task UpdateBoost_TogglesBoostState()
    {
        // Arrange - 초기 부스트 상태 확인
        Assert.That(_playerInfo.Boosts.Contains(BoostType.SPEED), Is.False);
        
        // Act - 부스트 추가
        var request = new C_TO_U_BOOST
        {
            BoostType = BoostType.SPEED,
        };
        await _playerController.UpdateBoost(request);
        
        // Assert - 부스트가 추가되었는지 확인
        Assert.That(_playerInfo.Boosts.Contains(BoostType.SPEED), Is.True);
        
        // Act - 같은 부스트 다시 요청 (제거)
        await _playerController.UpdateBoost(request);
        
        // Assert - 부스트가 제거되었는지 확인
        Assert.That(_playerInfo.Boosts.Contains(BoostType.SPEED), Is.False);
        
        // BroadcastUpdateInfo가 호출되었는지 확인 (총 2번)
        Assert.That(_gameUser.BroadcastUpdateInfoCallCount, Is.EqualTo(2),
            "BroadcastUpdateInfo should have been called twice");
    }
    
    [Test]
    public void IsInvalidAction_ReturnsTrueForActionProtocolsInActionState()
    {
        // Arrange - 액션 상태로 설정
        _playerInfo.State = PlayerState.CRAFT_1;
        
        // Act & Assert - 액션 프로토콜에 대해 true 반환해야 함
        Assert.That(_playerController.IsInvalidAction(Protocol.C_TO_U_MOVE), Is.True);
        Assert.That(_playerController.IsInvalidAction(Protocol.C_TO_U_WEAR_ITEM), Is.True);
        
        // 액션 프로토콜이 아닌 경우 false 반환해야 함
        Assert.That(_playerController.IsInvalidAction(Protocol.C_TO_U_HEART_BEAT), Is.False);
    }
    
    [Test]
    public async Task PlayerController_WithCommonMap_HandlesCorrectly()
    {
        // Arrange - CommonMap용 PlayerInfo 생성
        var commonMapInfo = CreateTestPlayerInfo(MapId.Library);
        var commonMapController = new PlayerController(_gameUser, commonMapInfo);
        
        // Act - 소셜 액션 수행
        var request = new C_TO_U_SOCIAL_ACTION
        {
            SocialActionType = SocialActionType.LAUGH
        };
        await commonMapController.SocialAction(request);
        using (Assert.EnterMultipleScope())
        {

            // Assert
            Assert.That(_gameUser.BroadcastSocialActionCalled, Is.True, "BroadcastSocialAction should have been called");
            Assert.That(_gameUser.LastSocialActionType, Is.EqualTo(SocialActionType.LAUGH));

            // 맵 정보 확인
            Assert.That(commonMapInfo.ObjectInfo.MapId, Is.EqualTo(MapId.Library));
            Assert.That(GameMapData.IsCommonMap(commonMapInfo.ObjectInfo.MapId), Is.True, "Should be a CommonMap");
        }
    }
    
    [Test]
    public async Task PlayerController_WithInstanceMap_HandlesCorrectly()
    {
        // Arrange - InstanceMap용 PlayerInfo 생성
        var instanceMapInfo = CreateTestPlayerInfo(MapId.TutorialLibrary);
        var instanceMapController = new PlayerController(_gameUser, instanceMapInfo);
        _gameUser.ResetTrackers(); // 이전 호출 기록 초기화
        
        // Act - 소셜 액션 수행
        var request = new C_TO_U_SOCIAL_ACTION
        {
            SocialActionType = SocialActionType.LAUGH
        };
        await instanceMapController.SocialAction(request);
        using (Assert.EnterMultipleScope())
        {

            // Assert
            Assert.That(_gameUser.BroadcastSocialActionCalled, Is.True, "BroadcastSocialAction should have been called");
            Assert.That(_gameUser.LastSocialActionType, Is.EqualTo(SocialActionType.LAUGH));

            // 맵 정보 확인
            Assert.That(instanceMapInfo.ObjectInfo.MapId, Is.EqualTo(MapId.TutorialLibrary));
            Assert.That(GameMapData.IsCommonMap(instanceMapInfo.ObjectInfo.MapId), Is.False, "Should be an InstanceMap");
        }
    }
    
    private PlayerInfo CreateTestPlayerInfo(MapId mapId)
    {
        var playerId = 1001;
        var playerInfo = new PlayerInfo(playerId, false)
        {
            Name = "TestPlayer",
            ObjectInfo =
            {
                MapId = mapId,
                ObjectType = ObjectType.PLAYER,
                CurrentCell = new Cell(10, 10),
                TargetCell = new Cell(10, 10),
                MapSubId = GameMapData.IsCommonMap(mapId) ? 0 : playerId
            }
        };

        return playerInfo;
    }
}