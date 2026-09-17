using Microsoft.Extensions.Logging;

namespace server_tests;

internal static class TestLoggerExtensions
{
    // 기존 로그 수집기와 동기화 동작을 유지하면서 생성자에 필요한 범주 타입을 제공한다.
    public static ILogger<T> For<T>(this ILogger logger) => new TypedLogger<T>(logger);

    private sealed class TypedLogger<T>(ILogger logger) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => logger.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => logger.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => logger.Log(logLevel, eventId, state, exception, formatter);
    }
}
