using game_server.admin.dto;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

namespace game_server.admin;

/// <summary>
///     운영 어드민 HTTP endpoint 등록 서비스.
///     HealthCheckService의 WebApplication에 /admin/* 라우트를 추가한다.
/// </summary>
public static class AdminEndpoints
{
    /// <summary>
    ///     /admin/* 라우트를 앱에 등록한다.
    ///     호출 시점: HealthCheckService가 WebApplication을 구성한 뒤.
    /// </summary>
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app, GameServer gameServer)
    {
        // GET /admin/instances — 활성 인스턴스 목록
        app.MapGet("/admin/instances", () =>
        {
            var ids = gameServer.GetActiveInstanceIds();
            var summaries = ids
                .Select(id => gameServer.GetInstanceSummary(id))
                .Where(s => s != null)
                .Select(s => s!)
                .ToList();

            var response = new InstanceListResponse
            {
                TotalInstances = summaries.Count,
                TotalPlayers = summaries.Sum(s => s.PlayerCount),
                Instances = summaries
            };

            return Results.Ok(response);
        });

        // GET /admin/instance/{matchingId} — 인스턴스 상세
        app.MapGet("/admin/instance/{matchingId:long}", (long matchingId) =>
        {
            var snapshot = gameServer.GetInstanceSnapshot(matchingId);
            if (snapshot == null)
                return Results.NotFound(new { error = $"Instance {matchingId} not found" });

            return Results.Ok(snapshot);
        });

        // GET /admin/health — 어드민 서비스 헬스
        app.MapGet("/admin/health", () => Results.Ok(new { status = "ok", timestamp = DateTime.UtcNow }));
    }
}
