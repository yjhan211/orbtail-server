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
}
