using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

public enum BotBehaviorActionKind
{
    None,
    Chat,
    FollowTarget,
    MoveToPortal,
    Explore
}

public sealed class BotBehaviorPlayerSnapshot
{
    public long PlayerId { get; init; }
    public long TargetPlayerId { get; init; }
    public AreaType CurrentArea { get; init; }
    public Vector3f? Position { get; init; }
    public bool IsEliminated { get; init; }
}

public sealed class BotPortalActionCandidate
{
    public AreaType TargetArea { get; init; }
    public ConnectionType ConnectionType { get; init; }
    public StairSide StairSide { get; init; }
    public int Distance { get; init; }
    public bool IsStairLike => ConnectionType == ConnectionType.Stair || StairSide != StairSide.None;
}

public sealed class BotBehaviorDecision
{
    public BotBehaviorActionKind Kind { get; init; }
    public long TargetPlayerId { get; init; }
    public AreaType TargetArea { get; init; }
    public BotPortalActionCandidate? PortalCandidate { get; init; }
    public string Reason { get; init; } = "";

    public static BotBehaviorDecision None(string reason = "") =>
        new() { Kind = BotBehaviorActionKind.None, Reason = reason };
}

public static class BotBehaviorDecisionService
{
    public static BotBehaviorDecision Decide(
        BotPlayerState bot,
        MapId mapId,
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

        var portalCandidate = GetPortalActionCandidates(mapId, bot.CurrentArea, bot.Cell, isAreaBlocked)
            .FirstOrDefault();
        if (portalCandidate != null)
        {
            return new BotBehaviorDecision
            {
                Kind = BotBehaviorActionKind.MoveToPortal,
                TargetArea = portalCandidate.TargetArea,
                PortalCandidate = portalCandidate,
                Reason = portalCandidate.IsStairLike ? "nearest-stair-or-door" : "nearest-door"
            };
        }

        return new BotBehaviorDecision
        {
            Kind = BotBehaviorActionKind.Explore,
            TargetArea = bot.CurrentArea,
            Reason = "no-player-or-portal-action"
        };
    }

    public static List<BotPortalActionCandidate> GetPortalActionCandidates(
        MapId mapId,
        AreaType currentArea,
        Cell currentCell,
        Func<AreaType, bool> isAreaBlocked)
    {
        var candidates = new List<BotPortalActionCandidate>();
        if (currentArea == AreaType.None) return candidates;

        foreach (var connection in GameAreaConnectionData.GetConnections(mapId, currentArea))
        {
            if (connection == null) continue;
            if (connection.ToArea == AreaType.None || connection.ToArea == currentArea) continue;
            if (isAreaBlocked(connection.ToArea)) continue;

            var exitCell = GameAreaConnectionData.GetSpawnCell(
                mapId,
                connection.ToArea,
                currentArea,
                connection.StairSide);

            int distance = !ReferenceEquals(exitCell, null)
                ? Math.Abs(exitCell.X - currentCell.X) + Math.Abs(exitCell.Y - currentCell.Y)
                : int.MaxValue;

            candidates.Add(new BotPortalActionCandidate
            {
                TargetArea = connection.ToArea,
                ConnectionType = connection.Type,
                StairSide = connection.StairSide,
                Distance = distance
            });
        }

        return candidates
            .OrderBy(c => c.Distance)
            .ThenBy(c => c.IsStairLike ? 0 : 1)
            .ThenBy(c => c.TargetArea.GetFloor())
            .ThenBy(c => c.TargetArea)
            .ToList();
    }
}
