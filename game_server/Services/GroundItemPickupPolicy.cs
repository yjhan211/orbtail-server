namespace game_server.services;

public enum GroundItemPickupDisposition
{
    Store,
    AutoUse,
    LeaveOnGround
}

public static class GroundItemPickupPolicy
{
    public const int BandageItemId = 201000008;
    public const int CannedCoffeeItemId = 201000011;
    public const int FirstAidKitItemId = 201000018;
    public const int BandageRecovery = 15;
    public const int CannedCoffeeStaminaRecovery = 15;
    public const int FirstAidKitRecovery = 35;

    public static GroundItemPickupDisposition Resolve(
        int itemId,
        int stamina,
        int maxStamina,
        int corruption,
        out int staminaRecovery,
        out int corruptionRecovery)
    {
        staminaRecovery = itemId == CannedCoffeeItemId
            ? CannedCoffeeStaminaRecovery
            : 0;
        corruptionRecovery = itemId switch
        {
            BandageItemId => BandageRecovery,
            FirstAidKitItemId => FirstAidKitRecovery,
            _ => 0
        };

        if (staminaRecovery == 0 && corruptionRecovery == 0)
            return GroundItemPickupDisposition.Store;

        // Ground pickups never consume recovery items immediately. Stats are applied only
        // when the player later chooses the stored inventory item.
        return GroundItemPickupDisposition.Store;
    }
}
