using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
///     배틀아이템 레시피와 재료를 검사하고 매치 인벤토리와 합성 난수를 사용해 결과를 확정한다.
///     호출자가 매치 잠금을 소유해야 하며, 세션이나 TCP 연결은 보관하지 않는다.
///     조합과 로그 기록을 마친 뒤 결과를 반환하며, 패킷 전송은 호출자가 담당한다.
/// </summary>
internal sealed class ItemCombinationService(GameEventLogManager eventLogs)
{
    public sealed record CombinationResult(
        ErrorCode ErrorCode,
        int OutputItemId = 0,
        IReadOnlyCollection<InGameItemInfo>? ChangedItems = null,
        int RecipeId = 0);

    public CombinationResult Combine(
        MatchRuntime runtime,
        long playerId,
        AreaType area,
        C_TO_G_COMBINE_ITEMS msg)
    {
        if (!Monitor.IsEntered(runtime.Sync))
            throw new InvalidOperationException("Item combination requires the match lock.");
        long matchingId = runtime.MatchingId;
        if (OrbData.IsOrbItem(msg.ItemA) || OrbData.IsOrbItem(msg.ItemB))
            return new CombinationResult(ErrorCode.INVALID_PARAMETER);

        var candidates = BattleItemRecipeData.GetAvailableRecipes(
            [msg.ItemA, msg.ItemB],
            area);
        if (candidates.Count == 0)
            return new CombinationResult(ErrorCode.INSUFFICIENT_ITEM);

        var recipeInventory = runtime.Inventory.GetPlayerInventory(playerId);
        if (!recipeInventory.HasItems(candidates[0].InputItemIds))
        {
            return new CombinationResult(ErrorCode.INSUFFICIENT_ITEM);
        }

        if (!runtime.Inventory.TryCombineRandomRecipe(
                playerId,
                candidates,
                runtime.Swarm.ItemCombineRandom,
                out BattleItemRecipe? recipe,
                out List<InGameItemInfo> recipeChangedItems))
        {
            return new CombinationResult(ErrorCode.INSUFFICIENT_ITEM);
        }

        if (recipe == null)
            throw new InvalidOperationException("Successful random recipe combine did not select a recipe.");

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
        return new CombinationResult(ErrorCode.SUCCESS, recipe.OutputItemId, recipeChangedItems, recipe.RecipeId);
    }

}
