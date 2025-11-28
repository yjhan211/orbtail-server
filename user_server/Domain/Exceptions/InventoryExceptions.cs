namespace user_server.domain.exceptions;

/// <summary>
/// Exception thrown when inventory is full
/// </summary>
public class InventoryFullException(int capacity, int currentCount)
    : DomainException("INVENTORY_FULL", $"Inventory is full: {currentCount}/{capacity}")
{
    public int Capacity { get; } = capacity;
    public int CurrentCount { get; } = currentCount;
}

/// <summary>
/// Exception thrown when item is not found in inventory
/// </summary>
public class ItemNotFoundException(long itemUid)
    : DomainException("ITEM_NOT_FOUND", $"Item with UID {itemUid} not found in inventory")
{
    public long ItemUid { get; } = itemUid;
}

/// <summary>
/// Exception thrown when item cannot be equipped
/// </summary>
public class CannotEquipItemException(int itemId, string reason)
    : DomainException("CANNOT_EQUIP_ITEM", $"Cannot equip item {itemId}: {reason}")
{
    public int ItemId { get; } = itemId;
    public string Reason { get; } = reason;
}
