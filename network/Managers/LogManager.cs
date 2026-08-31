using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace network.managers;

public class LogManager : ILogger
{
    private static ILogger? _staticLogger;
    private readonly ILogger _logger;

    public LogManager(ILogger<LogManager> logger)
    {
        _logger = logger;
        _staticLogger ??= logger;
    }

    // ILogger 인터페이스 구현
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return _logger.BeginScope(state);
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return _logger.IsEnabled(logLevel);
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _logger.Log(logLevel, eventId, state, exception, formatter);
    }

    public static void WriteInfoLog(string message)
    {
        _staticLogger?.LogInformation(message);
    }

    public static void WriteDebugLog(string message)
    {
        _staticLogger?.LogDebug(message);
    }

    public static void WriteErrorLog(string message)
    {
        _staticLogger?.LogError(message);
    }

    public static void WriteErrorLog(Exception exception)
    {
        _staticLogger?.LogError(exception, exception.Message);
    }
}
