namespace user_server.domain.exceptions;

/// <summary>
/// Exception thrown when a quest is not found
/// </summary>
public class QuestNotFoundException : DomainException
{
    public int QuestId { get; }

    public QuestNotFoundException(int questId)
        : base("QUEST_NOT_FOUND", $"Quest with ID {questId} not found")
    {
        QuestId = questId;
    }
}

/// <summary>
/// Exception thrown when quest prerequisites are not met
/// </summary>
public class QuestPrerequisiteNotMetException : DomainException
{
    public int QuestId { get; }
    public int PrerequisiteQuestId { get; }

    public QuestPrerequisiteNotMetException(int questId, int prerequisiteQuestId)
        : base("QUEST_PREREQUISITE_NOT_MET",
            $"Quest {questId} requires completion of quest {prerequisiteQuestId}")
    {
        QuestId = questId;
        PrerequisiteQuestId = prerequisiteQuestId;
    }
}

/// <summary>
/// Exception thrown when quest is already completed
/// </summary>
public class QuestAlreadyCompletedException : DomainException
{
    public int QuestId { get; }

    public QuestAlreadyCompletedException(int questId)
        : base("QUEST_ALREADY_COMPLETED", $"Quest {questId} is already completed")
    {
        QuestId = questId;
    }
}
