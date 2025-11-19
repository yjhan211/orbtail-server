using Moq;
using network.common;
using network.common.data.models;
using user_server.application.queries.handlers;
using user_server.application.queries.player;
using user_server.domain.player;
using user_server.infrastructure.network;
using Xunit;

namespace user_server.tests.unit.queries;

public class PlayerQueryHandlerTests
{
    private readonly Mock<GameSession> _mockSession;
    private readonly Mock<Player> _mockPlayer;
    private readonly PlayerQueryHandler _handler;
    private readonly PlayerInfo _testPlayerInfo;
    private const long TestPlayerId = 12345L;

    public PlayerQueryHandlerTests()
    {
        _mockSession = new Mock<GameSession>();
        _mockPlayer = new Mock<Player>();

        _testPlayerInfo = new PlayerInfo
        {
            PlayerId = TestPlayerId,
            Name = "TestPlayer",
            Hp = 1000
        };

        Func<long, GameSession?> getSession = (playerId) =>
            playerId == TestPlayerId ? _mockSession.Object : null;

        _handler = new PlayerQueryHandler(getSession);

        // Setup session to return mock player
        _mockSession.Setup(s => s.Player).Returns(_mockPlayer.Object);
        _mockPlayer.Setup(p => p.PlayerInfo).Returns(_testPlayerInfo);
    }

    [Fact]
    public async Task HandleAsync_GetPlayerInfoQuery_ReturnsPlayerInfo()
    {
        // Arrange
        var query = new GetPlayerInfoQuery(TestPlayerId);

        // Act
        var result = await _handler.HandleAsync(query);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.PlayerInfo);
        Assert.Equal(TestPlayerId, result.PlayerInfo.PlayerId);
        Assert.Equal("TestPlayer", result.PlayerInfo.Name);
        Assert.Equal(1000, result.PlayerInfo.Hp);
    }

    [Fact]
    public async Task HandleAsync_GetPlayerInfoQuery_InvalidPlayerId_ReturnsNull()
    {
        // Arrange
        var query = new GetPlayerInfoQuery(99999L); // Invalid player ID

        // Act
        var result = await _handler.HandleAsync(query);

        // Assert
        Assert.NotNull(result);
        Assert.Null(result.PlayerInfo);
    }

    [Fact]
    public async Task HandleAsync_GetPlayerQuestsQuery_ReturnsQuests()
    {
        // Arrange
        var questDiary = new QuestDiary(TestPlayerId);
        questDiary.QuestDict[1001] = new QuestInfo { QuestId = 1001 };
        questDiary.QuestDict[1002] = new QuestInfo { QuestId = 1002 };

        _testPlayerInfo.QuestDiary = questDiary;

        var query = new GetPlayerQuestsQuery(TestPlayerId);

        // Act
        var result = await _handler.HandleAsync(query);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Quests);
        Assert.Equal(2, result.Quests.Count);
        Assert.Contains(result.Quests, q => q.QuestId == 1001);
        Assert.Contains(result.Quests, q => q.QuestId == 1002);
    }

    [Fact]
    public async Task HandleAsync_GetPlayerItemsQuery_ReturnsItems()
    {
        // Arrange
        var inventory = new InventoryInfo(InventoryOwnerType.PLAYER, TestPlayerId);
        inventory.ItemDict[100] = new ItemInfo { ItemUid = 100, ItemId = 5001 };
        inventory.ItemDict[101] = new ItemInfo { ItemUid = 101, ItemId = 5002 };

        _testPlayerInfo.InventoryInfo = inventory;

        var query = new GetPlayerItemsQuery(TestPlayerId);

        // Act
        var result = await _handler.HandleAsync(query);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Items);
        Assert.Equal(2, result.Items.Count);
        Assert.Contains(result.Items, i => i.ItemUid == 100);
        Assert.Contains(result.Items, i => i.ItemUid == 101);
    }

    [Fact]
    public async Task HandleAsync_GetPlayerMailsQuery_ReturnsMails()
    {
        // Arrange
        var mailBox = new MailBox(TestPlayerId);
        mailBox.MailDict[200] = new MailInfo { MailUid = 200 };
        mailBox.MailDict[201] = new MailInfo { MailUid = 201 };

        _testPlayerInfo.MailBox = mailBox;

        var query = new GetPlayerMailsQuery(TestPlayerId);

        // Act
        var result = await _handler.HandleAsync(query);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Mails);
        Assert.Equal(2, result.Mails.Count);
        Assert.Contains(result.Mails, m => m.MailUid == 200);
        Assert.Contains(result.Mails, m => m.MailUid == 201);
    }
}
