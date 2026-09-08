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

        var response = new G_TO_C_ITEMS_COMBINED
        {
            ErrorCode = ErrorCode.INVALID_GAME_STATE,
            InputItemA = msg.ItemA,
            InputItemB = msg.ItemB
        };
        var match = Volatile.Read(ref _match);
        if (MatchingId <= 0 || match == null)
        {
            using var packet = Packet.Create((int)Protocol.G_TO_C_ITEMS_COMBINED, PlayerId.Value);
            packet.SetBody(MessagePackSerializer.Serialize(response));
            TrySend(packet);
            return Task.CompletedTask;
        }

        using (match.Enter())
        {
            IReadOnlyCollection<InGameItemInfo>? changedItems = null;
            if (!match.IsTerminal && !IsGameplayActionBlocked(out _))
            {
                var result = _itemCombinations.Combine(match, PlayerId.Value, CurrentArea, msg);
                response.ErrorCode = result.ErrorCode;
                response.RecipeId = result.RecipeId;
                response.OutputItemId = result.OutputItemId;
                changedItems = result.ChangedItems;
            }

            using var combinePacket = Packet.Create((int)Protocol.G_TO_C_ITEMS_COMBINED, PlayerId.Value);
            combinePacket.SetBody(MessagePackSerializer.Serialize(response));
            TrySend(combinePacket);
            if (response.ErrorCode != ErrorCode.SUCCESS)
            {
                return Task.CompletedTask;
            }

            var outputItem = changedItems!.LastOrDefault(item => item.ItemId == response.OutputItemId && item.Count > 0);
            var equippedBattleItem = match.Inventory.GetEquippedBattleItem(PlayerId.Value);
            bool shouldReplaceEquippedItem = outputItem != null && equippedBattleItem?.ItemUid == outputItem.ItemUid;

            if (changedItems != null)
            {
                using var inventoryPacket = PacketMaker.G_TO_C_INGAME_INVENTORY_UPDATE(changedItems.ToList());
                TrySend(inventoryPacket);
            }

            if (shouldReplaceEquippedItem)
            {
                using var equippedPacket = PacketMaker.G_TO_C_USE_INGAME_ITEM_RESULT(true, outputItem!.ItemUid, ErrorCode.SUCCESS);
                TrySend(equippedPacket);
            }
        }

        return Task.CompletedTask;
    }
}
