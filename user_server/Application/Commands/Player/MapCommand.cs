using network.common;
using network.common.data;
using network.common.data.models;

namespace user_server.application.commands.player;

/// <summary>
/// Command to change map
/// </summary>
public record ChangeMapCommand(
    long PlayerId,
    C_TO_U_CHANGE_MAP MapData
);

/// <summary>
/// Command to enter a map with specific position
/// </summary>
public record EnterMapCommand(
    long PlayerId,
    MapId MapId,
    Cell SpawnPosition,
    bool IsFlip,
    bool IsLogin
);

/// <summary>
/// Command to enter a camp (player's base)
/// </summary>
public record EnterCampCommand(
    long PlayerId,
    long MapSubId
);
