using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
///     스팟 아레나 세대에서 넘어온 공용 타입 — 스웜 엔진(SwarmArena/SwarmAttackEvents/BotPlayerManager)이 사용한다.
///     SpotArenaManager 본체는 #255에서, 틱/스냅샷 계약 등 미사용 타입은 #274 후속 정리에서 삭제됐다.
/// </summary>

public readonly record struct SpotArenaPlayerSpatial(
    long PlayerId,
    AreaType Area,
    Vector3f Position);

public readonly record struct SpotArenaPlayerDamage(
    int MonsterId,
    long TargetPlayerId,
    AreaType Area,
    int Damage);

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
