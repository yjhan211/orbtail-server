using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using network.hosting;
using network.interfaces;
using Prometheus;

namespace user_server;

public class HealthCheckService(
    ILogger<HealthCheckService> logger,
    RedisConfiguration redisConfiguration,
    ServerReadinessState readinessState)
    : IHostedService
{
    private WebApplication? _app;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();

        builder.Services.AddHealthChecks()
            .AddRedis(redisConfiguration.ConnectionString, "redis", tags: ["ready"])
            .AddCheck(
                "server",
                () => readinessState.IsReady
                    ? HealthCheckResult.Healthy()
                    : HealthCheckResult.Unhealthy(readinessState.Status),
                tags: ["ready"]);

        builder.WebHost.UseUrls("http://*:8080");

        _app = builder.Build();

        _app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });

        _app.MapHealthChecks("/health/ready",
            new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

        _app.MapMetrics();

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
