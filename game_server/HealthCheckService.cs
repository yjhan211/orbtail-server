using game_server.admin;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using network.hosting;
using network.interfaces;
using Prometheus;

namespace game_server;

public class HealthCheckService(
    ILogger<HealthCheckService> logger,
    RedisConfiguration redisConfiguration,
    ServerReadinessState readinessState,
    GameServer gameServer) : IHostedService
{
    private WebApplication? _app;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();

        // ASP.NET Core HTTP 요청 라이프사이클 로그 차단 (메인 Serilog와 별개 파이프라인)
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

        builder.Services
            .AddHealthChecks()
            .AddRedis(redisConfiguration.ConnectionString, name: "redis", tags: ["ready"])
            .AddCheck(
                "server",
                () => readinessState.IsReady
                    ? HealthCheckResult.Healthy()
                    : HealthCheckResult.Unhealthy(readinessState.Status),
                tags: ["ready"]);
        builder.WebHost.UseUrls("http://*:8080");

        _app = builder.Build();

        _app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = _ => false
        });

        _app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready")
        });

        _app.MapMetrics();

        // 운영 어드민 endpoint 등록
        _app.MapAdminEndpoints(gameServer);

        logger.LogInformation("Health check service starting on port 8080");

        await _app.StartAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app != null)
        {
            await _app.StopAsync(cancellationToken);
            await _app.DisposeAsync();
        }
    }
}
