using game_server.matches.logging;
using game_server.matches.monsters;
using game_server.players;
using game_server.players.bots;
using game_server.sessions;
using MessagePack;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.matches;

/// <summary>
///     플레이어·몬스터 피해와 지연 타격을 처리하고, 피격 로그와 알림을 남긴다.
///     플레이어의 체력 변경·탈락 처리는 PlayerHealthService에 위임한다.
/// </summary>
internal sealed class MatchCombatDamageService(GameEventLogManager eventLogs, MatchMonsterService monsters)
{
    private const int SwarmRingVfxKindRetaliationBlocked = 6;
    private static double SwarmCriticalChance => SwarmConfigData.GetDouble("SWARM_CRITICAL_CHANCE", 0.15d);
    private static float SwarmCriticalMultiplier => SwarmConfigData.GetFloat("SWARM_CRITICAL_MULTIPLIER", 2f);

    private static bool RollCritical(MatchRuntime runtime, double chance) => runtime.CombatDamage.CriticalRng.NextDouble() < chance;

    public void ScheduleMonsterHit(MatchRuntime runtime, PendingMonsterHit hit)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat damage requires the match lock.");
        }
        runtime.CombatDamage.PendingMonsterHits.Add(hit);
    }

    public void SchedulePvpHit(MatchRuntime runtime, ProximityCombatAttack attack, DateTime dueAtUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat damage requires the match lock.");
        }
        runtime.CombatDamage.PendingPvpHits.Add((attack, dueAtUtc));
    }

    public void ProcessPendingPvpHits(MatchRuntime runtime, PlayerHealthService healthService, DateTime nowUtc, IReadOnlyList<Player> players, List<GameClientSession> sessions)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat damage requires the match lock.");
        }
        for (int index = runtime.CombatDamage.PendingPvpHits.Count - 1; index >= 0; index--)
        {
            var pending = runtime.CombatDamage.PendingPvpHits[index];
            if (nowUtc < pending.DueAtUtc)
            {
                continue;
            }

            runtime.CombatDamage.PendingPvpHits.RemoveAt(index);
            ApplySwarmPvpAttack(runtime, healthService, pending.Attack, players, sessions, broadcastVfx: false);
            if (runtime.IsEnded)
            {
                return;
            }
        }
    }

    internal static int ConsumeSwarmPvpDamage(Player victim, int rawDamage)
    {
        float total = victim.PvpDamageCarry + rawDamage * Config.SWARM_PVP_DAMAGE_PER_DAMAGE;
        int whole = (int)total;
        victim.PvpDamageCarry = total - whole;
        return whole;
    }

    public void SendPlayerHitNotification(MatchRuntime runtime, Player? attacker, long targetPlayerId, AreaType area, int weaponItemId, int damage, int targetHealth, bool isPeriodicDamage = false)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat damage requires the match lock.");
        }
        if (attacker == null || attacker.PlayerId == 0 || targetPlayerId == 0)
        {
            return;
        }

        using var packet = PacketMaker.G_TO_C_COMBAT_HIT(new G_TO_C_COMBAT_HIT
        {
            AttackerId = attacker.PlayerId,
            TargetId = targetPlayerId,
            AreaType = area,
            WeaponItemId = weaponItemId,
            Damage = damage,
            AttackerHealth = attacker.Health,
            TargetHealth = targetHealth,
            IsDot = isPeriodicDamage
        });
        attacker.Session?.TrySend(packet);
    }

    public void SendMonsterHitNotification(MatchRuntime runtime, Player? attacker, int monsterId, AreaType area,
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
        using var packet = PacketMaker.G_TO_C_COMBAT_HIT(new G_TO_C_COMBAT_HIT
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
        });
        attacker.Session?.TrySend(packet);
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
        eventLogs.LogHit(runtime.MatchingId, sourcePlayerId, victim.PlayerId, weaponItemId, damage, victim.Health > 0 && victim.Health - damage <= 0, BotPlayerManager.IsBotPlayerId(sourcePlayerId), DateTimeOffset.UtcNow);

        var session = victim.Session;
        healthService.ApplyDamage(runtime, victim, damage, sourcePlayerId);

        if (session == null)
        {
            return;
        }

        using var packet = PacketMaker.G_TO_C_COMBAT_HIT(new G_TO_C_COMBAT_HIT
        {
            AttackerId = sourcePlayerId,
            TargetId = victim.PlayerId,
            AreaType = area,
            WeaponItemId = weaponItemId,
            Damage = damage,
            AttackerHealth = sourcePlayerId == victim.PlayerId ? victim.Health : sourceHealth,
            TargetHealth = victim.Health,
            IsDot = isPeriodicDamage
        });
        session.TrySend(packet);
    }

    public void RecordCombatContact(MatchRuntime runtime, Player victim, long attackerId, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat damage requires the match lock.");
        }
        victim.MarkSwarmCombat(nowUtc);
        if (victim.InterruptDoor() is { } interactId)
        {
            eventLogs.LogExploreCancelled(runtime.MatchingId, victim.PlayerId, interactId, victim.CurrentArea.ToString(), "door_unlock_hit", isBot: victim.PlayerId < 0);
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
        victim.MarkSwarmCombat(nowUtc);
        if (victim.InterruptDoor() is { } interactId)
        {
            eventLogs.LogExploreCancelled(runtime.MatchingId, victim.PlayerId, interactId, victim.CurrentArea.ToString(), "door_unlock_hit", isBot: victim.PlayerId < 0);
            victim.Session?.SendDoorOpenInterrupted(interactId);
        }

        var bot = runtime.Bots.GetBots().FirstOrDefault(bot => ReferenceEquals(bot.Player, victim));
        if (bot != null)
        {
            bot.LastDamagedAtUtc = nowUtc;
        }

        int healthBefore = victim.Health;
        int healthAfter = Math.Max(0, healthBefore - damage);
        bool isLethal = healthBefore > 0 && healthAfter <= 0;
        bool isBot = BotPlayerManager.IsBotPlayerId(victim.PlayerId);
        eventLogs.LogSwarmAfterimageHit(runtime.MatchingId, monsterId, victim.PlayerId, victim.CurrentArea.ToString(), damage, healthBefore, healthAfter, isLethal, isBot, new DateTimeOffset(nowUtc));
        var session = victim.Session;
        healthService.ApplyDamage(runtime, victim, damage);

        if (session == null)
        {
            return;
        }

        using var packet = PacketMaker.G_TO_C_COMBAT_HIT(new G_TO_C_COMBAT_HIT
        {
            AttackerId = monsterId,
            AttackerKind = CombatEntityKind.Monster,
            TargetId = victim.PlayerId,
            AreaType = victim.CurrentArea,
            Damage = damage,
            TargetHealth = victim.Health
        });
        session.TrySend(packet);
    }

    public int RollSwarmCriticalDamage(MatchRuntime runtime, int damage, out bool critical)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat damage requires the match lock.");
        }
        critical = RollCritical(runtime, SwarmCriticalChance);
        return critical ? Math.Max(damage + 1, (int)MathF.Round(damage * SwarmCriticalMultiplier)) : damage;
    }

    private void SpawnSwarmSummonStone(MatchRuntime runtime, Monster defeated, int groundStoneReward, int heartReward, IReadOnlyCollection<GameClientSession> sessions)
    {
        if (groundStoneReward <= 0 && heartReward <= 0)
        {
            return;
        }

        int[] itemIds = Enumerable.Repeat(Config.SUMMON_STONE_GROUND_ITEM_ID, Math.Max(0, groundStoneReward)).Concat(Enumerable.Repeat(Config.HEART_GROUND_ITEM_ID, Math.Max(0, heartReward))).ToArray();
        var spawned = runtime.GroundItems.SpawnItems(defeated.Area, defeated.Position.X, defeated.Position.Y, itemIds);
        foreach (var item in spawned)
        {
            eventLogs.LogGroundItemSpawned(runtime.MatchingId, 0, item.GroundItemUid, item.ItemId, defeated.Area.ToString(), 0, isBot: false);
        }

        using var packet = PacketMaker.G_TO_C_GROUND_ITEM_SPAWN((int)defeated.Area, spawned.ToList());
        foreach (var session in sessions.Where(session => session.Player.CurrentArea == defeated.Area))
        {
            session.TrySend(packet);
        }
    }

    public void ApplySwarmMonsterHitNow(
        MatchRuntime runtime,
        long combatTargetId,
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
        var damageResult = monsters.ApplyMonsterDamage(runtime, combatTargetId, attackerId, damage, nowUtc);
        if (!damageResult.Applied)
        {
            return;
        }

        eventLogs.RecordMonsterHit(runtime.MatchingId, attackerId, damage, damageResult.Killed);
        var attacker = runtime.GetParticipant(attackerId);
        SendMonsterHitNotification(runtime, attacker, monsterId, area, weaponItemId, damage, critical, showDamageOnly: true);
        if (damageResult.Killed && damageResult.Monster != null)
        {
            SettleSwarmMonsterKill(runtime, damageResult, attackerId, damage, allSessions);
        }
    }

    private void SettleSwarmMonsterKill(
        MatchRuntime runtime,
        MonsterDamageResult damageResult,
        long attackerId,
        int damage,
        List<GameClientSession> allSessions)
    {
        if (damageResult.Monster is not { } defeated)
        {
            return;
        }

        eventLogs.LogSystem(runtime.MatchingId,
            $"monster_lifetime kind={defeated.Kind} area={defeated.Area} " +
            $"aliveSeconds={(defeated.DiedAtUtc - defeated.SpawnedAtUtc).TotalSeconds:F1} attackEvents={defeated.AttackEventCount} " +
            $"killer={attackerId}");
        eventLogs.LogSwarmAfterimageKilled(
            runtime.MatchingId, defeated.MonsterId,
            defeated.Area.ToString(),
            isCore: defeated.Kind == MonsterKind.RunawayGoblin,
            firstAttackerPlayerId: attackerId,
            lastAttackerPlayerId: attackerId,
            new Dictionary<long, int> { [attackerId] = damage });
        SpawnSwarmSummonStone(runtime, defeated, damageResult.SummonStoneReward, defeated.HeartReward, allSessions);
    }

    public void ApplySwarmShock(MatchRuntime runtime, PlayerHealthService healthService,
        long ownerId,
        int weaponItemId,
        AreaType area,
        long victimId,
        string label,
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
        if (victim.IsWounded(DateTime.UtcNow) && RollCritical(runtime, Config.SWARM_WIND_WOUND_CRIT_CHANCE))
        {
            shock = Math.Max(shock + 1, (int)MathF.Round(shock * SwarmCriticalMultiplier));
        }

        var owner = runtime.GetParticipant(ownerId);
        int ownerHealth = owner?.Health ?? -1;
        int healthBefore = victim.Health;
        ApplyProximityAutoCombatHit(runtime, healthService, victim, ownerId, area, weaponItemId, shock, isPeriodicDamage, ownerHealth);
        int healthAfter = victim.Health;
        SendPlayerHitNotification(runtime, owner, victimId, area, weaponItemId, shock, healthAfter, isPeriodicDamage);
        eventLogs.LogSystem(runtime.MatchingId, $"{label} owner={ownerId} victim={victimId} weapon={weaponItemId} " + $"healthBefore={healthBefore} healthAfter={healthAfter}");
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
        long matchingId = runtime.MatchingId;
        for (int index = runtime.CombatDamage.PendingMonsterHits.Count - 1; index >= 0; index--)
        {
            var hit = runtime.CombatDamage.PendingMonsterHits[index];
            if (nowUtc < hit.ApplyAtUtc)
            {
                continue;
            }

            runtime.CombatDamage.PendingMonsterHits.RemoveAt(index);
            var damageResult = monsters.ApplyMonsterDamage(runtime, hit.CombatTargetId, hit.AttackerId, hit.Damage, nowUtc);
            if (damageResult.Applied)
            {
                eventLogs.RecordMonsterHit(matchingId, hit.AttackerId, hit.Damage, damageResult.Killed);
            }

            if (damageResult.Applied && damageResult.Killed && damageResult.Monster != null)
            {
                SettleSwarmMonsterKill(runtime, damageResult, hit.AttackerId, hit.Damage, sessions);
            }
        }
    }

    public void ApplySwarmPvpAttack(MatchRuntime runtime, PlayerHealthService healthService,
        ProximityCombatAttack attack,
        IReadOnlyList<Player> players,
        List<GameClientSession> allSessions,
        bool broadcastVfx = true,
        bool sendAttackerFeedback = true)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat damage requires the match lock.");
        }
        if (runtime.IsEnded)
        {
            return;
        }
        var nowUtc = DateTime.UtcNow;
        if (runtime.CutRetaliationWindows.TryGetValue((attack.AttackerPlayerId, attack.TargetPlayerId), out var guardWindow) && nowUtc < guardWindow.ExpiresAtUtc)
        {
            guardWindow.BlockedHits++;
            guardWindow.BlockedDamage += attack.Damage;
            SendSwarmRetaliationVfx(runtime, attack.AttackerPlayerId, attack.TargetPlayerId, attack.Area, SwarmRingVfxKindRetaliationBlocked, 0f, allSessions);
            return;
        }

        var target = players.FirstOrDefault(player => player.PlayerId == attack.TargetPlayerId);
        if (target == null || target.IsEliminated)
        {
            return;
        }
        int healthDamage = ConsumeSwarmPvpDamage(target, attack.Damage);
        var attacker = runtime.GetParticipant(attack.AttackerPlayerId);
        int attackerHealth = attacker?.Health ?? -1;
        if (healthDamage > 0)
        {
            ApplyProximityAutoCombatHit(runtime, healthService, target, attack.AttackerPlayerId, attack.Area, attack.WeaponItemId, healthDamage, sourceHealth: attackerHealth);
        }
        else
        {
            RecordCombatContact(runtime, target, attack.AttackerPlayerId, DateTime.UtcNow);
        }

        int targetHealth = target.Health;
        if (healthDamage > 0 && sendAttackerFeedback)
        {
            SendPlayerHitNotification(runtime, attacker, attack.TargetPlayerId, attack.Area, attack.WeaponItemId, healthDamage, targetHealth);
        }

        if (broadcastVfx)
        {
            BroadcastSwarmAttackVfxToTargetAndObservers(attack, allSessions);
        }
    }

    public void SendSwarmRetaliationVfx(MatchRuntime runtime, long cutterId, long victimId, AreaType area, int kind, float seconds, List<GameClientSession> allSessions)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat damage requires the match lock.");
        }
        using var packet = Packet.Create((int)Protocol.G_TO_C_ORB_RING_EFFECT);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_ORB_RING_EFFECT
        {
            OwnerPlayerId = cutterId,
            CenterX = 0f,
            CenterY = 0f,
            Radius = seconds,
            Kind = kind,
            VictimPlayerId = victimId,
            FromOrdinal = 0
        }));

        foreach (var session in allSessions)
        {
            if (!session.PlayerId.HasValue || session.Player.CurrentArea != area)
            {
                continue;
            }

            if (session.PlayerId.Value == victimId || session.PlayerId.Value == cutterId)
            {
                session.TrySend(packet);
            }
        }
    }

    public static void BroadcastSwarmAttackVfxToTargetAndObservers(ProximityCombatAttack attack, IReadOnlyCollection<GameClientSession> sessions)
    {
        foreach (var observer in sessions)
        {
            if (!observer.PlayerId.HasValue || observer.PlayerId.Value == attack.AttackerPlayerId || observer.Player.CurrentArea != attack.Area)
            {
                continue;
            }

            using var packet = Packet.Create((int)Protocol.G_TO_C_PROXIMITY_ATTACK_VFX);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_PROXIMITY_ATTACK_VFX
            {
                AttackerPlayerId = attack.AttackerPlayerId,
                TargetPlayerId = attack.TargetPlayerId,
                AreaType = attack.Area,
                WeaponItemId = attack.WeaponItemId
            }));
            observer.TrySend(packet);
        }
    }
}
