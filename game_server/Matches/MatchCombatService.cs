using game_server.matches.combat;
using game_server.matches.logging;
using game_server.matches.monsters;
using game_server.players;
using game_server.players.bots;
using game_server.sessions;
using MessagePack;
using Microsoft.Extensions.Logging;
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
    OrbVisualStatePublisher orbVisuals,
    PlayerOrbTrailService orbTrails,
    MatchTrailCutService trailCuts,
    SunOrbAttackService sunOrbAttacks,
    WaveOrbAttackService waveOrbAttacks,
    BotDecisionService botDecisions,
    ILogger<MatchCombatService> logger)
{
    private const int SwarmArenaBasicDamage = 12;
    private static float SwarmArenaBasicRange => Config.SWARM_ORB_ATTACK_RANGE;
    private const float SwarmArenaBasicAttackIntervalSeconds = 1f;
    private const int SwarmArenaWeaponItemId = 107000010;

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
            foreach (var (startRoom, pairZone) in SwarmPairZones)
            {
                var path = BotPathfinder.FindPath(Config.SWARM_MATCH_MAP, startRoom, GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, startRoom), pairZone, GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, pairZone));
            }
        }

        var nowUtc = DateTime.UtcNow;
        int aliveHumanCount = players.Count(player => player.PlayerId > 0);
        var aliveBots = bots.Where(bot => !bot.Player.IsEliminated).ToList();
        var participants = new List<SwarmParticipantSpatial>();
        foreach (var player in players)
        {
            if (player.Position == null)
            {
                continue;
            }
            participants.Add(new SwarmParticipantSpatial(player.PlayerId, player.CurrentArea, player.Position));
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
        if (runtime.IsEnded)
        {
            return;
        }

        foreach (var player in runtime.GetAlivePlayers())
        {
            playerOrbs.ActivateWaveOrbs(runtime, player, nowUtc);
        }

        if (runtime.IsEnded)
        {
            return;
        }

        foreach (var player in runtime.GetAlivePlayers())
        {
            playerOrbs.ActivateWindOrbs(runtime, player, nowUtc);
        }

        if (runtime.IsEnded)
        {
            return;
        }

        sunOrbAttacks.ProcessSwarmSunBurns(runtime, nowUtc);
        if (runtime.IsEnded)
        {
            return;
        }

        if (tick.PlayerDamage.Count > 0 && (!runtime.NextContactLogAtUtc.HasValue || nowUtc >= runtime.NextContactLogAtUtc.Value))
        {
            runtime.NextContactLogAtUtc = nowUtc.AddSeconds(10);
            int toBots = tick.PlayerDamage.Count(entry => entry.TargetPlayerId < 0);
            int toHumans = tick.PlayerDamage.Count - toBots;
        }

        foreach (var damage in tick.PlayerDamage)
        {
            ApplySwarmParticipantDamage(runtime, damage, sessions);
            if (runtime.IsEnded)
            {
                return;
            }
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

        var actors = BuildSwarmArenaCombatActors(runtime, players, nowUtc);
        playerOrbs.ProcessOrbRecovery(runtime, actors, nowUtc);
        orbVisuals.Publish(runtime, actors, sessions);
        matchResults.BroadcastOrbRankings(runtime, sessions);
        botDecisions.ProcessBotOrbGrowth(runtime, aliveBots);
        if (matchResults.TryEndOnScoreTimeout(runtime, nowUtc))
        {
            return;
        }
        combatDamage.ProcessPendingMonsterHits(runtime, nowUtc, sessions);
        sunOrbAttacks.ProcessSwarmCrossfires(runtime, nowUtc);
        if (runtime.IsEnded)
        {
            return;
        }

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
        var attacks = runtime.AutoAttack.ResolveAttacks(
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
                continue;
            int monsterId = runtime.Monsters.GetMonsterIdForCombatTarget(attack.TargetPlayerId);
            if (monsterId <= 0 && attack.TargetPlayerId < -1_000_000_000_000L)
            {
                continue;
            }
            if (SunOrbAttackService.IsSwarmCrossfireWeapon(attack.WeaponItemId))
            {
                bool anchoredThisTick = !crossfireAnchoredTargets.Add((attack.AttackerPlayerId, attack.TargetPlayerId));
                if (anchoredThisTick || runtime.GetParticipant(attack.AttackerPlayerId) is not { } sunOwner || !playerOrbs.TryStartSunCrossfire(runtime, sunOwner, attack, nowUtc))
                {
                    runtime.AutoAttack.RefundAttack(attack.AttackerPlayerId, attack.AttackerItemUid, nowUtc);
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
                combatDamage.ScheduleMonsterHit(runtime, new PendingMonsterHit(attack.TargetPlayerId, attack.AttackerPlayerId, monsterDamage, nowUtc.AddSeconds(delaySeconds), attack.WeaponItemId, attack.AttackerItemUid, origin, anchor));
                continue;
            }

            if (attack.TargetPlayerId < -1_000_000_000_000L)
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

    private static readonly (AreaType StartRoom, AreaType PairZone)[] SwarmPairZones =
    [
        (AreaType.S2Classroom1, AreaType.S2Library1),
        (AreaType.S2Classroom2, AreaType.S2Library1),
        (AreaType.S2ExamRoom, AreaType.S2Gym1),
        (AreaType.S2BroadcastRoom, AreaType.S2Gym1),
        (AreaType.S2Storage, AreaType.S2Library2),
        (AreaType.S2AdminOffice2, AreaType.S2Library2),
        (AreaType.S2AdminOffice1, AreaType.S2Gym2),
        (AreaType.S2NurseOffice, AreaType.S2Gym2)
    ];

    internal void ApplySwarmParticipantDamage(
        MatchRuntime runtime,
        SwarmPlayerDamage damage,
        List<GameClientSession> allSessions)
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

    internal List<ProximityCombatActor> BuildSwarmArenaCombatActors(MatchRuntime runtime, IReadOnlyList<Player> players, DateTime nowUtc)
    {
        var actors = new List<ProximityCombatActor>();
        foreach (var player in players)
        {
            if (player.IsEliminated || player.Position == null)
            {
                continue;
            }

            if (!CombatActorFactory.TryCreateSpatialActor(player.PlayerId, Config.SWARM_MATCH_MAP, player.CurrentArea, player.Position, out var spatial))
            {
                continue;
            }
            var fallback = spatial with
            {
                WeaponItemId = SwarmArenaWeaponItemId,
                AttackRange = SwarmArenaBasicRange,
                Damage = SwarmArenaBasicDamage,
                AttackIntervalSeconds = SwarmArenaBasicAttackIntervalSeconds,
                WeaponItemUid = spatial.PlayerId,
                TargetPriority = 0
            };
            var inventory = runtime.GetOrbs(spatial.PlayerId);
            var inventoryItems = inventory.GetAllItems().Where(item => item.Count > 0).ToList();
            if (inventoryItems.Count == 0)
            {
                actors.Add(fallback with { WeaponItemId = 0, Damage = 0 });
                continue;
            }
            actors.Add(fallback with { WeaponItemId = 0, Damage = 0 });

            int before = actors.Count;
            CombatActorFactory.AddInventoryCombatActors(actors,
                fallback with
                {
                    AttackRange = 0f,
                    Damage = 0,
                    AttackIntervalSeconds = 0f
                },
                inventory);
            float sunAttackMultiplier = OrbData.GetSunPveAttackMultiplier(inventoryItems);
            var actorTiers = orbTrails.GetOrbTiersInOrder(runtime, player);
            int orbCount = actors.Count - before;
            long nowUnixMs = (long)(nowUtc - DateTime.UnixEpoch).TotalMilliseconds;
            for (int index = before; index < actors.Count; index++)
            {
                var actor = actors[index];
                var trailPosition = orbTrails.GetOrbPosition(runtime, player, index - before, spatial.Position, actorTiers);
                actor = actor with
                {
                    Position = trailPosition,
                    Cell = ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, trailPosition),
                    TrailOrdinal = index - before
                };
                actor = actor with { Untargetable = true };
                OrbData.TryGetColorAndTier(actor.WeaponItemId, out var orbColor, out _);
                if (orbColor is OrbColor.Blue or OrbColor.Green)
                {
                    actors[index] = actor with { Damage = 0 };
                    continue;
                }

                bool crossfireSun = SunOrbAttackService.IsSwarmCrossfireSun(actor.WeaponItemId);
                float crossfireDamageMultiplier = crossfireSun ? Config.SWARM_CROSSFIRE_SUN_DAMAGE_MULTIPLIER : 1f;
                float crossfireCadenceMultiplier = crossfireSun ? Config.SWARM_CROSSFIRE_SUN_CADENCE_MULTIPLIER : 1f;
                OrbData.TryGetColorAndTier(actor.WeaponItemId, out _, out int actorTier);
                float actorAttackRange = crossfireSun ? Config.SWARM_CROSSFIRE_SUN_RANGE_BY_TIER[Math.Clamp(actorTier, 1, 3) - 1] : SwarmCombatGeometry.SwarmPveSameAreaAttackRange;
                actors[index] = actor with
                {
                    Damage = Math.Max(1, (int)MathF.Round(
                        OrbData.GetSwarmPveAttackDamage(actor.WeaponItemId) *
                        sunAttackMultiplier * crossfireDamageMultiplier)),
                    AttackIntervalSeconds = OrbData.GetSwarmPveAttackIntervalSeconds(
                        actor.WeaponItemId) * ResolveSwarmOrbCadenceJitter(index - before) *
                        crossfireCadenceMultiplier,
                    InitialAttackDelaySeconds = 0f,
                    AttackRange = actorAttackRange
                };
            }

            if (orbCount > 1)
            {
                int rotation = (int)(nowUnixMs / 50 % orbCount);
                if (rotation > 0)
                {
                    var rotated = new ProximityCombatActor[orbCount];
                    for (int offset = 0; offset < orbCount; offset++)
                    {
                        rotated[offset] = actors[before + (offset + rotation) % orbCount];
                    }

                    for (int offset = 0; offset < orbCount; offset++)
                    {
                        actors[before + offset] = rotated[offset];
                    }
                }
            }
        }

        foreach (var target in runtime.Monsters.GetCombatTargets())
        {
            actors.Add(new ProximityCombatActor(
                target.CombatTargetId,
                target.Area,
                target.Position,
                0,
                0f,
                0,
                0f,
                MapId: Config.SWARM_MATCH_MAP,
                Cell: ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, target.Position),
                IsMonsterTarget: true,
                TargetPriority: 2));
        }

        return actors;
    }

    private static float ResolveSwarmOrbCadenceJitter(int slotIndex)
    {
        float phase = slotIndex * 0.6180339f;
        phase -= MathF.Floor(phase);
        return 0.88f + phase * 0.24f;
    }

}
