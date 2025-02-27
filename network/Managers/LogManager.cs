using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Compact;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace network.managers;

public class LogManager : ILogger
{
    private readonly ILogger<LogManager> _logger;
    private readonly SerilogLoggerProvider _loggerProvider;

    public LogManager(string serverType, int serverId)
    {
        var serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.WithProperty("serverType", serverType)
            .Enrich.WithProperty("serverId", serverId)
            .WriteTo.Console(new CompactJsonFormatter())
            .CreateLogger();

        _loggerProvider = new SerilogLoggerProvider(serilogLogger);
        var factory = new SerilogLoggerFactory(serilogLogger);
        _logger = factory.CreateLogger<LogManager>();
    }

    public void WriteInfoLog(string message)
    {
        _logger.LogInformation(message);
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

        _logger.LogDebug(logBuilder.ToString());
    }

    public void WriteErrorLog(Exception exception)
    {
        _logger.LogError(exception, exception.Message);
    }

    public void WriteErrorLog(string message)
    {
        _logger.LogError(message);
    }

    // ILogger 인터페이스 구현
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        // Serilog의 LoggerProvider를 통해 스코프 시작
        return _loggerProvider.CreateLogger("LogManager").BeginScope(state);
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        // 모든 로그 레벨 활성화
        return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        // 내부 로거에 위임
        _logger.Log(logLevel, eventId, state, exception, formatter);
    }
}