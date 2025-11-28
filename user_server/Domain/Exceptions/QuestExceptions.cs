namespace user_server.domain.exceptions;

/// <summary>
/// Exception thrown when a quest is not found
/// </summary>
public class QuestNotFoundException(int questId)
    : DomainException("QUEST_NOT_FOUND", $"Quest with ID {questId} not found")
{
    public int QuestId { get; } = questId;
}

/// <summary>
/// Exception thrown when quest prerequisites are not met
/// </summary>
public class QuestPrerequisiteNotMetException(int questId, int prerequisiteQuestId) : DomainException(
    "QUEST_PREREQUISITE_NOT_MET",
    $"Quest {questId} requires completion of quest {prerequisiteQuestId}")
{
    public int QuestId { get; } = questId;
    public int PrerequisiteQuestId { get; } = prerequisiteQuestId;
}

/// <summary>
/// Exception thrown when quest is already completed
/// </summary>
public class QuestAlreadyCompletedException(int questId)
    : DomainException("QUEST_ALREADY_COMPLETED", $"Quest {questId} is already completed")
{
    public int QuestId { get; } = questId;
}
