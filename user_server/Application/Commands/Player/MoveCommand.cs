using network.common.data;
using network.common.data.models;

namespace user_server.application.commands.player;

/// <summary>
/// Command to request player movement
/// </summary>
public record MoveCommand(
    long PlayerId,
    C_TO_U_MOVE MoveData
);
