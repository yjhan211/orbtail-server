using game_server.matches;
using game_server.matches.logging;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.packets;

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
            eliminations.EliminatePlayer(match, player, EliminationReason.HEALTH_ZERO, attackerPlayerId: attackerId);
        }
    }

    public Player.HealthChange Recover(MatchRuntime match, Player player, int amount)
    {
        if (!Monitor.IsEntered(match.MatchLock))
        {
            throw new InvalidOperationException("Health changes require the match lock.");
        }
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        if (match.IsEnded || player.IsEliminated)
        {
            return new(player.Health, player.Health, amount);
        }
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

    public void ApplyPeriodicBuffs(MatchRuntime runtime, IReadOnlyList<Player> players, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Health changes require the match lock.");
        }
        foreach (var player in players)
        {
            if (runtime.IsEnded)
            {
                return;
            }
            if (player.IsEliminated)
            {
                player.ClearPeriodicBuffs();
                continue;
            }

            try
            {
                foreach (int delta in player.TakeDuePeriodicBuffDeltas(nowUtc, Config.MAX_HEALTH))
                {
                    if (delta >= 0)
                    {
                        Recover(runtime, player, delta);
                    }
                    else
                    {
                        ApplyDamage(runtime, player, checked(-delta));
                    }

                    if (player.IsEliminated || runtime.IsEnded)
                    {
                        player.ClearPeriodicBuffs();
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                player.ClearPeriodicBuffs();
                logger.LogWarning(ex, "Periodic buff processing failed: MatchingId={MatchingId}, PlayerId={PlayerId}", runtime.MatchingId, player.PlayerId);
            }
        }
    }

    public void ApplySleepRecovery(MatchRuntime runtime, IEnumerable<Player> players, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Health changes require the match lock.");
        }
        foreach (var player in players)
        {
            int recovered = player.GetSleepRecovery(nowUtc, player.IsEliminated, Config.MAX_HEALTH);
            if (recovered <= 0)
            {
                continue;
            }
            var change = Recover(runtime, player, recovered);
            if (player.Session == null)
            {
                continue;
            }
            using var packet = PacketMaker.G_TO_C_HEALTH_RECOVERY(new G_TO_C_HEALTH_RECOVERY
            {
                PlayerId = player.PlayerId,
                AreaType = player.CurrentArea,
                Amount = change.Recovered,
                Source = HealthRecoveryKind.Sleep
            });
            player.Session.TrySend(packet);
        }
    }
}
