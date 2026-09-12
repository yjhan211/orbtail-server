using network.common;

namespace game_server.matches.monsters;

/// <summary>
///     매치 하나의 몬스터 상태. MatchRuntime이 소유하고 매치 잠금 안에서만 읽고 쓴다.
///     개체 목록·공급 구역 회계를 드는 필드 홀더다. 조회와 갱신은 모두 MatchMonsterService를 거치고, 매치 정리 때 Release로 비운다.
/// </summary>
public sealed class MatchMonsterState
{
    public bool IsInitialized { get; set; }
    public DateTime NextSnapshotAtUtc { get; set; }
    public DateTime StartsAtUtc { get; set; }
    public DateTime LastTickAtUtc { get; set; }
    public Dictionary<int, Monster> Entities { get; } = new();
    public Random Rng { get; set; } = new();
    public int NextSerial { get; set; }
    public bool NextMonsterGrantsSummonStone { get; set; } = true;
    public Dictionary<AreaType, float> InfiltrationExitBearings { get; } = new();
    public Dictionary<AreaType, MonsterSupplyZoneState> SupplyZones { get; } = new();
    public Dictionary<AreaType, DateTime> ZoneVacatedAtUtc { get; } = new();
    public Dictionary<(AreaType Area, int PhaseIndex), (double Available, DateTime RefilledAtUtc)> SupplyStoneBucket { get; } = new();
    public HashSet<(AreaType Area, int PhaseIndex)> SupplyCoreRewarded { get; } = new();
    public int NextSupplyPackOrdinal { get; set; }
    public int NextInfiltrationOriginOrdinal { get; set; }
    public int MaxParticipantCount { get; set; }

    internal void Release()
    {
        IsInitialized = false;
        NextSnapshotAtUtc = default;
        Entities.Clear();
        InfiltrationExitBearings.Clear();
        SupplyZones.Clear();
        ZoneVacatedAtUtc.Clear();
        SupplyStoneBucket.Clear();
        SupplyCoreRewarded.Clear();
    }
}

public sealed class MonsterSupplyZoneState
{
    public DateTime? NextTopUpAtUtc { get; set; }
    public DateTime? WipeRestUntilUtc { get; set; }
    public bool HasSpawned { get; set; }
}
