using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
///     사람의 오브·배틀아이템 조합 조건을 검사하고 매치 인벤토리와 합성 난수를 사용해 결과를 확정한다.
///     호출자가 매치 잠금을 소유해야 하며, 세션이나 TCP 연결은 보관하지 않는다.
///     패킷 전송은 전달받은 콜백에 맡긴다. 기존 순서대로 결과를 발행한 뒤 로그를 기록한다.
/// </summary>
internal sealed class ItemCombinationService(GameEventLogManager eventLogs)
{
    public bool TryCombine(
        MatchRuntime runtime,
        long playerId,
        AreaType area,
        C_TO_G_COMBINE_ITEMS msg,
        Action<int, IReadOnlyCollection<InGameItemInfo>, int> publishResult,
        Action<ErrorCode> publishFailure)
    {
        if (!Monitor.IsEntered(runtime.Sync))
            throw new InvalidOperationException("Item combination requires the match lock.");
        long matchingId = runtime.MatchingId;
        bool isOrbRequest = OrbData.IsOrbItem(msg.ItemA) ||
                                    OrbData.IsOrbItem(msg.ItemB);
        if (isOrbRequest)
        {
            var inventory = runtime.Inventory.GetPlayerInventory(playerId);
            if (!OrbData.CanMerge(msg.ItemA, msg.ItemB) ||
                !inventory.HasItems([msg.ItemA, msg.ItemB]))
            {
                publishFailure(ErrorCode.INVALID_PARAMETER);
                return true;
            }

            bool hadResonance = inventory.TryGetActiveOrbPair(out OrbColor previousResonanceColor,
                out int previousSupportTier);
            bool combined = runtime.Inventory.TryCombineOrbs(
                playerId,
                msg.ItemA,
                msg.ItemB,
                runtime.Swarm.ItemCombineRandom,
                out int outputItemId,
                out List<InGameItemInfo> changedItems);
            if (!combined)
            {
                publishFailure(ErrorCode.INVALID_PARAMETER);
                return true;
            }

            publishResult(outputItemId, changedItems, 0);

            bool resonanceActive = inventory.TryGetActiveOrbPair(out OrbColor resonanceColor,
                out int supportTier);
            OrbData.TryGetColorAndTier(outputItemId, out OrbColor outputColor, out int outputTier);
            eventLogs.LogOrbBoardTransition(
                matchingId, playerId, inventory.GetAllItems(),
                inventory.GetEquippedBattleItem()?.ItemId ?? 0, area.ToString(), "merge", isBot: false);
            eventLogs.LogMission(
                matchingId,
                playerId,
                $"SURVIVOR_ORB_MERGE inputs=[{msg.ItemA},{msg.ItemB}] output={outputItemId} " +
                $"outputColor={outputColor} outputTier={outputTier} " +
                $"resonanceBefore={(hadResonance ? previousResonanceColor.ToString() : "off")}/T{previousSupportTier} " +
                $"resonanceAfter={(resonanceActive ? resonanceColor.ToString() : "off")}/T{supportTier} " +
                $"area={area} nextArea=pending",
                isBot: false);

            var outputCombatData = BattleItemCombatData.Get(outputItemId);
            eventLogs.LogTierReached(
                matchingId,
                playerId,
                outputItemId,
                outputCombatData?.Tier ?? 0,
                isBot: false);
            return true;
        }

        var candidates = BattleItemRecipeData.GetAvailableRecipes(
            [msg.ItemA, msg.ItemB],
            area);
        if (candidates.Count == 0)
            return false;

        var recipeInventory = runtime.Inventory.GetPlayerInventory(playerId);
        if (!recipeInventory.HasItems(candidates[0].InputItemIds))
        {
            publishFailure(ErrorCode.INSUFFICIENT_ITEM);
            return true;
        }

        if (!runtime.Inventory.TryCombineRandomRecipe(
                playerId,
                candidates,
                runtime.Swarm.ItemCombineRandom,
                out BattleItemRecipe? recipe,
                out List<InGameItemInfo> recipeChangedItems))
        {
            publishFailure(ErrorCode.INSUFFICIENT_ITEM);
            return true;
        }

        if (recipe == null)
            throw new InvalidOperationException("Successful random recipe combine did not select a recipe.");

        publishResult(recipe.OutputItemId, recipeChangedItems, recipe.RecipeId);
        eventLogs.LogMission(matchingId, playerId,
            $"Battle item combine: {msg.ItemA} + {msg.ItemB} => {recipe.OutputItemId}",
            isBot: false);

        var combinedCombatData = BattleItemCombatData.Get(recipe.OutputItemId);
        eventLogs.LogTierReached(
            matchingId,
            playerId,
            recipe.OutputItemId,
            combinedCombatData?.Tier ?? 0,
            isBot: false);
        return true;
    }

}
