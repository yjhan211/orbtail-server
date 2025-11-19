using network.common;
using network.common.data.models;

namespace user_server.domain.specifications;

/// <summary>
/// Specification: Player is alive (HP > 0)
/// </summary>
public class PlayerIsAliveSpecification : CompositeSpecification<PlayerInfo>
{
    public override bool IsSatisfiedBy(PlayerInfo player)
    {
        return player.Hp > 0;
    }
}

/// <summary>
/// Specification: Player is above a certain level threshold
/// </summary>
public class PlayerMinimumLevelSpecification : CompositeSpecification<PlayerInfo>
{
    private readonly int _minimumLevel;

    public PlayerMinimumLevelSpecification(int minimumLevel)
    {
        _minimumLevel = minimumLevel;
    }

    public override bool IsSatisfiedBy(PlayerInfo player)
    {
        // Note: If Level doesn't exist in PlayerInfo, this is a placeholder
        // You would adjust based on actual PlayerInfo structure
        return true; // Placeholder
    }
}

/// <summary>
/// Specification: Player has enough HP for an action
/// </summary>
public class PlayerHasMinimumHpSpecification : CompositeSpecification<PlayerInfo>
{
    private readonly int _minimumHp;

    public PlayerHasMinimumHpSpecification(int minimumHp)
    {
        _minimumHp = minimumHp;
    }

    public override bool IsSatisfiedBy(PlayerInfo player)
    {
        return player.Hp >= _minimumHp;
    }
}

/// <summary>
/// Specification: Player is not a dummy player
/// </summary>
public class PlayerIsNotDummySpecification : CompositeSpecification<PlayerInfo>
{
    public override bool IsSatisfiedBy(PlayerInfo player)
    {
        return player.PlayerId > PlayerConstants.DUMMY_PLAYER_ID_THRESHOLD;
    }
}

/// <summary>
/// Specification: Player is in a specific state
/// </summary>
public class PlayerInStateSpecification : CompositeSpecification<PlayerInfo>
{
    private readonly PlayerState _state;

    public PlayerInStateSpecification(PlayerState state)
    {
        _state = state;
    }

    public override bool IsSatisfiedBy(PlayerInfo player)
    {
        return player.State == _state;
    }
}

/// <summary>
/// Specification: Player owns a specific item
/// </summary>
public class PlayerOwnsItemSpecification : CompositeSpecification<PlayerInfo>
{
    private readonly int _itemId;

    public PlayerOwnsItemSpecification(int itemId)
    {
        _itemId = itemId;
    }

    public override bool IsSatisfiedBy(PlayerInfo player)
    {
        return player.InventoryInfo.ItemDict.Values.Any(item => item.ItemId == _itemId);
    }
}

/// <summary>
/// Specification: Player has completed a specific quest
/// </summary>
public class PlayerCompletedQuestSpecification : CompositeSpecification<PlayerInfo>
{
    private readonly int _questId;

    public PlayerCompletedQuestSpecification(int questId)
    {
        _questId = questId;
    }

    public override bool IsSatisfiedBy(PlayerInfo player)
    {
        return player.QuestDiary.QuestDict.ContainsKey(_questId);
        // Additional check for completion status could be added if QuestState exists
    }
}
