using System.Net.Http.Json;
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
            return await httpClient.GetFromJsonAsync<InstanceSnapshot>($"/admin/instance/{matchingId}", JsonOpts, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GameServerClient] GetInstance({matchingId}) 오류: {ex.Message}");
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
}
