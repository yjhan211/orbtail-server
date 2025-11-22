using Moq;
using user_server.application.events;
using user_server.domain.events;
using user_server.infrastructure.events;
using Xunit;

namespace user_server.tests.unit.events;

public class EventDispatcherTests
{
    private readonly EventDispatcher _dispatcher;

    public EventDispatcherTests()
    {
        _dispatcher = new EventDispatcher();
    }

    [Fact]
    public async Task DispatchAsync_CallsRegisteredHandler()
    {
        // Arrange
        var mockHandler = new Mock<IEventHandler<QuestCompletedEvent>>();
        var testEvent = new QuestCompletedEvent(
            PlayerId: 12345L,
            QuestId: 1001,
            OccurredAt: DateTime.UtcNow
        );

        _dispatcher.RegisterHandler(mockHandler.Object);

        // Act
        await _dispatcher.DispatchAsync(testEvent);

        // Assert
        mockHandler.Verify(h => h.HandleAsync(testEvent), Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_CallsMultipleHandlers()
    {
        // Arrange
        var mockHandler1 = new Mock<IEventHandler<QuestCompletedEvent>>();
        var mockHandler2 = new Mock<IEventHandler<QuestCompletedEvent>>();
        var testEvent = new QuestCompletedEvent(
            PlayerId: 12345L,
            QuestId: 1001,
            OccurredAt: DateTime.UtcNow
        );

        _dispatcher.RegisterHandler(mockHandler1.Object);
        _dispatcher.RegisterHandler(mockHandler2.Object);

        // Act
        await _dispatcher.DispatchAsync(testEvent);

        // Assert
        mockHandler1.Verify(h => h.HandleAsync(testEvent), Times.Once);
        mockHandler2.Verify(h => h.HandleAsync(testEvent), Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_WithNoHandlers_DoesNotThrow()
    {
        // Arrange
        var testEvent = new QuestCompletedEvent(
            PlayerId: 12345L,
            QuestId: 1001,
            OccurredAt: DateTime.UtcNow
        );

        // Act & Assert - should not throw
        await _dispatcher.DispatchAsync(testEvent);
    }

    [Fact]
    public async Task DispatchAsync_HandlerThrowsException_ContinuesExecution()
    {
        // Arrange
        var mockHandler1 = new Mock<IEventHandler<QuestCompletedEvent>>();
        var mockHandler2 = new Mock<IEventHandler<QuestCompletedEvent>>();
        var testEvent = new QuestCompletedEvent(
            PlayerId: 12345L,
            QuestId: 1001,
            OccurredAt: DateTime.UtcNow
        );

        mockHandler1.Setup(h => h.HandleAsync(It.IsAny<QuestCompletedEvent>()))
            .ThrowsAsync(new Exception("Test exception"));

        _dispatcher.RegisterHandler(mockHandler1.Object);
        _dispatcher.RegisterHandler(mockHandler2.Object);

        // Act - should not throw
        await _dispatcher.DispatchAsync(testEvent);

        // Assert - both handlers were called despite first one throwing
        mockHandler1.Verify(h => h.HandleAsync(testEvent), Times.Once);
        mockHandler2.Verify(h => h.HandleAsync(testEvent), Times.Once);
    }

    [Fact]
    public async Task DispatchAsync_DifferentEventTypes_OnlyCallsMatchingHandlers()
    {
        // Arrange
        var questHandler = new Mock<IEventHandler<QuestCompletedEvent>>();
        var itemHandler = new Mock<IEventHandler<ItemReceivedEvent>>();

        var questEvent = new QuestCompletedEvent(
            PlayerId: 12345L,
            QuestId: 1001,
            OccurredAt: DateTime.UtcNow
        );

        _dispatcher.RegisterHandler(questHandler.Object);
        _dispatcher.RegisterHandler(itemHandler.Object);

        // Act
        await _dispatcher.DispatchAsync(questEvent);

        // Assert
        questHandler.Verify(h => h.HandleAsync(questEvent), Times.Once);
        itemHandler.Verify(h => h.HandleAsync(It.IsAny<ItemReceivedEvent>()), Times.Never);
    }
}
