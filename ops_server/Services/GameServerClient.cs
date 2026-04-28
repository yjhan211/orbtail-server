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

    public async Task<InstanceSnapshot?> GetFullInstanceAsync(long matchingId, CancellationToken ct = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<InstanceSnapshot>($"/admin/instance/{matchingId}/full", JsonOpts, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GameServerClient] GetFullInstance({matchingId}) 오류: {ex.Message}");
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
    public List<int> ClosedAreaIds { get; set; } = [];
    public int NextClosureAreaType { get; set; } = -1;
    public long NextClosureAtUnix { get; set; } = -1;
    public int NextClosureSecondsLeft { get; set; } = -1;
    public bool WarningActive { get; set; }
}

public class MissionFullStep
{
    public int Order { get; set; }
    public int TargetAreaType { get; set; }
    public string TargetAreaName { get; set; } = "";
    public int TargetInteractId { get; set; }
    public bool IsCompleted { get; set; }
    public bool IsCurrent { get; set; }
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
