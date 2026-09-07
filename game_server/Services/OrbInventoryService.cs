using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
///     매치 잠금 안에서 오브 소환·파괴의 인벤토리와 소환석 변경을 함께 처리한다.
///     클라이언트 요청 상태·응답·로그 발행 순서는 세션이 관리한다.
/// </summary>
internal static class OrbInventoryService
{
    public static int GetDraftItem(int choiceIndex, int tier) =>
        OrbData.ApplyDraftTier(choiceIndex switch
        {
            1 => 107000030,
            2 => 107000020,
            _ => 107000010
        }, tier);

    public static SummonOrbAttempt Summon(MatchRuntime runtime, long playerId,
        int choiceIndex, int? costOverride, int? exactItemId)
    {
        RequireLock(runtime);
        return runtime.SummonStones.TrySummon(playerId,
            itemId => runtime.Inventory.TryAddItemWithCapacity(
                playerId, itemId, Config.SWARM_ORB_CAPACITY, out var added) ? added : null,
            choiceIndex, costOverride, exactItemId);
    }

    public static OrbDestroyResult Destroy(MatchRuntime runtime, long playerId, long itemUid)
    {
        RequireLock(runtime);
        var inventory = runtime.Inventory.GetPlayerInventory(playerId);
        var item = inventory.GetItem(itemUid);
        if (item == null || item.Count <= 0)
            return Failed(ErrorCode.ITEM_NOT_FOUND);
        bool valid = OrbData.TryGetColorAndTier(item.ItemId, out _, out int tier) ||
                     OrbData.TryGetRecoveryTier(item.ItemId, out tier);
        if (!valid) return Failed(ErrorCode.ITEM_NOT_USABLE);
        if (!runtime.Inventory.TryRemoveItem(playerId, itemUid, 1, out var removed) || removed == null)
            return Failed(ErrorCode.ITEM_NOT_OWNED);
        int refund = Config.SWARM_ORB_DESTROY_REFUND_STONES;
        var state = runtime.SummonStones.AddStones(playerId, refund);
        return new(ErrorCode.SUCCESS, removed, item.ItemId, tier, refund, state);

        OrbDestroyResult Failed(ErrorCode error) => new(error, null, 0, 0, 0, runtime.SummonStones.GetSnapshot(playerId));
    }

    public static void Grant(MatchRuntime runtime, long playerId, int itemId)
    {
        RequireLock(runtime);
        // 같은 종류라도 오브 하나마다 독립 ItemUid를 유지한다.
        runtime.Inventory.GetPlayerInventory(playerId)
            .TryAddItemWithCapacity(itemId, Config.SWARM_ORB_CAPACITY, out _);
    }

    private static void RequireLock(MatchRuntime runtime)
    {
        if (!Monitor.IsEntered(runtime.Sync))
            throw new InvalidOperationException("Orb inventory changes require the match lock.");
    }
}

internal sealed record OrbDestroyResult(
    ErrorCode Error, InGameItemInfo? RemovedItem, int ItemId, int Tier,
    int RefundedStones, SummonStoneSnapshot State);
