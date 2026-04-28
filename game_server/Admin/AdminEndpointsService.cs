using game_server.admin.dto;
using game_server.services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

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
        var matchingConfig = gameServer.MatchingConfigService;

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

        // GET /admin/instance/{matchingId}/full — 인스턴스 풀 상세 (폐쇄 스케줄 + 미션 전체 단계)
        app.MapGet("/admin/instance/{matchingId:long}/full", (long matchingId) =>
        {
            var snapshot = gameServer.GetFullInstanceSnapshot(matchingId);
            if (snapshot == null)
                return Results.NotFound(new { error = $"Instance {matchingId} not found" });

            return Results.Ok(snapshot);
        });

        // GET /admin/matching-config — 현재 글로벌 매칭 config 조회
        app.MapGet("/admin/matching-config", async () =>
        {
            var snapshot = await matchingConfig.GetSnapshotAsync();
            return Results.Ok(snapshot);
        });

        // POST /admin/matching-config/closure — 폐쇄 config 변경
        app.MapPost("/admin/matching-config/closure", async (SetClosureConfigRequest req) =>
        {
            if (req.ResetAll)
            {
                matchingConfig.ResetClosureConfig();
                var snapshot = await matchingConfig.GetSnapshotAsync();
                return Results.Ok(new { message = "폐쇄 config 기본값 복원 완료. 다음 매칭부터 적용됩니다.", config = snapshot });
            }

            if (req.ResetSequence)
                matchingConfig.ClearForcedSequence();

            matchingConfig.SetClosureConfig(req.StartDelaySec, req.IntervalSec, req.Sequence);
            var updated = await matchingConfig.GetSnapshotAsync();
            return Results.Ok(new { message = "폐쇄 config 변경 완료. 다음 매칭부터 적용됩니다.", config = updated });
        });

        // POST /admin/matching-config/job-pool — 직책 풀 config 변경
        app.MapPost("/admin/matching-config/job-pool", async (SetJobPoolConfigRequest req) =>
        {
            await matchingConfig.SetJobPoolConfigAsync(req.Jobs);
            var snapshot = await matchingConfig.GetSnapshotAsync();
            string message = req.Jobs == null
                ? "직책 풀 무작위 복원 완료. 다음 매칭부터 적용됩니다."
                : $"직책 풀 강제 지정 완료 ({req.Jobs.Count}개). 다음 매칭부터 적용됩니다.";
            return Results.Ok(new { message, config = snapshot });
        });

        // GET /admin/health — 어드민 서비스 헬스
        app.MapGet("/admin/health", () => Results.Ok(new { status = "ok", timestamp = DateTime.UtcNow }));
    }
}
