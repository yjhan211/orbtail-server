using Microsoft.Extensions.FileProviders;
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
builder.Services.AddSingleton<StoryletCsvService>();

// ─── 앱 빌드 ──────────────────────────────────────────────────────────────

var app = builder.Build();

// 정적 파일 (wwwroot/index.html, app.js)
app.UseDefaultFiles();
app.UseStaticFiles();

string? itemSpritesRoot = ResolveItemSpritesRoot(builder.Configuration["ItemSpritesRoot"]);
if (!string.IsNullOrWhiteSpace(itemSpritesRoot))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(itemSpritesRoot),
        RequestPath = "/item-sprites",
    });
}

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

// 프록시: 인스턴스 진행 로그 (이동/자원/미션/탈락)
app.MapGet("/api/instance/{matchingId:long}/events",
    async (long matchingId, int? limit, long? since, GameServerClient client, CancellationToken ct) =>
    {
        var result = await client.GetInstanceEventsAsync(matchingId, limit, since, ct);
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

// 기록 Storylet CSV 조회
app.MapGet("/api/storylets", (StoryletCsvService service) => Results.Ok(service.Load()));

// 기록 Storylet 시작점 저장
app.MapPut("/api/storylets/start/{nodeId}", (string nodeId, Dictionary<string, string?> body, StoryletCsvService service) =>
{
    var result = service.UpdateStart(nodeId, body);
    return result.Success
        ? Results.Ok(new { result.Message, data = service.Load() })
        : Results.NotFound(new { result.Message });
});

// 기록 Storylet 후보 풀 저장
app.MapPut("/api/storylets/pool/{nodeId}", (string nodeId, Dictionary<string, string?> body, StoryletCsvService service) =>
{
    var result = service.UpdatePool(nodeId, body);
    return result.Success
        ? Results.Ok(new { result.Message, data = service.Load() })
        : Results.NotFound(new { result.Message });
});

// 상호작용 오브젝트 기본 본문 저장
app.MapPut("/api/storylets/interactable/{id}", (string id, Dictionary<string, string?> body, StoryletCsvService service) =>
{
    var result = service.UpdateInteractable(id, body);
    return result.Success
        ? Results.Ok(new { result.Message, data = service.Load() })
        : Results.NotFound(new { result.Message });
});

// 아이템 이름 저장
app.MapPut("/api/storylets/item/{id}", (string id, Dictionary<string, string?> body, StoryletCsvService service) =>
{
    var result = service.UpdateItem(id, body);
    return result.Success
        ? Results.Ok(new { result.Message, data = service.Load() })
        : Results.NotFound(new { result.Message });
});

// 조합 레시피 저장
app.MapPut("/api/storylets/recipe/{recipeId}",
    (string recipeId, Dictionary<string, string?> body, StoryletCsvService service) =>
    {
        var result = service.UpdateRecipe(recipeId, body);
        return result.Success
            ? Results.Ok(new { result.Message, data = service.Load() })
            : Results.NotFound(new { result.Message });
    });

app.MapPut("/api/storylets/object-action/{actionGroupKey}/{actionId}",
    (string actionGroupKey, string actionId, Dictionary<string, string?> body, StoryletCsvService service) =>
    {
        var result = service.UpdateObjectAction(actionGroupKey, actionId, body);
        return result.Success
            ? Results.Ok(new { result.Message, data = service.Load() })
            : Results.NotFound(new { result.Message });
    });

// ops_server 헬스
app.MapGet("/health", () => Results.Ok(new { status = "ok", timestamp = DateTime.UtcNow }));

app.Run();

static string? ResolveItemSpritesRoot(string? configuredRoot)
{
    // 설정(ItemSpritesRoot 환경변수)이 있고 실제로 존재하면 우선 사용 — Docker 마운트 경로 등.
    // 컨테이너에는 client/ 에셋이 없어 walk-up이 실패하므로 이 경로가 스프라이트 서빙의 핵심이다.
    if (!string.IsNullOrWhiteSpace(configuredRoot))
    {
        string full = Path.GetFullPath(configuredRoot);
        if (Directory.Exists(full)) return full;
    }

    foreach (string seed in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        string? cursor = Path.GetFullPath(seed);
        while (!string.IsNullOrEmpty(cursor))
        {
            string candidate = Path.Combine(cursor, "client", "Assets", "Resources", "ItemSprites");
            if (Directory.Exists(candidate)) return candidate;
            cursor = Directory.GetParent(cursor)?.FullName;
        }
    }

    return null;
}
