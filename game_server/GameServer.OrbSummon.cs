using game_server.services;
using network.common;

namespace game_server;

public partial class GameServer
{
    private void ProcessBotOrbSummons(long matchingId)
    {
        if (!game_server.network.GameClientSession.IsRoundActionPhase(matchingId))
            return;

        foreach (var bot in _botPlayerManager.GetBots(matchingId).Where(bot => !bot.IsEliminated))
        {
            TryDestroyBotOverflowOrb(matchingId, bot);

            while (true)
            {
                var attempt = _summonStoneManager.TrySummon(
                    matchingId,
                    bot.PlayerId,
                    itemId => _inGameInventoryManager.TryAddItemWithCapacity(
                        matchingId,
                        bot.PlayerId,
                        itemId,
                        Config.SURVIVOR_INVENTORY_SLOT_COUNT,
                        out var addedItem)
                        ? addedItem
                        : null);

                if (!attempt.Success || attempt.AddedItem == null)
                    break;

                var inventory = _inGameInventoryManager.GetPlayerInventory(matchingId, bot.PlayerId);
                _gameEventLogManager.LogSurvivorOrbBoardTransition(
                    matchingId,
                    bot.PlayerId,
                    inventory.GetAllItems(),
                    inventory.GetEquippedBattleItem()?.ItemId ?? 0,
                    bot.CurrentArea.ToString(),
                    "bot_summon",
                    isBot: true);
                _gameEventLogManager.LogOrbSummonAttempt(
                    matchingId,
                    bot.PlayerId,
                    true,
                    ErrorCode.SUCCESS,
                    attempt.ItemId,
                    attempt.State.StoneCount,
                    attempt.State.NextCost,
                    attempt.State.SuccessfulSummonCount,
                    bot.CurrentArea.ToString(),
                    isBot: true);
                _gameEventLogManager.LogSystem(
                    matchingId,
                    $"Bot orb summon: PlayerId={bot.PlayerId}, ItemId={attempt.ItemId}, " +
                    $"Stones={attempt.State.StoneCount}, NextCost={attempt.State.NextCost}");
            }
        }
    }

    private void TryDestroyBotOverflowOrb(long matchingId, BotPlayerState bot)
    {
        var inventory = _inGameInventoryManager.GetPlayerInventory(matchingId, bot.PlayerId);
        if (inventory.GetAllItems().Count(item => item.Count > 0) < Config.SURVIVOR_INVENTORY_SLOT_COUNT)
            return;

        var decision = BotBattleItemLoadout.SelectOverflowDestroyCandidate(
            inventory,
            bot.Corruption,
            Config.SURVIVOR_MAX_CORRUPTION);
        if (decision == null)
            return;

        var summonState = _summonStoneManager.GetSnapshot(matchingId, bot.PlayerId);
        int refundedStones = Math.Clamp(decision.Tier, 1, 3);
        if (summonState.StoneCount + refundedStones < summonState.NextCost)
            return;

        if (!_inGameInventoryManager.TryRemoveItem(
                matchingId,
                bot.PlayerId,
                decision.ItemUid,
                1,
                out _))
        {
            return;
        }

        var state = _summonStoneManager.AddStones(matchingId, bot.PlayerId, refundedStones);
        _gameEventLogManager.LogSurvivorOrbBoardTransition(
            matchingId,
            bot.PlayerId,
            inventory.GetAllItems(),
            inventory.GetEquippedBattleItem()?.ItemId ?? 0,
            bot.CurrentArea.ToString(),
            "bot_destroy",
            isBot: true);
        _gameEventLogManager.LogSystem(
            matchingId,
            $"Bot overflow orb destroyed: PlayerId={bot.PlayerId}, ItemId={decision.ItemId}, " +
            $"Tier={decision.Tier}, Color={decision.Color}, KeepScore={decision.KeepScore}, " +
            $"RefundedStones={refundedStones}, Stones={state.StoneCount}, NextCost={state.NextCost}");
    }
}
