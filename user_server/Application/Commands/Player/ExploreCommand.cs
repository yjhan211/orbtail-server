using network.common.data;
using network.common.data.models;

namespace user_server.application.commands.player;

/// <summary>
/// Command to start exploration
/// </summary>
public record ExploreCommand(
    long PlayerId,
    C_TO_U_EXPLORE ExploreData
);
