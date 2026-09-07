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

namespace game_server.sessions;

/// <summary>
///     사람 아이템 조합 요청을 받아 ItemCombinationService를 매치 잠금 안에서 호출하고
///     결과/실패 bundle을 같은 순서로 보낸다.
/// </summary>
public partial class GameClientSession
{
    /// <summary>
    ///     조합 요청의 outer 경계. PlayerId가 없으면 조용히 끝내고 matchingId가 없으면 터미널과 같은
    ///     INVALID_GAME_STATE 거부 bundle을 보낸다 (매치 밖 조합 core는 없다). 유효한 매치는 잠금 안에서
    ///     core를 돌리고, 터미널이면 같은 거부 bundle을 보낸다.
    ///     ClientStartUnixMs는 legacy payload key/Int64 shape 보존 필드이며 순서 결정에는 사용하지 않는다.
    /// </summary>
    private Task HandleCombineItems(C_TO_G_COMBINE_ITEMS msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;
        if (MatchingId <= 0)
        {
            SendCombineItemsFailure(msg.ItemA, msg.ItemB, ErrorCode.INVALID_GAME_STATE);
            return Task.CompletedTask;
        }

        return RunUnderMatch(
            () => HandleCombineItemsCore(msg),
            () => SendCombineItemsFailure(msg.ItemA, msg.ItemB, ErrorCode.INVALID_GAME_STATE));
    }

    /// <summary>
    ///     매치 잠금 안에서 동기 완료해야 하는 권위 조합 core.
    ///     미션 부품 결합은 퇴역(#238)했고 배틀아이템·오브 조합과 기존 packet/log 순서만 유지한다.
    /// </summary>
    private Task HandleCombineItemsCore(C_TO_G_COMBINE_ITEMS msg)
    {
        if (IsRoundActionLocked(out _))
        {
            SendCombineItemsFailure(msg.ItemA, msg.ItemB, ErrorCode.INVALID_GAME_STATE);
            return Task.CompletedTask;
        }

        if (_itemCombinations.TryCombine(
                Match, PlayerId!.Value, CurrentArea, msg,
                (outputItemId, changedItems, recipeId) =>
                    SendBattleItemCombineResult(msg, outputItemId, changedItems, recipeId),
                errorCode => SendCombineItemsFailure(msg.ItemA, msg.ItemB, errorCode)))
            return Task.CompletedTask;

        SendCombineItemsFailure(msg.ItemA, msg.ItemB, ErrorCode.INSUFFICIENT_ITEM);
        return Task.CompletedTask;
    }

    private void SendBattleItemCombineResult(C_TO_G_COMBINE_ITEMS msg, int outputItemId,
        IReadOnlyCollection<InGameItemInfo> changedItems, int recipeId)
    {
        var outputItem = changedItems.LastOrDefault(item => item.ItemId == outputItemId && item.Count > 0);
        var equippedBattleItem = Match.Inventory.GetEquippedBattleItem(PlayerId!.Value);
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
            IsRaceComplete = false
        };
        combinePacket.SetBody(MessagePackSerializer.Serialize(combinedMsg));
        TrySend(combinePacket);

        // Inventory update follows the result packet so the client reveals the server-authoritative outcome.
        using var inventoryPacket = PacketMaker.G_TO_C_INGAME_INVENTORY_UPDATE(changedItems.ToList());
        TrySend(inventoryPacket);

        if (shouldReplaceEquippedItem)
        {
            using var equippedPacket = PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(
                true, outputItem!.ItemUid, ErrorCode.SUCCESS);
            TrySend(equippedPacket);
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
            IsRaceComplete = false
        };
        failPacket.SetBody(MessagePackSerializer.Serialize(failMsg));
        TrySend(failPacket);

        SendErrorResponse(errorCode, "부품 결합 실패");
    }
}
