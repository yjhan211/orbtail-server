using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using System.Diagnostics;
using System.Text;

namespace network.managers
{
    public class LogManager
    {
        private readonly ILogger<LogManager> _logger;

        public LogManager(string serverType, int serverId)
        {
            var serilogLogger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .Enrich.WithProperty("serverType", serverType)
                .Enrich.WithProperty("serverId", serverId)
                .Enrich.WithProperty("@t_kst", () => DateTimeOffset.UtcNow
                    .ToOffset(TimeSpan.FromHours(9))
                    .ToString("yyyy-MM-dd HH:mm:ss"))
                .WriteTo.Console(new Serilog.Formatting.Compact.CompactJsonFormatter())
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
            if (includeStackTrace)
            {
                var stackTrace = new StackTrace(true);
                logBuilder.AppendLine("Stack Trace:");
                for (int i = 1; i < stackTrace.FrameCount; i++)
                {
                    StackFrame? frame = stackTrace.GetFrame(i);
                    if (frame == null)
                    {
                        continue;
                    }
                    logBuilder.AppendLine($"in {frame.GetFileName()}:line {frame.GetFileLineNumber()}");
                }
            }

            _logger.LogDebug(logBuilder.ToString());
        }
        public void WriteErrorLog(Exception exception)
        {
            _logger.LogError(exception, exception.Message);
        }
    }
}
