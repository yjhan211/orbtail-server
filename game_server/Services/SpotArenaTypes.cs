using System.Collections.Concurrent;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
///     스팟 아레나 세대에서 넘어온 공용 타입 — 스웜 엔진(SwarmArena/SwarmAttackEvents/BotPlayerManager)이 사용한다.
///     SpotArenaManager 본체는 #255에서 삭제됐다.
/// </summary>

public readonly record struct SpotArenaPlayerRegistration(
    long PlayerId,
    long TargetPlayerId,
    AreaType Area,
    Cell Cell);

public readonly record struct SpotArenaPlayerSpatial(
    long PlayerId,
    AreaType Area,
    Vector3f Position);

public readonly record struct SpotArenaSpotSnapshot(
    int MonsterId,
    long CombatTargetId,
    long OwnerPlayerId,
    long TargetPlayerId,
    AreaType Area,
    Cell Cell,
    Vector3f Position,
    int Health,
    int MaxHealth,
    bool Destroyed);

public readonly record struct SpotArenaWaveSnapshot(
    int MonsterId,
    long CombatTargetId,
    long OwnerPlayerId,
    long TargetOwnerPlayerId,
    AreaType Area,
    Vector3f Position,
    int Health,
    bool Alive);

public readonly record struct SpotArenaSnapshot(
    long MatchingId,
    int RemainingSeconds,
    IReadOnlyList<SpotArenaSpotSnapshot> Spots,
    IReadOnlyList<SpotArenaWaveSnapshot> Waves,
    IReadOnlyDictionary<long, int> RespawnSecondsByPlayer,
    bool Ended,
    long WinnerPlayerId)
{
    public static SpotArenaSnapshot Empty => new(
        0, 0, Array.Empty<SpotArenaSpotSnapshot>(), Array.Empty<SpotArenaWaveSnapshot>(),
        new Dictionary<long, int>(), false, 0);
}

public sealed class SpotArenaTickResult
{
    public static SpotArenaTickResult Empty => new(SpotArenaSnapshot.Empty);

    public SpotArenaTickResult(SpotArenaSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    public SpotArenaSnapshot Snapshot { get; set; }
    public List<MonsterRuntimeInfo> SpawnedWaves { get; } = new();
    public List<MonsterRuntimeInfo> ChangedWaves { get; } = new();
    public List<SpotArenaWaveClashEvent> WaveClashes { get; } = new();
    public List<SpotArenaPlayerDamage> PlayerDamage { get; } = new();
    public List<SpotArenaRespawnEvent> RespawnedPlayers { get; } = new();
    public List<SpotArenaSpotDestroyedEvent> DestroyedSpots { get; } = new();
    public List<SpotArenaReconnectEvent> Reconnections { get; } = new();
    public bool MatchEnded { get; set; }
    public long WinnerPlayerId { get; set; }
}

public readonly record struct SpotArenaDamageResult(
    bool StateChanged,
    bool DestroyedOrKilled,
    MonsterRuntimeInfo? WaveState,
    long SourcePlayerId,
    long TargetOwnerPlayerId,
    SpotArenaSpotDestroyedEvent? DestroyedSpot = null)
{
    public static SpotArenaDamageResult None => new(false, false, null, 0, 0);
}

public readonly record struct SpotArenaWaveClashEvent(
    int AttackerMonsterId,
    int TargetMonsterId,
    long AttackerOwnerPlayerId,
    long TargetOwnerPlayerId,
    AreaType Area,
    int Damage);

public readonly record struct SpotArenaPlayerDamage(
    int MonsterId,
    long TargetPlayerId,
    AreaType Area,
    int Damage);

public readonly record struct SpotArenaRespawnEvent(
    long PlayerId,
    AreaType Area,
    Cell Cell,
    DateTime InvulnerableUntilUtc);

public readonly record struct SpotArenaSpotDestroyedEvent(
    long OwnerPlayerId,
    long DestroyedByPlayerId,
    AreaType Area,
    long PreviousTargetPlayerId);

public readonly record struct SpotArenaReconnectEvent(
    long PredatorPlayerId,
    long RemovedPlayerId,
    long NewTargetPlayerId);

public enum SpotArenaCombatTargetKind
{
    Wave,
    Spot
}

public readonly record struct SpotArenaCombatTarget(
    long CombatTargetId,
    AreaType Area,
    Vector3f Position,
    SpotArenaCombatTargetKind Kind,
    long OwnerPlayerId,
    long TargetOwnerPlayerId,
    int MonsterId);

public enum SpotArenaBotMode
{
    None,
    Defend,
    Escort,
    Return
}

public readonly record struct SpotArenaBotDirective(
    SpotArenaBotMode Mode,
    AreaType DestinationArea,
    Cell DestinationCell,
    Vector3f DestinationPosition)
{
    public static SpotArenaBotDirective None => new(
        SpotArenaBotMode.None, AreaType.None, new Cell(0, 0), new Vector3f(0f, 0f, 0f));
}
