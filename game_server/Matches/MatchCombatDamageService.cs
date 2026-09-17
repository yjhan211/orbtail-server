using game_server.matches.monsters;
using game_server.players;
using game_server.sessions;
using MessagePack;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.matches;

/// <summary>
///     플레이어·몬스터 피해와 지연 타격을 처리하고, 틱 끝에 보낼 피격 알림을 모은다.
///     플레이어의 체력 변경·탈락 처리는 PlayerHealthService에 위임한다.
/// </summary>
internal sealed class MatchCombatDamageService(MonsterCombatService monsters)
{
    private static bool RollCritical(MatchRuntime runtime, double chance) => runtime.CombatDamage.CriticalRng.NextDouble() < chance;

    public void ScheduleMonsterHit(MatchRuntime runtime, PendingMonsterHit hit)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat damage requires the match lock.");
        }
        runtime.CombatDamage.PendingMonsterHits.Add(hit);
    }

    internal static int ConsumeSwarmPvpDamage(Player victim, int rawDamage)
    {
        float total = victim.PvpDamageCarry + rawDamage * Config.SWARM_PVP_DAMAGE_PER_DAMAGE;
        int whole = (int)total;
        victim.PvpDamageCarry = total - whole;
        return whole;
    }

    public void QueuePlayerHitNotification(MatchRuntime runtime, Player? attacker, long targetPlayerId, AreaType area, int weaponItemId, int damage, int targetHealth, bool isPeriodicDamage = false)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat damage requires the match lock.");
        }
        if (attacker == null || attacker.PlayerId == 0 || targetPlayerId == 0)
        {
            return;
        }

        var hit = new G_TO_C_COMBAT_HIT
        {
            AttackerId = attacker.PlayerId,
            TargetId = targetPlayerId,
            AreaType = area,
            WeaponItemId = weaponItemId,
            Damage = damage,
            AttackerHealth = attacker.Health,
            TargetHealth = targetHealth,
            IsDot = isPeriodicDamage
        };
        if (attacker.Session != null)
        {
            runtime.PendingCombatHits.Enqueue((attacker.Session, hit));
        }
    }

    public void QueueMonsterHitNotification(MatchRuntime runtime, Player? attacker, int monsterId, AreaType area,
        int weaponItemId, int damage, bool critical = false, bool showDamageOnly = false)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat damage requires the match lock.");
        }

        if (attacker == null || attacker.PlayerId == 0 || attacker.IsEliminated || monsterId < 0 || weaponItemId <= 0 || damage <= 0)
        {
            return;
        }
        var hit = new G_TO_C_COMBAT_HIT
        {
            AttackerId = attacker.PlayerId,
            TargetId = monsterId,
            TargetKind = CombatEntityKind.Monster,
            AreaType = area,
            WeaponItemId = weaponItemId,
            Damage = damage,
            AttackerHealth = attacker.Health,
            IsCritical = critical,
            ShowDamageOnly = showDamageOnly
        };
        if (attacker.Session != null)
        {
            runtime.PendingCombatHits.Enqueue((attacker.Session, hit));
        }
    }

    public void ApplyProximityAutoCombatHit(MatchRuntime runtime, PlayerHealthService healthService, Player victim, long sourcePlayerId, AreaType area, int weaponItemId, int damage, bool isPeriodicDamage = false, int sourceHealth = -1)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat damage requires the match lock.");
        }
        if (runtime.IsEnded || victim.IsEliminated || damage <= 0)
        {
            return;
        }

        RecordCombatContact(runtime, victim, sourcePlayerId, DateTime.UtcNow);
        if (runtime.GetPlayer(sourcePlayerId) is { } attackerPlayer)
        {
            attackerPlayer.PvpDamageDealt += damage;
        }

        var session = victim.Session;
        healthService.ApplyDamage(runtime, victim, damage, sourcePlayerId);

        if (session == null)
        {
            return;
        }

        var hit = new G_TO_C_COMBAT_HIT
        {
            AttackerId = sourcePlayerId,
            TargetId = victim.PlayerId,
            AreaType = area,
            WeaponItemId = weaponItemId,
            Damage = damage,
            AttackerHealth = sourcePlayerId == victim.PlayerId ? victim.Health : sourceHealth,
            TargetHealth = victim.Health,
            IsDot = isPeriodicDamage
        };
        runtime.PendingCombatHits.Enqueue((session, hit));
    }

    public void RecordCombatContact(MatchRuntime runtime, Player victim, long attackerId, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat damage requires the match lock.");
        }
        if (victim.Interactions.Cancel() is { } interactId)
        {
            victim.State = PlayerState.IDLE;
            victim.Session?.SendDoorOpenInterrupted(interactId);
        }

        var bot = runtime.Bots.GetBots().FirstOrDefault(bot => ReferenceEquals(bot.Player, victim));
        if (bot == null)
        {
            return;
        }

        bot.LastProximityAttackerPlayerId = attackerId;
        bot.LastDamagedAtUtc = nowUtc;
    }

    public void ApplySwarmAfterimageMonsterHit(MatchRuntime runtime, PlayerHealthService healthService, Player victim, int monsterId, int damage)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat damage requires the match lock.");
        }
        if (runtime.IsEnded || victim.IsEliminated || monsterId <= 0 || damage <= 0) return;

        var nowUtc = DateTime.UtcNow;
        if (victim.Interactions.Cancel() is { } interactId)
        {
            victim.State = PlayerState.IDLE;
            victim.Session?.SendDoorOpenInterrupted(interactId);
        }

        var bot = runtime.Bots.GetBots().FirstOrDefault(bot => ReferenceEquals(bot.Player, victim));
        if (bot != null)
        {
            bot.LastDamagedAtUtc = nowUtc;
        }

        var session = victim.Session;
        healthService.ApplyDamage(runtime, victim, damage);

        if (session == null)
        {
            return;
        }

        var hit = new G_TO_C_COMBAT_HIT
        {
            AttackerId = monsterId,
            AttackerKind = CombatEntityKind.Monster,
            TargetId = victim.PlayerId,
            AreaType = victim.GameInfo.ObjectInfo.Area,
            Damage = damage,
            TargetHealth = victim.Health
        };
        runtime.PendingCombatHits.Enqueue((session, hit));
    }

    public int RollSwarmCriticalDamage(MatchRuntime runtime, int damage, out bool critical)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat damage requires the match lock.");
        }
        critical = RollCritical(runtime, Config.SWARM_CRITICAL_CHANCE);
        return critical ? Math.Max(damage + 1, (int)MathF.Round(damage * Config.SWARM_CRITICAL_MULTIPLIER)) : damage;
    }

    private void SpawnSwarmSummonStone(MatchRuntime runtime, Monster defeated, int groundStoneReward, int heartReward)
    {
        if (groundStoneReward <= 0 && heartReward <= 0)
        {
            return;
        }

        int[] itemIds = Enumerable.Repeat(Config.SUMMON_STONE_GROUND_ITEM_ID, Math.Max(0, groundStoneReward)).Concat(Enumerable.Repeat(Config.HEART_GROUND_ITEM_ID, Math.Max(0, heartReward))).ToArray();
        runtime.GroundItems.SpawnItems(defeated.Area, defeated.Position.X, defeated.Position.Y, itemIds);
    }

    public void ApplySwarmMonsterHitNow(
        MatchRuntime runtime,
        int monsterId,
        long attackerId,
        int weaponItemId,
        AreaType area,
        int damage,
        bool critical,
        DateTime nowUtc,
        List<GameClientSession> allSessions)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat damage requires the match lock.");
        }
        var damageResult = monsters.ApplyMonsterDamage(runtime, monsterId, attackerId, damage, nowUtc);
        if (!damageResult.Applied)
        {
            return;
        }

        var attacker = runtime.GetPlayer(attackerId);
        QueueMonsterHitNotification(runtime, attacker, monsterId, area, weaponItemId, damage, critical, showDamageOnly: true);
        if (damageResult.Killed && damageResult.Monster != null)
        {
            SettleSwarmMonsterKill(runtime, damageResult, attackerId, allSessions);
        }
    }

    private void SettleSwarmMonsterKill(
        MatchRuntime runtime,
        MonsterDamageResult damageResult,
        long attackerId,
        List<GameClientSession> allSessions)
    {
        if (damageResult.Monster is not { } defeated)
        {
            return;
        }

        SpawnSwarmSummonStone(runtime, defeated, damageResult.SummonStoneReward, defeated.HeartReward);
    }

    public void ApplySwarmShock(MatchRuntime runtime, PlayerHealthService healthService,
        long ownerId,
        int weaponItemId,
        AreaType area,
        long victimId,
        IReadOnlyList<Player> players,
        float damageScale = 1f,
        bool isPeriodicDamage = false)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat damage requires the match lock.");
        }
        if (runtime.IsEnded)
        {
            return;
        }

        int shock = Math.Max(1, (int)MathF.Round(Config.ScaleSwarmDamageTaken(Config.SWARM_CROSSFIRE_SHOCK_DAMAGE) * damageScale));
        var victim = players.FirstOrDefault(player => player.PlayerId == victimId);
        if (victim == null || victim.IsEliminated)
        {
            return;
        }
        if (victim.StatusEffects.IsActive(PlayerStatusEffectKind.Wound, DateTime.UtcNow) && RollCritical(runtime, Config.SWARM_WIND_WOUND_CRIT_CHANCE))
        {
            shock = Math.Max(shock + 1, (int)MathF.Round(shock * Config.SWARM_CRITICAL_MULTIPLIER));
        }

        var owner = runtime.GetPlayer(ownerId);
        int ownerHealth = owner?.Health ?? -1;
        ApplyProximityAutoCombatHit(runtime, healthService, victim, ownerId, area, weaponItemId, shock, isPeriodicDamage, ownerHealth);
        int healthAfter = victim.Health;
        QueuePlayerHitNotification(runtime, owner, victimId, area, weaponItemId, shock, healthAfter, isPeriodicDamage);
    }

    public void ProcessPendingMonsterHits(MatchRuntime runtime, DateTime nowUtc, List<GameClientSession> sessions)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat damage requires the match lock.");
        }
        if (runtime.IsEnded)
        {
            return;
        }
        for (int index = runtime.CombatDamage.PendingMonsterHits.Count - 1; index >= 0; index--)
        {
            var hit = runtime.CombatDamage.PendingMonsterHits[index];
            if (nowUtc < hit.ApplyAtUtc)
            {
                continue;
            }

            runtime.CombatDamage.PendingMonsterHits.RemoveAt(index);
            var damageResult = monsters.ApplyMonsterDamage(runtime, hit.MonsterId, hit.AttackerId, hit.Damage, nowUtc);
            if (damageResult.Applied && damageResult.Killed && damageResult.Monster != null)
            {
                SettleSwarmMonsterKill(runtime, damageResult, hit.AttackerId, sessions);
            }
        }
    }

    public void QueueSessionEffect<T>(MatchRuntime runtime, GameClientSession session, Protocol protocol, T body) where T : IMessagePackObject
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat damage requires the match lock.");
        }
        runtime.PendingCombatEffects.Enqueue((session, protocol, MessagePackSerializer.Serialize(body)));
    }
}
