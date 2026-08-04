using game_server.services;
using network.common;
using network.common.data;

namespace game_server;

public partial class GameServer
{
    private void ProcessBotOrbSummons(long matchingId)
    {
        if (!game_server.network.GameClientSession.IsRoundActionPhase(matchingId))
            return;

        foreach (var bot in _botPlayerManager.GetBots(matchingId).Where(bot => !bot.IsEliminated))
        {
            if (!_survivorPhaseManager.AreOrbBoardActionsAllowed(matchingId, bot.CurrentArea))
                continue;

            TryDestroyBotOverflowOrb(matchingId, bot);

            while (true)
            {
                int choiceIndex = SelectBotSummonChoice(matchingId, bot);
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
                        : null,
                    choiceIndex);

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

    /// <summary>
    ///     봇의 소환 2택. 사람이 고를 법한 순서로 고른다 — 오염이 높은데 회복이 없으면 회복,
    ///     아니면 보드에 이미 있는 계열(머지 짝)을 우선한다. 둘 다 아니면 후보 0.
    /// </summary>
    private int SelectBotSummonChoice(long matchingId, BotPlayerState bot)
    {
        var candidates = _summonStoneManager.GetSummonCandidates(matchingId, bot.PlayerId);
        if (candidates.Length < 2 || candidates[0] == candidates[1])
            return 0;

        var boardItemIds = _inGameInventoryManager.GetPlayerInventory(matchingId, bot.PlayerId)
            .GetAllItems()
            .Where(item => item.Count > 0)
            .Select(item => item.ItemId)
            .ToList();

        bool needsRecovery = bot.Corruption >= Config.SURVIVOR_MAX_CORRUPTION * 2 / 5 &&
                             !boardItemIds.Any(SurvivorOrbData.IsRecoveryOrb);
        if (needsRecovery)
        {
            for (int index = 0; index < candidates.Length; index++)
            {
                if (SurvivorOrbData.IsRecoveryOrb(candidates[index]))
                    return index;
            }
        }

        for (int index = 0; index < candidates.Length; index++)
        {
            if (boardItemIds.Contains(candidates[index]))
                return index;
        }

        return 0;
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
