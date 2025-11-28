using network.common.data.models;

namespace user_server.application.commands.player;

/// <summary>
/// Command to use an item from inventory
/// </summary>
public record UseItemCommand(
    long PlayerId,
    C_TO_U_USE_ITEM UseData
);
