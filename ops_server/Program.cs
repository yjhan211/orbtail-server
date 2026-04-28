using ops_server.services;

var builder = WebApplication.CreateBuilder(args);

// ASP.NET Core HTTP 요청 라이프사이클 + HttpClient 송수신 로그 차단 (정상 200 요청 노이즈 제거)
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

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

// 프록시: 인스턴스 풀 상세 (폐쇄 스케줄 + 미션 전체 단계)
app.MapGet("/api/instance/{matchingId:long}/full", async (long matchingId, GameServerClient client, CancellationToken ct) =>
{
    var result = await client.GetFullInstanceAsync(matchingId, ct);
    return result is null
        ? Results.NotFound(new { error = $"Instance {matchingId} not found" })
        : Results.Ok(result);
});

// 프록시: 글로벌 매칭 config 조회
app.MapGet("/api/matching-config", async (GameServerClient client, CancellationToken ct) =>
{
    var result = await client.GetMatchingConfigAsync(ct);
    return result is null
        ? Results.Problem("game_server 연결 실패")
        : Results.Ok(result);
});

// 프록시: 폐쇄 config 변경
app.MapPost("/api/matching-config/closure", async (System.Text.Json.JsonElement body, GameServerClient client, CancellationToken ct) =>
{
    var result = await client.PostClosureConfigAsync(body, ct);
    return result is null
        ? Results.Problem("game_server 연결 실패")
        : Results.Ok(result);
});

// 프록시: 직책 풀 config 변경
app.MapPost("/api/matching-config/job-pool", async (System.Text.Json.JsonElement body, GameServerClient client, CancellationToken ct) =>
{
    var result = await client.PostJobPoolConfigAsync(body, ct);
    return result is null
        ? Results.Problem("game_server 연결 실패")
        : Results.Ok(result);
});

// ops_server 헬스
app.MapGet("/health", () => Results.Ok(new { status = "ok", timestamp = DateTime.UtcNow }));

app.Run();
