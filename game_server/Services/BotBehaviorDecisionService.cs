using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

public enum BotBehaviorActionKind
{
    None,
    Chat,
    FollowTarget,
    Explore
}

public sealed class BotBehaviorPlayerSnapshot
{
    public long PlayerId { get; init; }
    public long TargetPlayerId { get; init; }
    public AreaType CurrentArea { get; init; }
    public bool IsEliminated { get; init; }
}

public sealed class BotBehaviorDecision
{
    public BotBehaviorActionKind Kind { get; init; }
    public long TargetPlayerId { get; init; }
    public AreaType TargetArea { get; init; }
    public string Reason { get; init; } = "";

    public static BotBehaviorDecision None(string reason = "") =>
        new() { Kind = BotBehaviorActionKind.None, Reason = reason };
}

public static class BotBehaviorDecisionService
{
    public static BotBehaviorDecision Decide(
        BotPlayerState bot,
        IReadOnlyList<BotBehaviorPlayerSnapshot> players,
        Func<AreaType, bool> isAreaBlocked)
    {
        if (bot.IsEliminated) return BotBehaviorDecision.None("bot-eliminated");
        if (bot.IsInInteraction) return BotBehaviorDecision.None("bot-in-interaction");
        if (bot.CurrentArea == AreaType.None) return BotBehaviorDecision.None("area-none");

        var sameAreaPlayers = players
            .Where(p => !p.IsEliminated && p.CurrentArea == bot.CurrentArea)
            .ToList();

        var targetPlayer = sameAreaPlayers.FirstOrDefault(p => p.PlayerId == bot.TargetPlayerId);
        if (targetPlayer != null)
            return new BotBehaviorDecision
            {
                Kind = BotBehaviorActionKind.Chat,
                TargetPlayerId = targetPlayer.PlayerId,
                TargetArea = bot.CurrentArea,
                Reason = "bot-target-in-area"
            };

        var playerTargetingBot = sameAreaPlayers.FirstOrDefault(p => p.TargetPlayerId == bot.PlayerId);
        if (playerTargetingBot != null)
            return new BotBehaviorDecision
            {
                Kind = BotBehaviorActionKind.Chat,
                TargetPlayerId = playerTargetingBot.PlayerId,
                TargetArea = bot.CurrentArea,
                Reason = "player-targets-bot-in-area"
            };

        var knownTarget = players.FirstOrDefault(p => p.PlayerId == bot.TargetPlayerId && !p.IsEliminated);
        if (knownTarget != null
            && knownTarget.CurrentArea != AreaType.None
            && knownTarget.CurrentArea != bot.CurrentArea
            && !isAreaBlocked(knownTarget.CurrentArea))
        {
            return new BotBehaviorDecision
            {
                Kind = BotBehaviorActionKind.FollowTarget,
                TargetPlayerId = knownTarget.PlayerId,
                TargetArea = knownTarget.CurrentArea,
                Reason = "target-area-known"
            };
        }

        return new BotBehaviorDecision
        {
            Kind = BotBehaviorActionKind.Explore,
            TargetArea = bot.CurrentArea,
            Reason = "no-player-action"
        };
    }
}
