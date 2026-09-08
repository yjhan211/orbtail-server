using MessagePack;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.sessions;

public partial class GameClientSession
{
    private Task HandleCombineItems(C_TO_G_COMBINE_ITEMS msg)
    {
        if (!PlayerId.HasValue)
        {
            return Task.CompletedTask;
        }
        var match = Volatile.Read(ref _match);
        if (MatchingId <= 0 || match == null)
        {
            SendCombineItemsFailure(msg.ItemA, msg.ItemB, ErrorCode.INVALID_GAME_STATE);
            return Task.CompletedTask;
        }

        using (match.Enter())
        {
            if (match.IsTerminal || IsGameplayActionBlocked(out _))
            {
                SendCombineItemsFailure(msg.ItemA, msg.ItemB, ErrorCode.INVALID_GAME_STATE);
                return Task.CompletedTask;
            }

            var result = _itemCombinations.Combine(match, PlayerId.Value, CurrentArea, msg);
            if (result.ErrorCode != ErrorCode.SUCCESS)
            {
                SendCombineItemsFailure(msg.ItemA, msg.ItemB, result.ErrorCode);
                return Task.CompletedTask;
            }

            SendCombineItemsSuccess(msg, result.OutputItemId, result.ChangedItems!, result.RecipeId);
        }

        return Task.CompletedTask;
    }

    private void SendCombineItemsSuccess(C_TO_G_COMBINE_ITEMS msg, int outputItemId, IReadOnlyCollection<InGameItemInfo> changedItems, int recipeId)
    {
        var outputItem = changedItems.LastOrDefault(item => item.ItemId == outputItemId && item.Count > 0);
        var equippedBattleItem = Match.Inventory.GetEquippedBattleItem(PlayerId!.Value);
        bool shouldReplaceEquippedItem = outputItem != null && equippedBattleItem?.ItemUid == outputItem.ItemUid;

        using var combinePacket = Packet.Create((int)Protocol.G_TO_C_ITEMS_COMBINED, PlayerId.Value);
        var combinedMsg = new G_TO_C_ITEMS_COMBINED
        {
            RecipeId = recipeId,
            InputItemA = msg.ItemA,
            InputItemB = msg.ItemB,
            OutputItemId = outputItemId
        };
        combinePacket.SetBody(MessagePackSerializer.Serialize(combinedMsg));
        TrySend(combinePacket);

        using var inventoryPacket = PacketMaker.G_TO_C_INGAME_INVENTORY_UPDATE(changedItems.ToList());
        TrySend(inventoryPacket);

        if (!shouldReplaceEquippedItem)
        {
            return;
        }
        using var equippedPacket = PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(true, outputItem!.ItemUid, ErrorCode.SUCCESS);
        TrySend(equippedPacket);
    }

    private void SendCombineItemsFailure(int partA, int partB, ErrorCode errorCode)
    {
        using var failPacket = Packet.Create((int)Protocol.G_TO_C_ITEMS_COMBINED, PlayerId!.Value);
        var failMsg = new G_TO_C_ITEMS_COMBINED
        {
            RecipeId = 0,
            InputItemA = partA,
            InputItemB = partB,
            OutputItemId = 0
        };
        failPacket.SetBody(MessagePackSerializer.Serialize(failMsg));
        TrySend(failPacket);

        SendErrorResponse(errorCode, "부품 결합 실패");
    }
}
