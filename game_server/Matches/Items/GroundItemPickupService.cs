using game_server.matches;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches.items;

/// <summary>
///     매치 잠금 안에서 바닥 아이템의 획득 가능 여부와 자동 사용·인벤토리 추가를 확정한다.
///     결과를 받은 세션이 회복·재화 통지와 삭제·획득 패킷을 기존 순서대로 발행한다.
/// </summary>
internal static class GroundItemPickupService
{
    public static GroundItemPickupResult TryPickup(
        MatchRuntime runtime, long playerId, AreaType area, Vector3f position,
        int health, long groundItemUid)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
            throw new InvalidOperationException("Ground item pickup requires the match lock.");

        InGameItemInfo? addedItem = null;
        bool autoUsed = false;
        bool summonStonePickup = false;
        bool bootsPickup = false;
        int healthRecovery = 0;
        ErrorCode rejection = ErrorCode.INVENTORY_FULL;

        long discovererPlayerId = runtime.GroundItems.GetDiscovererPlayerId(
            groundItemUid);
        var attemptedItem = runtime.GroundItems.GetItem(groundItemUid);
        var status = runtime.GroundItems.TryClaim(
            groundItemUid,
            playerId,
            area,
            position.X,
            position.Y,
            item =>
            {
                if (item.ItemId == Config.SUMMON_STONE_GROUND_ITEM_ID)
                {
                    summonStonePickup = true;
                    return true;
                }

                if (item.ItemId == Config.BOOTS_GROUND_ITEM_ID)
                {
                    bootsPickup = true;
                    return true;
                }

                if (item.ItemId == Config.KEY_GROUND_ITEM_ID)
                {
                    rejection = ErrorCode.ITEM_NOT_USABLE;
                    return false;
                }

                var disposition = GroundItemPickupPolicy.Resolve(
                    item.ItemId,
                    health,
                    out healthRecovery,
                    runtime.MatchingId,
                    playerId);
                if (disposition == GroundItemPickupDisposition.LeaveOnGround)
                {
                    rejection = ErrorCode.ITEM_NOT_USABLE;
                    return false;
                }
                if (disposition == GroundItemPickupDisposition.AutoUse)
                {
                    autoUsed = true;
                    return true;
                }

                bool added = runtime.Inventory.TryAddItemWithCapacity(
                    playerId, item.ItemId, Config.GetOrbCapacity(),
                    out addedItem);
                if (!added) rejection = ErrorCode.INVENTORY_FULL;

                return added;
            },
            out var claimedItem);

        return new(status, rejection, attemptedItem, claimedItem, addedItem,
            discovererPlayerId, autoUsed, summonStonePickup,
            bootsPickup, healthRecovery);
    }
}

internal sealed record GroundItemPickupResult(
    GroundItemClaimStatus Status, ErrorCode Rejection,
    GroundItemInfo? AttemptedItem, GroundItemInfo? ClaimedItem, InGameItemInfo? AddedItem,
    long DiscovererPlayerId, bool AutoUsed, bool SummonStonePickup,
    bool BootsPickup, int HealthRecovery);
