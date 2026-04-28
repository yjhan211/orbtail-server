using ops_server.services;

var builder = WebApplication.CreateBuilder(args);

// ─── 서비스 등록 ───────────────────────────────────────────────────────────

string gameServerBaseUrl = builder.Configuration["GameServerUrl"] ?? "http://game-server:8080";

builder.Services.AddHttpClient<GameServerClient>(client =>
{
    client.BaseAddress = new Uri(gameServerBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(5);
});

// ─── 앱 빌드 ──────────────────────────────────────────────────────────────

var app = builder.Build();

// 정적 파일 (wwwroot/index.html, app.js)
app.UseDefaultFiles();
app.UseStaticFiles();

// ─── API 라우트 ───────────────────────────────────────────────────────────

// 프록시: 인스턴스 목록
app.MapGet("/api/instances", async (GameServerClient client, CancellationToken ct) =>
{
    var result = await client.GetInstancesAsync(ct);
    return result is null
        ? Results.Problem("game_server 연결 실패")
        : Results.Ok(result);
});

// 프록시: 인스턴스 상세
app.MapGet("/api/instance/{matchingId:long}", async (long matchingId, GameServerClient client, CancellationToken ct) =>
{
    var result = await client.GetInstanceAsync(matchingId, ct);
    return result is null
        ? Results.NotFound(new { error = $"Instance {matchingId} not found" })
        : Results.Ok(result);
});

// ops_server 헬스
app.MapGet("/health", () => Results.Ok(new { status = "ok", timestamp = DateTime.UtcNow }));

app.Run();
