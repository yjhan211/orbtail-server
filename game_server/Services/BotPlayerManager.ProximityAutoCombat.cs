using Microsoft.Extensions.Logging;
using network.common;

namespace game_server.services;

public partial class BotPlayerManager
{
    public void ApplyProximityAutoCombatDamage(BotPlayerState bot, int damage)
    {
        if (bot.IsEliminated || damage <= 0)
            return;

        bot.Corruption = Math.Clamp(bot.Corruption + damage, 0, 100);
    }

    public bool TryFinalizeProximityAutoCombatElimination(BotPlayerState bot, long matchingId)
    {
        if (bot.IsEliminated || bot.Corruption < 100)
            return false;

        bot.IsEliminated = true;
        bot.IsForcedFollowActive = false;
        bot.Path.Clear();
        bot.PathIndex = 0;
        bot.PendingRngInteractId = 0;
        bot.PendingChecklistTaskId = 0;
        bot.PendingChecklistInteractId = 0;
        bot.ChecklistActivityProgressStartTime = DateTime.MinValue;
        bot.RngCollectProgressStartTime = DateTime.MinValue;
        ClearBotRoomExplorePlan(bot);
        bot.LoopWaitUntil = DateTime.MinValue;

        _logger.LogInformation(
            "Bot eliminated by proximity auto combat: MatchingId={MatchingId}, BotId={BotId}, Corruption={Corruption}",
            matchingId,
            bot.PlayerId,
            bot.Corruption);
        return true;
    }
}
