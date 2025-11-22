using network.common.data;
using network.common.data.models;

namespace user_server.application.commands.player;

/// <summary>
/// Command to wear items on player
/// </summary>
public record WearItemCommand(
    long PlayerId,
    C_TO_U_WEAR_ITEM WearData
);
