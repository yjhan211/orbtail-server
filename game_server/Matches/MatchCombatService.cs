using game_server.matches.combat;
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

internal class MatchCombatService(
    GameEventLogManager eventLogs,
    PlayerHealthService healthService,
    MatchCombatDamageService combatDamage,
    MatchResultService matchResults,
    PlayerOrbService playerOrbs,
    PlayerOrbTrailService orbTrails,
    MatchTrailCutService trailCuts,
    MatchCombatActorBuilder actorBuilder,
    MatchAutoAttackService autoAttacks,
    SunOrbAttackService sunOrbAttacks,
    WaveOrbAttackService waveOrbAttacks,
    BotDecisionService botDecisions)
{
    public virtual void ProcessTick(MatchRuntime runtime)
    {
        if (runtime.IsEnded)
        {
            return;
        }

        if (runtime.Mode == MatchMode.SoloMapValidation)
        {
            return;
        }

        var sessions = runtime.GetSessions().Where(session => !session.IsGameEnded).ToList();
        var bots = runtime.Bots.GetBots().ToList();
        var players = runtime.GetAlivePlayers();
        if (players.Count == 0)
        {
            return;
        }

        if (!runtime.Monsters.HasMatching())
        {
            long initialPlayerId = players[0].PlayerId;
            if (!runtime.Monsters.InitializeMatching(initialPlayerId, DateTime.UtcNow))
            {
                return;
            }
        }

        var nowUtc = DateTime.UtcNow;
        var aliveBots = bots.Where(bot => !bot.Player.IsEliminated).ToList();
        var participants = new List<PlayerPositionSnapshot>();
        foreach (var player in players)
        {
            if (player.Position == null)
            {
                continue;
            }
            participants.Add(new PlayerPositionSnapshot(player.PlayerId, player.CurrentArea, player.Position));
        }

        var tick = runtime.Monsters.Tick(participants, runtime.IsGameplayActive(), nowUtc);
        foreach (string report in tick.StuckReports)
        {
            eventLogs.LogSystem(runtime.MatchingId, report);
        }

        if (!runtime.IsGameplayActive())
        {
            MonsterSnapshotPublisher.Broadcast(runtime, sessions, runtime.Monsters.GetVisualStates());
            return;
        }

        orbTrails.UpdateTrails(runtime, participants);
        trailCuts.ProcessTick(runtime, nowUtc, participants, sessions);
        waveOrbAttacks.ProcessTick(runtime, nowUtc);
        foreach (var player in runtime.GetAlivePlayers())
        {
            playerOrbs.ActivateWaveOrbs(runtime, player, nowUtc);
        }
        foreach (var player in runtime.GetAlivePlayers())
        {
            playerOrbs.ActivateWindOrbs(runtime, player, nowUtc);
        }
        sunOrbAttacks.ProcessSwarmSunBurns(runtime, nowUtc);
        foreach (var damage in tick.PlayerDamage)
        {
            ApplySwarmParticipantDamage(runtime, damage, sessions);
        }
        if (runtime.IsEnded)
        {
            return;
        }

        aliveBots.RemoveAll(bot => bot.Player.IsEliminated);
        botDecisions.UpdateSleep(runtime, aliveBots, nowUtc);
        healthService.ApplyPeriodicBuffs(runtime, players, nowUtc);
        if (runtime.IsEnded)
        {
            return;
        }
        players.RemoveAll(player => player.IsEliminated);
        healthService.ApplySleepRecovery(runtime, players, nowUtc);
        botDecisions.ProcessSwarmBotDoorUnlocks(runtime, aliveBots, sessions, nowUtc);
        if (MonsterSnapshotPublisher.TryConsumeBroadcastSlot(runtime, nowUtc))
        {
            MonsterSnapshotPublisher.Broadcast(runtime, sessions, runtime.Monsters.GetVisualStates());
        }

        var actors = actorBuilder.Build(runtime, players, nowUtc);
        playerOrbs.ProcessOrbRecovery(runtime, actors, nowUtc);
        var orbVisuals = OrbVisual.Build(runtime, actors);
        foreach (var session in sessions)
        {
            session.SendOrbVisualStates(orbVisuals);
        }
        matchResults.BroadcastOrbRankings(runtime, sessions);
        botDecisions.ProcessBotOrbGrowth(runtime, aliveBots);
        if (matchResults.TryEndOnScoreTimeout(runtime, nowUtc))
        {
            return;
        }
        combatDamage.ProcessPendingMonsterHits(runtime, nowUtc, sessions);
        sunOrbAttacks.ProcessSwarmCrossfires(runtime, nowUtc);
        combatDamage.ProcessPendingPvpHits(runtime, healthService, nowUtc, runtime.GetAlivePlayers(), sessions);
        if (runtime.IsEnded)
        {
            return;
        }

        var swarmBodyPositions = new Dictionary<long, Vector3f>();
        foreach (var actor in actors)
        {
            if (actor.WeaponItemId == 0 && !actor.IsMonsterTarget)
            {
                swarmBodyPositions[actor.PlayerId] = actor.Position;
            }
        }

        var crossfireCappedOwners = sunOrbAttacks.CollectSwarmCrossfireCappedOwners(runtime, nowUtc);
        var crossfireAnchoredTargets = sunOrbAttacks.CollectSwarmCrossfireAnchoredTargets(runtime);
        var attacks = autoAttacks.UpdateAttacks(
            runtime,
            actors,
            nowUtc,
            (attacker, target) =>
            {
                if (attacker.IsMonsterTarget || attacker.Area != target.Area)
                {
                    return false;
                }
                if (SunOrbAttackService.IsSwarmCrossfireWeapon(attacker.WeaponItemId))
                {
                    if (crossfireCappedOwners.Contains(attacker.PlayerId))
                    {
                        return false;
                    }

                    if (crossfireAnchoredTargets.Contains((attacker.PlayerId, target.PlayerId)))
                    {
                        return false;
                    }

                    if (!SwarmCombatGeometry.IsWithinSwarmOrbRange(attacker, target))
                    {
                        return false;
                    }
                }

                if (target.IsMonsterTarget)
                {
                    return true;
                }

                if (SunOrbAttackService.IsSwarmCrossfireWeapon(attacker.WeaponItemId))
                {
                    return true;
                }

                if (attacker.TrailOrdinal >= Config.SWARM_PVP_ORB_COUNT)
                {
                    return false;
                }

                if (!swarmBodyPositions.TryGetValue(attacker.PlayerId, out var attackerBody))
                {
                    attackerBody = attacker.Position;
                }
                float dx = target.Position.X - attackerBody.X;
                float dy = target.Position.Y - attackerBody.Y;
                if (dx * dx + dy * dy > Config.SWARM_PVP_ATTACK_RANGE * Config.SWARM_PVP_ATTACK_RANGE)
                {
                    return false;
                }
                return true;
            });
        Dictionary<long, ProximityCombatActor>? actorById = null;
        foreach (var attack in attacks)
        {
            if (runtime.GetParticipant(attack.AttackerPlayerId)?.IsEliminated == true)
            {
                continue;
            }
            int monsterId = runtime.Monsters.GetMonsterIdForCombatTarget(attack.TargetPlayerId);
            if (monsterId <= 0 && SwarmMonsterDirector.IsCombatTargetId(attack.TargetPlayerId))
            {
                continue;
            }
            if (SunOrbAttackService.IsSwarmCrossfireWeapon(attack.WeaponItemId))
            {
                bool anchoredThisTick = !crossfireAnchoredTargets.Add((attack.AttackerPlayerId, attack.TargetPlayerId));
                if (anchoredThisTick || runtime.GetParticipant(attack.AttackerPlayerId) is not { } sunOwner || !playerOrbs.TryStartSunCrossfire(runtime, sunOwner, attack, nowUtc))
                {
                    runtime.GetParticipant(attack.AttackerPlayerId)?.AutoAttack.ResetAttackCooldown(attack.AttackerItemUid, nowUtc);
                }
                continue;
            }

            if (monsterId > 0)
            {
                int monsterDamage = combatDamage.RollSwarmCriticalDamage(runtime, attack.Damage, out bool critical);
                var attacker = runtime.GetParticipant(attack.AttackerPlayerId);
                combatDamage.SendMonsterHitNotification(runtime, attacker, monsterId, attack.Area, attack.WeaponItemId, monsterDamage, critical);
                MatchCombatDamageService.BroadcastSwarmAttackVfxToTargetAndObservers(attack with { TargetPlayerId = -monsterId }, sessions);
                actorById ??= actors.GroupBy(actor => actor.PlayerId).ToDictionary(group => group.Key, group => group.First());
                var origin = attack.Origin ?? (actorById.TryGetValue(attack.AttackerPlayerId, out var attackerActor) ? attackerActor.Position : null);
                var anchor = attack.AnchorPosition ?? (actorById.TryGetValue(attack.TargetPlayerId, out var targetActor) ? targetActor.Position : null);
                float distance = origin != null && anchor != null ? Vector3f.Distance(origin, anchor) : Config.SWARM_ORB_ATTACK_RANGE;
                double delaySeconds = OrbData.GetPvpProjectileImpactDelaySeconds(attack.WeaponItemId, distance);
                runtime.Monsters.ReserveMonsterDamage(attack.TargetPlayerId, monsterDamage);
                runtime.Monsters.RecordMonsterAttackEvent(attack.TargetPlayerId);
                combatDamage.ScheduleMonsterHit(runtime, new PendingMonsterHit(attack.TargetPlayerId, attack.AttackerPlayerId, monsterDamage, nowUtc.AddSeconds(delaySeconds)));
                continue;
            }

            if (SwarmMonsterDirector.IsCombatTargetId(attack.TargetPlayerId))
            {
                continue;
            }

            if (OrbData.TryGetColorAndTier(attack.WeaponItemId, out var pvpColor, out _) && pvpColor is OrbColor.Red or OrbColor.Green)
            {
                MatchCombatDamageService.BroadcastSwarmAttackVfxToTargetAndObservers(attack, sessions);
                actorById ??= actors.GroupBy(actor => actor.PlayerId).ToDictionary(group => group.Key, group => group.First());
                float pvpDistance = actorById.TryGetValue(attack.AttackerPlayerId, out var pvpAttacker) && actorById.TryGetValue(attack.TargetPlayerId, out var pvpTarget)
                    ? Vector3f.Distance(pvpAttacker.Position, pvpTarget.Position)
                    : Config.SWARM_ORB_ATTACK_RANGE;
                double pvpDelaySeconds = OrbData.GetPvpProjectileImpactDelaySeconds(attack.WeaponItemId, pvpDistance); combatDamage.SchedulePvpHit(runtime, attack, nowUtc.AddSeconds(pvpDelaySeconds));
                continue;
            }
            combatDamage.ApplySwarmPvpAttack(runtime, healthService, attack, runtime.GetAlivePlayers(), sessions);
            if (runtime.IsEnded)
            {
                return;
            }
        }
    }

    internal void ApplySwarmParticipantDamage(MatchRuntime runtime, MonsterContactDamage damage, List<GameClientSession> allSessions)
    {
        var victim = runtime.GetParticipant(damage.TargetPlayerId);
        if (runtime.IsEnded || victim == null || victim.IsEliminated)
        {
            return;
        }
        damage = damage with { Damage = Config.ScaleSwarmDamageTaken(damage.Damage) };
        if (runtime.Monsters.IsWavePatternMonster(damage.MonsterId))
        {
            using var vfxPacket = Packet.Create((int)Protocol.G_TO_C_MONSTER_ATTACK_VFX);
            vfxPacket.SetBody(MessagePackSerializer.Serialize(new G_TO_C_MONSTER_ATTACK_VFX
            {
                MonsterId = damage.MonsterId,
                TargetPlayerId = damage.TargetPlayerId,
                AreaType = damage.Area
            }));
            foreach (var vfxSession in allSessions)
            {
                if (!vfxSession.Player.IsEliminated && vfxSession.Player.CurrentArea == damage.Area)
                {
                    vfxSession.TrySend(vfxPacket);
                }
            }
        }
        combatDamage.ApplySwarmAfterimageMonsterHit(runtime, healthService, victim, damage.MonsterId, damage.Damage);
    }
}
