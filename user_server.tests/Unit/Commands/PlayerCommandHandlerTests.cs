using Moq;
using network.common.data.models;
using user_server.application.commands.handlers;
using user_server.application.commands.player;
using user_server.domain.player;
using user_server.infrastructure.network;
using Xunit;

namespace user_server.tests.unit.commands;

public class PlayerCommandHandlerTests
{
    private readonly Mock<GameSession> _mockSession;
    private readonly Mock<Player> _mockPlayer;
    private readonly PlayerCommandHandler _handler;
    private const long TestPlayerId = 12345L;

    public PlayerCommandHandlerTests()
    {
        _mockSession = new Mock<GameSession>();
        _mockPlayer = new Mock<Player>();

        Func<long, GameSession?> getSession = (playerId) =>
            playerId == TestPlayerId ? _mockSession.Object : null;

        _handler = new PlayerCommandHandler(getSession);

        // Setup session to return mock player
        _mockSession.Setup(s => s.Player).Returns(_mockPlayer.Object);
    }

    [Fact]
    public async Task HandleAsync_MoveCommand_CallsPlayerRequestMove()
    {
        // Arrange
        var moveData = new C_TO_U_MOVE();
        var command = new MoveCommand(TestPlayerId, moveData);

        // Act
        await _handler.HandleAsync(command);

        // Assert
        _mockPlayer.Verify(p => p.RequestMove(moveData), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WearItemCommand_CallsPlayerWearItem()
    {
        // Arrange
        var wearData = new C_TO_U_WEAR_ITEM { ItemUidList = new List<long> { 1, 2, 3 } };
        var command = new WearItemCommand(TestPlayerId, wearData);

        // Act
        await _handler.HandleAsync(command);

        // Assert
        _mockPlayer.Verify(p => p.WearItem(wearData), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_UseItemCommand_CallsPlayerUseItem()
    {
        // Arrange
        var useData = new C_TO_U_USE_ITEM { ItemUid = 100 };
        var command = new UseItemCommand(TestPlayerId, useData);

        // Act
        await _handler.HandleAsync(command);

        // Assert
        _mockPlayer.Verify(p => p.UseItem(useData), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_InvalidPlayerId_DoesNotThrow()
    {
        // Arrange
        var moveData = new C_TO_U_MOVE();
        var command = new MoveCommand(99999L, moveData); // Invalid player ID

        // Act & Assert - should not throw
        await _handler.HandleAsync(command);
    }

    [Fact]
    public async Task HandleAsync_CompleteQuestCommand_CallsPlayerCompleteQuest()
    {
        // Arrange
        var questData = new C_TO_U_QUEST_SUCCESS { QuestId = 1001 };
        var command = new CompleteQuestCommand(TestPlayerId, questData);

        // Act
        await _handler.HandleAsync(command);

        // Assert
        _mockPlayer.Verify(p => p.CompleteQuest(questData), Times.Once);
    }
}
