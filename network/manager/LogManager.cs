using Reactive.Bindings.Extensions;
using Serilog;
using Serilog.Core;
using Serilog.Formatting.Compact;
using Serilog.Formatting.Json;

namespace network
{
    public static class LogManager
    {
        static string server_type = "unknown";
        static Dictionary<string, Logger> logger_map = new();

        public static void Initialize(string server_type)
        {
            LogManager.server_type = server_type;
        }

        private static Logger CreateLogger(string log_type)
        {
            string logDirectory = Path.Combine("..", "logs", server_type, log_type);

            return new LoggerConfiguration().MinimumLevel
                .Debug()
                .WriteTo.Console(new CompactJsonFormatter())
                .WriteTo.File(
                    new CompactJsonFormatter(),
                    Path.Combine(logDirectory, ".log"),
                    rollingInterval: RollingInterval.Day
                )
                .CreateLogger();
        }

        public static void WriteInfoLog(string msg)
        {
            string log_type = "info";
            if (!logger_map.TryGetValue(log_type, out var logger))
            {
                logger = CreateLogger(log_type);
                logger_map.Add(log_type, logger);
            }

            logger.Information("info: {msg}", msg);
        }

        public static void WriteDebugLog(string msg)
        {
            string log_type = "debug";
            if (!logger_map.TryGetValue(log_type, out var logger))
            {
                logger = CreateLogger(log_type);
                logger_map.Add(log_type, logger);
            }

            logger.Information("debug: {msg}", msg);
        }

        public static void WriteErrorLog(Exception e)
        {
            string log_type = "error";
            if (!logger_map.TryGetValue(log_type, out var logger))
            {
                logger = CreateLogger(log_type);
                logger_map.Add(log_type, logger);
            }

            logger.Error("Exception: {@e}", e);
        }

        public static void WriteLoginLog(
            LoginType login_type,
            string access_token,
            PlayerInfo player_info,
            bool is_created
        )
        {
            string log_type = "login";
            if (!logger_map.TryGetValue(log_type, out var logger))
            {
                logger = CreateLogger(log_type);
                logger_map.Add(log_type, logger);
            }

            logger.Information(
                "login success. login_type:{login_type}, access_token:{access_token}, player_info:{@player_info}, is_created:{is_created}",
                login_type,
                access_token,
                player_info,
                is_created
            );
        }
    }
}
