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
            logBuilder.Append(message);

            if (includeStackTrace)
            {
                var stackTrace = new StackTrace(true);
                logBuilder.AppendLine("\nStack Trace:");
                for (int i = 1; i < stackTrace.FrameCount; i++)
                {
                    StackFrame? frame = stackTrace.GetFrame(i);
                    if (frame == null) continue;

                    var method = frame.GetMethod();
                    if (method == null) continue;

                    string fileName = frame.GetFileName() ?? "Unknown File";
                    int lineNumber = frame.GetFileLineNumber();
                    string className = method.DeclaringType?.FullName ?? "Unknown Class";
                    string methodName = method.Name;

                    if (lineNumber > 0)
                    {
                        logBuilder.AppendLine($"   at {className}.{methodName} in {fileName}:line {lineNumber}");
                    }
                    else
                    {
                        logBuilder.AppendLine($"   at {className}.{methodName}");
                    }
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
