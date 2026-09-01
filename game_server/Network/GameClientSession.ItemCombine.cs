using System;
using System.Collections.Generic;
using System.Linq;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;
using network.packets;

namespace game_server.network;

/// <summary>
///     사람 아이템 조합 (C_TO_G_COMBINE_ITEMS): 배틀아이템·오브 조합을 match별 ordered
///     publication turn에서 준비하고 결과/실패 bundle을 기존 순서로 발행한다.
/// </summary>
public partial class GameClientSession
{
    /// <summary>
    ///     조합 요청의 outer 경계. PlayerId가 없으면 조용히 끝내고 matchingId가 없으면 기존
    ///     core를 ordered lane 밖에서 직접 실행한다. 유효한 매치는 SessionBase의 outer operation
    ///     lease를 빌려 ordered publisher에 active core와 Finalizing rejection을 함께 넘긴다.
    /// </summary>
    private Task HandleCombineItems(C_TO_G_COMBINE_ITEMS msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;
        if (CurrentMapSubId <= 0)
            return HandleCombineItemsCore(msg);

        return PublishOrderedSessionAction(
            () => HandleCombineItemsCore(msg),
            () => SendCombineItemsFailure(msg.ItemA, msg.ItemB, ErrorCode.INVALID_GAME_STATE));
    }

    /// <summary>
    ///     매치 monitor와 publication capture 안에서 동기 완료해야 하는 권위 조합 core.
    ///     미션 부품 결합은 퇴역(#238)했고 배틀아이템·오브 조합과 기존 packet/log 순서만 유지한다.
    /// </summary>
    private Task HandleCombineItemsCore(C_TO_G_COMBINE_ITEMS msg)
    {
        if (IsRoundActionLocked(out _))
        {
            SendCombineItemsFailure(msg.ItemA, msg.ItemB, ErrorCode.INVALID_GAME_STATE);
            return Task.CompletedTask;
        }

        if (TryHandleBattleItemCombine(msg))
            return Task.CompletedTask;

        SendCombineItemsFailure(msg.ItemA, msg.ItemB, ErrorCode.INSUFFICIENT_ITEM);
        return Task.CompletedTask;
    }

    private bool TryHandleBattleItemCombine(C_TO_G_COMBINE_ITEMS msg)
    {
        if (!PlayerId.HasValue) return false;

        bool isOrbRequest = OrbData.IsOrbItem(msg.ItemA) ||
                                    OrbData.IsOrbItem(msg.ItemB);
        if (isOrbRequest)
        {
            var inventory = _inGameInventoryManager.GetPlayerInventory(CurrentMapSubId, PlayerId.Value);
            bool hadResonance = inventory.TryGetActiveOrbPair(out OrbColor previousResonanceColor,
                out int previousSupportTier);
            if (!_inGameInventoryManager.TryCombineOrbs(
                    CurrentMapSubId,
                    PlayerId.Value,
                    msg.ItemA,
                    msg.ItemB,
                    Random.Shared,
                    out int outputItemId,
                    out var changedItems))
            {
                SendCombineItemsFailure(msg.ItemA, msg.ItemB, ErrorCode.INVALID_PARAMETER);
                return true;
            }

            SendBattleItemCombineResult(msg, outputItemId, changedItems, recipeId: 0);

            bool resonanceActive = inventory.TryGetActiveOrbPair(out OrbColor resonanceColor,
                out int supportTier);
            OrbData.TryGetColorAndTier(outputItemId, out OrbColor outputColor, out int outputTier);
            _gameEventLogManager.LogOrbBoardTransition(
                CurrentMapSubId, PlayerId.Value, inventory.GetAllItems(),
                inventory.GetEquippedBattleItem()?.ItemId ?? 0, CurrentArea.ToString(), "merge", isBot: false);
            _gameEventLogManager.LogMission(
                CurrentMapSubId,
                PlayerId.Value,
                $"SURVIVOR_ORB_MERGE inputs=[{msg.ItemA},{msg.ItemB}] output={outputItemId} " +
                $"outputColor={outputColor} outputTier={outputTier} " +
                $"resonanceBefore={(hadResonance ? previousResonanceColor.ToString() : "off")}/T{previousSupportTier} " +
                $"resonanceAfter={(resonanceActive ? resonanceColor.ToString() : "off")}/T{supportTier} " +
                $"area={CurrentArea} nextArea=pending",
                isBot: false);

            var outputCombatData = BattleItemCombatData.Get(outputItemId);
            _gameEventLogManager.LogTierReached(
                CurrentMapSubId,
                PlayerId.Value,
                outputItemId,
                outputCombatData?.Tier ?? 0,
                isBot: false);
            return true;
        }

        var recipe = BattleItemRecipeData.PickRandomRecipe(
            new[] { msg.ItemA, msg.ItemB },
            CurrentArea,
            Random.Shared);
        if (recipe == null)
            return false;

        if (!_inGameInventoryManager.TryCombineItems(
                CurrentMapSubId,
                PlayerId.Value,
                recipe.InputItemIds,
                recipe.OutputItemId,
                out var legacyChangedItems))
        {
            SendCombineItemsFailure(msg.ItemA, msg.ItemB, ErrorCode.INSUFFICIENT_ITEM);
            return true;
        }

        SendBattleItemCombineResult(msg, recipe.OutputItemId, legacyChangedItems, recipe.RecipeId);
        _gameEventLogManager.LogMission(CurrentMapSubId, PlayerId.Value,
            $"Battle item combine: {msg.ItemA} + {msg.ItemB} => {recipe.OutputItemId}",
            isBot: false);

        var combinedCombatData = BattleItemCombatData.Get(recipe.OutputItemId);
        _gameEventLogManager.LogTierReached(
            CurrentMapSubId,
            PlayerId.Value,
            recipe.OutputItemId,
            combinedCombatData?.Tier ?? 0,
            isBot: false);
        return true;
    }

