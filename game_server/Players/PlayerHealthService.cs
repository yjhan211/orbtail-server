using game_server.logging;
using game_server.matches;
using Microsoft.Extensions.Logging;
using network.common;

namespace game_server.players;

internal sealed class PlayerHealthService(
    GameEventLogManager eventLogs,
    PlayerEliminationService eliminations,
    ILogger<PlayerHealthService> logger)
{
    public void ApplyDamage(MatchRuntime match, Player player, int damage, long attackerId = 0, bool handleElimination = true)
    {
        if (!Monitor.IsEntered(match.MatchLock))
        {
            throw new InvalidOperationException("Health changes require the match lock.");
        }
        ArgumentOutOfRangeException.ThrowIfNegative(damage);
        if (match.IsEnded || player.IsEliminated)
        {
            return;
        }

        var change = player.ApplyDamage(damage);
        if (change.Changed)
        {
            logger.LogInformation("Player {PlayerId} Health: {OldHealth}→{Health} ({Delta:+#;-#;0})", player.PlayerId, change.Before, change.After, change.RequestedDelta);
            if (change.Recovered > 0)
            {
                eventLogs.RecordRecovery(match.MatchingId, player.PlayerId, change.Recovered);
            }
            eventLogs.LogResource(match.MatchingId, player.PlayerId, change.RequestedDelta, change.After, reason: "", isBot: player.PlayerId < 0);
            try
            {
                player.Session?.SendHealth(change);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Health notification failed: MatchingId={MatchingId}, PlayerId={PlayerId}", match.MatchingId, player.PlayerId);
            }
        }
        if (handleElimination && change.IsDepleted && !match.IsEnded && !player.IsEliminated)
        {
            eliminations.EliminatePlayer(match.MatchingId, player.PlayerId, EliminationReason.HEALTH_ZERO, attackerPlayerId: attackerId);
        }
    }

    public Player.HealthChange Recover(MatchRuntime match, Player player, int amount)
    {
        if (!Monitor.IsEntered(match.MatchLock))
        {
            throw new InvalidOperationException("Health changes require the match lock.");
        }
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        if (match.IsEnded || player.IsEliminated) return new(player.Health, player.Health, amount);
        var change = player.Recover(amount);
        if (change.Changed)
        {
            logger.LogInformation("Player {PlayerId} Health: {OldHealth}→{Health} ({Delta:+#;-#;0})", player.PlayerId, change.Before, change.After, change.RequestedDelta);
            if (change.Recovered > 0)
            {
                eventLogs.RecordRecovery(match.MatchingId, player.PlayerId, change.Recovered);
            }
            eventLogs.LogResource(match.MatchingId, player.PlayerId, change.RequestedDelta, change.After, reason: "", isBot: player.PlayerId < 0);
            try
            {
                player.Session?.SendHealth(change);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Health notification failed: MatchingId={MatchingId}, PlayerId={PlayerId}", match.MatchingId, player.PlayerId);
            }
        }
        return change;
    }
}
