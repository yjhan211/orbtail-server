using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace network.managers;

public class LogManager : ILogger
{
    private static ILogger? _staticLogger;
    private readonly ILogger _logger;

    public LogManager(string serverType, int serverId, ILogger<LogManager> logger)
    {
        _logger = logger;
        _staticLogger ??= logger;
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

    public void WriteDebugLogWithStackTrace(string message, bool includeStackTrace = false)
    {
        var logBuilder = new StringBuilder();
        logBuilder.Append(message);
        if (includeStackTrace)
        {
            var stackTrace = new StackTrace(true);
            logBuilder.AppendLine("\nStack Trace:");
            for (var i = 1; i < stackTrace.FrameCount; i++)
            {
                var frame = stackTrace.GetFrame(i);
                if (frame == null) continue;
                var method = frame.GetMethod();
                if (method == null) continue;
                var fileName = frame.GetFileName() ?? "Unknown File";
                var lineNumber = frame.GetFileLineNumber();
                var className = method.DeclaringType?.FullName ?? "Unknown Class";
                var methodName = method.Name;
                if (lineNumber > 0)
                    logBuilder.AppendLine($"   at {className}.{methodName} in {fileName}:line {lineNumber}");
                else
                    logBuilder.AppendLine($"   at {className}.{methodName}");
            }
        }
        _logger.LogDebug(logBuilder.ToString());
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

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        _logger.Log(logLevel, eventId, state, exception, formatter);
    }
}
