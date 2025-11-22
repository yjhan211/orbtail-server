using network.common.data;
using network.common.data.models;

namespace user_server.application.commands.player;

/// <summary>
/// Command to perform a social action (sit, emote, etc.)
/// </summary>
public record PerformSocialActionCommand(
    long PlayerId,
    C_TO_U_SOCIAL_ACTION ActionData
);

/// <summary>
/// Command to set player name
/// </summary>
public record SetPlayerNameCommand(
    long PlayerId,
    string Name
);
