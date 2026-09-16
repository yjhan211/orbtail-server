using game_server.matches;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.players;

internal sealed class PlayerHealthService(
    PlayerEliminationService eliminations,
    ILogger<PlayerHealthService> logger)
{
    // 수면 회복은 1초 준비 후 초당 최대 체력의 5%를 지급한다.
    public int GetSleepRecovery(Player player, DateTime nowUtc, int maxHealth)
    {
        if (player.IsEliminated)
        {
            player.TryStopSleep();
            return 0;
        }
        return player.StatusEffects.GetSleepRecovery(nowUtc, player.Health, maxHealth);
    }

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
        match.Bots.GetBot(player.PlayerId)?.UpdateWoundedState();
        if (change.Changed)
        {
            logger.LogInformation("Player {PlayerId} Health: {OldHealth}→{Health} ({Delta:+#;-#;0})", player.PlayerId, change.Before, change.After, change.RequestedDelta);
            if (change.Recovered > 0)
            {
                player.RecoveryTotal += change.Recovered;
            }
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
        match.Bots.GetBot(player.PlayerId)?.UpdateWoundedState();
        if (change.Changed)
        {
            logger.LogInformation("Player {PlayerId} Health: {OldHealth}→{Health} ({Delta:+#;-#;0})", player.PlayerId, change.Before, change.After, change.RequestedDelta);
            if (change.Recovered > 0)
            {
                player.RecoveryTotal += change.Recovered;
            }
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


    public void ApplySleepRecovery(MatchRuntime runtime, IEnumerable<Player> players, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Health changes require the match lock.");
        }
        foreach (var player in players)
        {
            int recovered = GetSleepRecovery(player, nowUtc, Config.MAX_HEALTH);
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
                AreaType = player.GameInfo.ObjectInfo.Area,
                Amount = change.Recovered,
                Source = HealthRecoveryKind.Sleep
            });
            player.Session.TrySend(packet);
        }
    }
}
