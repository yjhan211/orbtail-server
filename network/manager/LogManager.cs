using Reactive.Bindings.Extensions;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Formatting.Compact;
using Serilog.Formatting.Json;

namespace network
{
    public static class LogManager
    {
        static string server_type = "unknown";
        static Logger? logger;

        public static void Initialize(string server_type)
        {
            LogManager.server_type = server_type;

            logger = new LoggerConfiguration().MinimumLevel
                .Debug()
                .Enrich.WithProperty("server_type", LogManager.server_type)
                .Enrich.WithProperty("log_type", "info")
                .Enrich.WithProperty(
                    "@t_kst",
                    DateTimeOffset.UtcNow
                        .ToOffset(TimeSpan.FromHours(9))
                        .ToString("yyyy-MM-dd HH:mm:ss")
                )
                .WriteTo.Console(new CompactJsonFormatter())
                .CreateLogger();
        }

        public static void WriteInfoLog(string msg)
        {
            using (LogContext.PushProperty("log_type", "info"))
            {
                logger!.Information("{msg}", msg);
            }
        }

        public static void WriteDebugLog(string msg)
        {
            using (LogContext.PushProperty("log_type", "debug"))
            {
                logger!.Debug("{msg}", msg);
            }
        }

        public static void WriteErrorLog(Exception e)
        {
            using (LogContext.PushProperty("log_type", "error"))
            {
                logger!.Error("Exception: {@e}", e);
            }
        }

        // public static void WriteLoginLog(
        //     LoginType login_type,
        //     string access_token,
        //     PlayerInfo player_info,
        //     bool is_created
        // )
        // {
        //     string log_type = "login";
        //     if (!logger_map.TryGetValue(log_type, out var logger))
        //     {
        //         logger = CreateLogger(log_type);
        //         logger_map.Add(log_type, logger);
        //     }

        //     logger.Information(
        //         "login success. login_type:{login_type}, access_token:{access_token}, player_info:{@player_info}, is_created:{is_created}",
        //         login_type,
        //         access_token,
        //         player_info,
        //         is_created
        //     );
        // }
    }
}
