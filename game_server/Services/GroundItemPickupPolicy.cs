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

    // 하트 (#222 M4): 체력 105 즉시 회복 = 최대 체력(420)의 1/4 — 버스트 전 생존성 레이어.
    public const int HeartItemId = global::network.common.Config.HEART_GROUND_ITEM_ID;
    public const int HeartRecovery = 105;

    public static bool IsImmediateUseItem(int itemId) =>
        itemId is BandageItemId or CannedCoffeeItemId or HeartItemId;

    public static bool ShouldDropOnElimination(int itemId) => !IsImmediateUseItem(itemId);

    public static GroundItemPickupDisposition Resolve(
        int itemId,
        int stamina,
        int maxStamina,
        int health,
        out int staminaRecovery,
        out int healthRecovery,
        long matchingId = 0,
        long playerId = 0)
    {
        staminaRecovery = itemId == CannedCoffeeItemId
            ? CannedCoffeeStaminaRecovery
            : 0;
        healthRecovery = itemId switch
        {
            BandageItemId => BandageRecovery,
            FirstAidKitItemId => FirstAidKitRecovery,
            HeartItemId => HeartRecovery,
            _ => 0
        };

        // 원작 하트 문법: 만피면 흐릿해지고 못 줍는다 — 본체 체력이 가득하면 바닥에 남긴다.
        // 낭비 방지 + 다친 쪽이 줍는 경합 유지. (오브 HP 게이트는 오브 HP 전투 퇴역으로 #318에서 삭제)
        if (itemId == HeartItemId && health >= global::network.common.Config.MAX_HEALTH)
            return GroundItemPickupDisposition.LeaveOnGround;

        if (staminaRecovery == 0 && healthRecovery == 0)
            return GroundItemPickupDisposition.Store;

        return IsImmediateUseItem(itemId)
            ? GroundItemPickupDisposition.AutoUse
            : GroundItemPickupDisposition.Store;
    }
}
