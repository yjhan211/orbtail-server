using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Prometheus;

namespace game_server;

public class HealthCheckService(ILogger<HealthCheckService> logger, IConfiguration configuration) : IHostedService
{
    private readonly string _redisEndpoints = configuration["redisEndPoints"] ?? "localhost:6379";
    private WebApplication? _app;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();

        builder.Services.AddHealthChecks().AddRedis(_redisEndpoints, name: "redis", tags: ["ready"]);
        builder.WebHost.UseUrls($"http" + $"://*:8080");

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

        logger.LogInformation("Health check service starting on port 8080");

        _ = _app.RunAsync(cancellationToken);

        return Task.CompletedTask;
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
