using System.Linq;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.network;

public partial class GameClientSession
{
    private Task HandleSummonOrb(C_TO_G_SUMMON_ORB request)
    {
        if (!PlayerId.HasValue)
            return Task.CompletedTask;

        if (IsRoundActionLocked(out _))
        {
            SendSummonOrbResult(false, ErrorCode.INVALID_GAME_STATE, 0, 0,
                _summonStoneManager.GetSnapshot(CurrentMapSubId, PlayerId.Value));
            return Task.CompletedTask;
        }

        long playerId = PlayerId.Value;
        var attempt = _summonStoneManager.TrySummon(
            CurrentMapSubId,
            playerId,
            itemId => _inGameInventoryManager.TryAddItemWithCapacity(
                CurrentMapSubId,
                playerId,
                itemId,
                Config.SURVIVOR_INVENTORY_SLOT_COUNT,
                out var addedItem)
                ? addedItem
                : null);

        if (attempt.Success && attempt.AddedItem != null)
        {
            SendInGameInventoryUpdate(attempt.AddedItem);
            var inventory = _inGameInventoryManager.GetPlayerInventory(CurrentMapSubId, playerId);
            _gameEventLogManager.LogSurvivorOrbBoardTransition(
                CurrentMapSubId,
                playerId,
                inventory.GetAllItems(),
                inventory.GetEquippedBattleItem()?.ItemId ?? 0,
                CurrentArea.ToString(),
                "summon",
                isBot: false);
        }

        SendSummonOrbResult(
            attempt.Success,
            attempt.ErrorCode,
            attempt.ItemId,
            attempt.AddedItem?.ItemUid ?? 0,
            attempt.State);
        _gameEventLogManager.LogOrbSummonAttempt(
            CurrentMapSubId,
            playerId,
            attempt.Success,
            attempt.ErrorCode,
            attempt.ItemId,
            attempt.State.StoneCount,
            attempt.State.NextCost,
            attempt.State.SuccessfulSummonCount,
            CurrentArea.ToString(),
            isBot: false);

        Logger.LogInformation(
            "Orb summon request: MatchingId={MatchingId}, PlayerId={PlayerId}, Success={Success}, Error={ErrorCode}, ItemId={ItemId}, Stones={StoneCount}, NextCost={NextCost}",
            CurrentMapSubId,
            playerId,
            attempt.Success,
            attempt.ErrorCode,
            attempt.ItemId,
            attempt.State.StoneCount,
            attempt.State.NextCost);
        return Task.CompletedTask;
    }

    internal void SendSummonStoneState()
    {
        if (!PlayerId.HasValue || CurrentMapSubId <= 0)
            return;

        var state = _summonStoneManager.GetSnapshot(CurrentMapSubId, PlayerId.Value);
        using var packet = Packet.Create((int)Protocol.G_TO_C_SUMMON_STONE_STATE, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SUMMON_STONE_STATE
        {
            State = ToNetworkState(state)
        }));
        Send(packet);
    }

    private void SendSummonOrbResult(bool success, ErrorCode errorCode, int summonedItemId,
        long summonedItemUid, SummonStoneSnapshot state)
    {
        using var packet = Packet.Create((int)Protocol.G_TO_C_SUMMON_ORB_RESULT, PlayerId ?? 0);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SUMMON_ORB_RESULT
        {
            Success = success,
            ErrorCode = errorCode,
            SummonedItemId = summonedItemId,
            SummonedItemUid = summonedItemUid,
            State = ToNetworkState(state)
        }));
        Send(packet);
    }

    private SummonStoneStateInfo ToNetworkState(SummonStoneSnapshot state) => new()
    {
        StoneCount = state.StoneCount,
        SuccessfulSummonCount = state.SuccessfulSummonCount,
        NextCost = state.NextCost,
        PoolItemIds = _summonStoneManager.PoolItemIds.ToList()
    };
}
