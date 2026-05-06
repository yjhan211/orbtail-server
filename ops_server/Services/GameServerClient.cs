using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace ops_server.services;

/// <summary>
///     game_server /admin/* endpoint HttpClient 래퍼
/// </summary>
public class GameServerClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<InstanceListResponse?> GetInstancesAsync(CancellationToken ct = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<InstanceListResponse>("/admin/instances", JsonOpts, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GameServerClient] GetInstances 오류: {ex.Message}");
            return null;
        }
    }

    public async Task<InstanceSnapshot?> GetInstanceAsync(long matchingId, CancellationToken ct = default)
    {
        try
        {
            var response = await httpClient.GetAsync($"/admin/instance/{matchingId}", ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<InstanceSnapshot>(JsonOpts, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GameServerClient] GetInstance({matchingId}) 오류: {ex.Message}");
            return null;
        }
    }

    public async Task<InstanceSnapshot?> GetFullInstanceAsync(long matchingId, CancellationToken ct = default)
    {
        try
        {
            var response = await httpClient.GetAsync($"/admin/instance/{matchingId}/full", ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<InstanceSnapshot>(JsonOpts, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GameServerClient] GetFullInstance({matchingId}) 오류: {ex.Message}");
            return null;
        }
    }

    public async Task<InstanceEventsResponse?> GetInstanceEventsAsync(long matchingId,
        int? limit, long? since, CancellationToken ct = default)
    {
        try
        {
            string url = $"/admin/instance/{matchingId}/events";
            var query = new List<string>();
            if (limit.HasValue) query.Add($"limit={limit.Value}");
            if (since.HasValue) query.Add($"since={since.Value}");
            if (query.Count > 0) url += "?" + string.Join("&", query);

            var response = await httpClient.GetAsync(url, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<InstanceEventsResponse>(JsonOpts, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GameServerClient] GetInstanceEvents({matchingId}) 오류: {ex.Message}");
            return null;
        }
    }

    public async Task<MatchingConfigSnapshot?> GetMatchingConfigAsync(CancellationToken ct = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<MatchingConfigSnapshot>("/admin/matching-config", JsonOpts, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GameServerClient] GetMatchingConfig 오류: {ex.Message}");
            return null;
        }
    }

    public async Task<MatchingConfigApiResponse?> PostClosureConfigAsync(object body, CancellationToken ct = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(body, JsonOpts);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync("/admin/matching-config/closure", content, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<MatchingConfigApiResponse>(JsonOpts, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GameServerClient] PostClosureConfig 오류: {ex.Message}");
            return null;
        }
    }

    public async Task<MatchingConfigApiResponse?> PostJobPoolConfigAsync(object body, CancellationToken ct = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(body, JsonOpts);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync("/admin/matching-config/job-pool", content, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<MatchingConfigApiResponse>(JsonOpts, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GameServerClient] PostJobPoolConfig 오류: {ex.Message}");
            return null;
        }
    }
}

// ─── DTO mirrors (game_server Admin DTO와 구조 일치) ───────────────────────

public class InstanceListResponse
{
    public int TotalInstances { get; set; }
    public int TotalPlayers { get; set; }
    public List<InstanceSummary> Instances { get; set; } = [];
}

public class InstanceSummary
{
    public long MatchingId { get; set; }
    public string MapId { get; set; } = "";
    public int PlayerCount { get; set; }
    public int AliveCount { get; set; }
    public double ElapsedSeconds { get; set; }
    public List<string> ClosedAreas { get; set; } = [];
}

public class InstanceSnapshot
{
    public long MatchingId { get; set; }
    public string MapId { get; set; } = "";
    public int PlayerCount { get; set; }
    public int AliveCount { get; set; }
    public double ElapsedSeconds { get; set; }
    public List<string> ClosedAreas { get; set; } = [];
    public List<PlayerSnapshot> Players { get; set; } = [];
    public ClosureSnapshot? Closure { get; set; }
}

public class ClosureSnapshot
{
    public List<int> ClosureSequence { get; set; } = [];
    public List<string> AreaNames { get; set; } = [];
    public List<int> ClosedAreaIds { get; set; } = [];
    public int NextClosureAreaType { get; set; } = -1;
    public long NextClosureAtUnix { get; set; } = -1;
    public int NextClosureSecondsLeft { get; set; } = -1;
    public bool WarningActive { get; set; }
    public int StartDelaySec { get; set; }
    public int IntervalSec { get; set; }
}

public class MissionFullStep
{
    public int Order { get; set; }
    public int TargetAreaType { get; set; }
    public string TargetAreaName { get; set; } = "";
    public int TargetInteractId { get; set; }
    public bool IsCompleted { get; set; }
    public bool IsCurrent { get; set; }
    public string Description { get; set; } = "";
    public string TargetObjectName { get; set; } = "";
}

public class PlayerSnapshot
{
    public long PlayerId { get; set; }
    public string Area { get; set; } = "";
    public int Stamina { get; set; }
    public int Corruption { get; set; }
    public string ManittoStatus { get; set; } = "";
    public long TargetPlayerId { get; set; }
    public bool IsBot { get; set; }
    public bool IsEliminated { get; set; }
    public int MissionStep { get; set; }
    public int MissionTotalSteps { get; set; }
    public bool MissionCompleted { get; set; }
    public long? ManittoOfMe { get; set; }
    public string ChainStatus { get; set; } = "";
    public string JobTitle { get; set; } = "";
    public List<MissionFullStep> AllSteps { get; set; } = [];
}

public class MatchingConfigSnapshot
{
    public int StartDelaySec { get; set; }
    public int IntervalSec { get; set; }
    public List<int>? ForcedSequence { get; set; }
    public List<int>? ForcedJobs { get; set; }
}

public class MatchingConfigApiResponse
{
    public string Message { get; set; } = "";
    public MatchingConfigSnapshot? Config { get; set; }
}

public class InstanceEventsResponse
{
    public long MatchingId { get; set; }
    public int Count { get; set; }
    public List<GameEventEntryDto> Events { get; set; } = [];
}

public class GameEventEntryDto
{
    public long Seq { get; set; }
    public long TimestampUnixMs { get; set; }
    public string Type { get; set; } = "";
    public long PlayerId { get; set; }
    public bool IsBot { get; set; }
    public string Description { get; set; } = "";
}
