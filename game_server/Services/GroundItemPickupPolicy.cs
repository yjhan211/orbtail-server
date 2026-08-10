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

    // 하트 (#222 M4): 오염 105 즉시 회복 = 만오염(420)의 1/4 — 버스트 전 생존성 레이어.
    public const int HeartItemId = global::network.common.Config.HEART_GROUND_ITEM_ID;
    public const int HeartRecovery = 105;

    public static bool IsImmediateUseItem(int itemId) =>
        itemId is BandageItemId or CannedCoffeeItemId or HeartItemId;

    public static bool ShouldDropOnElimination(int itemId) => !IsImmediateUseItem(itemId);

    /// <summary>앞줄 오브 손상 여부 조회 — 하트 픽업 게이트 보조. GameServer가 스웜 초기화 시 배선.</summary>
    public static Func<long, long, bool>? FrontOrbDamagedResolver { get; set; }

    public static GroundItemPickupDisposition Resolve(
        int itemId,
        int stamina,
        int maxStamina,
        int corruption,
        out int staminaRecovery,
        out int corruptionRecovery,
        long matchingId = 0,
        long playerId = 0)
    {
        staminaRecovery = itemId == CannedCoffeeItemId
            ? CannedCoffeeStaminaRecovery
            : 0;
        corruptionRecovery = itemId switch
        {
            BandageItemId => BandageRecovery,
            FirstAidKitItemId => FirstAidKitRecovery,
            HeartItemId => HeartRecovery,
            _ => 0
        };

        // 원작 하트 문법: 스쿼드가 만피면 흐릿해지고 못 줍는다 — 본체(오염)도 앞줄 오브도
        // 멀쩡하면 바닥에 남긴다. 낭비 방지 + 다친 쪽이 줍는 경합 유지.
        if (itemId == HeartItemId && corruption <= 0 &&
            FrontOrbDamagedResolver?.Invoke(matchingId, playerId) != true)
            return GroundItemPickupDisposition.LeaveOnGround;

        if (staminaRecovery == 0 && corruptionRecovery == 0)
            return GroundItemPickupDisposition.Store;

        return IsImmediateUseItem(itemId)
            ? GroundItemPickupDisposition.AutoUse
            : GroundItemPickupDisposition.Store;
    }
}
