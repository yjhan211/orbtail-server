namespace user_server.domain.exceptions;

/// <summary>
/// Exception thrown when inventory is full
/// </summary>
public class InventoryFullException : DomainException
{
    public int Capacity { get; }
    public int CurrentCount { get; }

    public InventoryFullException(int capacity, int currentCount)
        : base("INVENTORY_FULL", $"Inventory is full: {currentCount}/{capacity}")
    {
        Capacity = capacity;
        CurrentCount = currentCount;
    }
}

/// <summary>
/// Exception thrown when item is not found in inventory
/// </summary>
public class ItemNotFoundException : DomainException
{
    public long ItemUid { get; }

    public ItemNotFoundException(long itemUid)
        : base("ITEM_NOT_FOUND", $"Item with UID {itemUid} not found in inventory")
    {
        ItemUid = itemUid;
    }
}

/// <summary>
/// Exception thrown when item cannot be equipped
/// </summary>
public class CannotEquipItemException : DomainException
{
    public int ItemId { get; }
    public string Reason { get; }

    public CannotEquipItemException(int itemId, string reason)
        : base("CANNOT_EQUIP_ITEM", $"Cannot equip item {itemId}: {reason}")
    {
        ItemId = itemId;
        Reason = reason;
    }
}
