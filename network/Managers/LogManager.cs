using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace network.managers;

public class LogManager(string serverType, int serverId, ILogger<LogManager> logger)
    : ILogger
{
    private readonly string _serverType = serverType;
    private readonly int _serverId = serverId;

    public void WriteInfoLog(string message)
    {
        logger.LogInformation(message);
    }

    public void WriteDebugLog(string message, bool includeStackTrace = false)
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
        logger.LogDebug(logBuilder.ToString());
    }

    public void WriteErrorLog(Exception exception)
    {
        logger.LogError(exception, exception.Message);
    }

    public void WriteErrorLog(string message)
    {
        logger.LogError(message);
    }

    // ILogger 인터페이스 구현
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        // 내부 로거를 통해 범위 전달
        return logger.BeginScope(state);
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logger.IsEnabled(logLevel);
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        logger.Log(logLevel, eventId, state, exception, formatter);
    }
}