    private void SendBattleItemCombineResult(C_TO_G_COMBINE_ITEMS msg, int outputItemId,
        IReadOnlyCollection<InGameItemInfo> changedItems, int recipeId)
    {
        var outputItem = changedItems.LastOrDefault(item => item.ItemId == outputItemId && item.Count > 0);
        var equippedBattleItem = _inGameInventoryManager.GetEquippedBattleItem(CurrentMapSubId, PlayerId!.Value);
        bool shouldReplaceEquippedItem = outputItem != null && equippedBattleItem?.ItemUid == outputItem.ItemUid;

        using var combinePacket = Packet.Create((int)Protocol.G_TO_C_ITEMS_COMBINED, PlayerId.Value);
        var itemData = GameItemData.Get(outputItemId);
        var combinedMsg = new G_TO_C_ITEMS_COMBINED
        {
            RecipeId = recipeId,
            InputItemA = msg.ItemA,
            InputItemB = msg.ItemB,
            OutputItemId = outputItemId,
            OutputItemName = itemData?.Name?.Kr ?? "",
            StaminaReward = 0,
            IsRaceComplete = false
        };
        combinePacket.SetBody(MessagePackSerializer.Serialize(combinedMsg));
        Send(combinePacket);

        // Inventory update follows the result packet so the client reveals the server-authoritative outcome.
        using var inventoryPacket = PacketMaker.G_TO_C_INGAME_INVENTORY_UPDATE(changedItems.ToList());
        Send(inventoryPacket);

        if (shouldReplaceEquippedItem)
        {
            using var equippedPacket = PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(
                true, outputItem!.ItemUid, ErrorCode.SUCCESS);
            Send(equippedPacket);
        }
    }

    private void SendCombineItemsFailure(int partA, int partB, ErrorCode errorCode)
    {
        using var failPacket = Packet.Create((int)Protocol.G_TO_C_ITEMS_COMBINED, PlayerId!.Value);
        var failMsg = new G_TO_C_ITEMS_COMBINED
        {
            RecipeId = 0,
            InputItemA = partA,
            InputItemB = partB,
            OutputItemId = 0,
            OutputItemName = "",
            StaminaReward = 0,
            IsRaceComplete = false
        };
        failPacket.SetBody(MessagePackSerializer.Serialize(failMsg));
        Send(failPacket);

        SendErrorResponse(errorCode, "부품 결합 실패");
    }
}
