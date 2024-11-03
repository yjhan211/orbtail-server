using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Compact;

namespace network.managers;

public class LogManager
{
    private readonly ILogger<LogManager> _logger;

    public LogManager(string serverType, int serverId)
    {
        var serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.WithProperty("serverType", serverType)
            .Enrich.WithProperty("serverId", serverId)
            .WriteTo.Console(new CompactJsonFormatter())
            .CreateLogger();

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
}