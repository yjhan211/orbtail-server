using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace network.managers
{
    public class LogManager
    {
        private readonly ILogger<LogManager> _logger;

        public LogManager(string serverType, int serverId)
        {
            var serilogLogger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .Enrich.WithProperty("server_type", serverType)
                .Enrich.WithProperty("server_id", serverId)
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

        public void WriteDebugLog(string message)
        {
            _logger.LogDebug(message);
        }

        public void WriteErrorLog(Exception exception)
        {
            _logger.LogError(exception, exception.Message);
        }
    }
}
