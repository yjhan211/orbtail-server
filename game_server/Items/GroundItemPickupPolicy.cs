namespace game_server.items;

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

    // 하트 (#222 M4): 체력 105 즉시 회복 = 최대 체력(420)의 1/4 — 버스트 전 생존성 레이어.
    public const int HeartItemId = global::network.common.Config.HEART_GROUND_ITEM_ID;
    public const int HeartRecovery = 105;

    public static bool IsImmediateUseItem(int itemId) =>
        itemId is BandageItemId or HeartItemId;

    public static bool ShouldDropOnElimination(int itemId) => !IsImmediateUseItem(itemId);

    public static GroundItemPickupDisposition Resolve(
        int itemId,
        int health,
        out int healthRecovery,
        long matchingId = 0,
        long playerId = 0)
    {
        // 사용하지 않는 잼 아이템은 인벤토리에 넣거나 자동 사용하지 않는다.
        if (itemId == global::network.common.Config.JAM_GROUND_ITEM_ID)
        {
            healthRecovery = 0;
            return GroundItemPickupDisposition.LeaveOnGround;
        }
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

        if (healthRecovery == 0)
            return GroundItemPickupDisposition.Store;

        return IsImmediateUseItem(itemId)
            ? GroundItemPickupDisposition.AutoUse
            : GroundItemPickupDisposition.Store;
    }
}
