using network.common.data.models;

namespace user_server.application.commands.player;

/// <summary>
/// Command to increase quest progress count
/// </summary>
public record IncreaseQuestCountCommand(
    long PlayerId,
    C_TO_U_QUEST_INCREASE QuestData
);

/// <summary>
/// Command to complete a quest
/// </summary>
public record CompleteQuestCommand(
    long PlayerId,
    C_TO_U_QUEST_SUCCESS QuestData
);

/// <summary>
/// Command to start a new quest
/// </summary>
public abstract record StartQuestCommand(
    long PlayerId,
    int QuestId
);
