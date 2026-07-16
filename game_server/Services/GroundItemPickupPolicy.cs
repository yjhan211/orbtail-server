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
    public const int FirstAidKitItemId = 201000018;
    public const int BandageRecovery = 15;
    public const int FirstAidKitRecovery = 35;

    public static GroundItemPickupDisposition Resolve(int itemId, int corruption, out int recovery)
    {
        recovery = itemId switch
        {
            BandageItemId => BandageRecovery,
            FirstAidKitItemId => FirstAidKitRecovery,
            _ => 0
        };

        if (recovery == 0) return GroundItemPickupDisposition.Store;
        return corruption > 0
            ? GroundItemPickupDisposition.AutoUse
            : GroundItemPickupDisposition.LeaveOnGround;
    }
}