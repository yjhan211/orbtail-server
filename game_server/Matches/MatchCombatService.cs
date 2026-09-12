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
///     매치의 전투 틱을 조율한다.
///     오브·몬스터 공격, 상태 효과, 지연 타격을 순서대로 처리하고 자동공격 목록을 결정한 뒤 피해 적용을 각 서비스에 위임한다.
/// </summary>
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
    MatchOrbAttackService orbAttacks,
    BotDecisionService botDecisions,
    MonsterCombatService monsterCombat,
    MatchMonsterSpawnService monsterSpawns,
    MonsterMovementService monsterMovement)
{
    internal List<MonsterContactDamage> ProcessMonsterTick(MatchRuntime runtime, IReadOnlyCollection<PlayerPositionSnapshot> participants, bool isGameplayActive, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Match monster service requires the match lock.");
        }
        var contacts = new List<MonsterContactDamage>();
        var state = runtime.Monsters;
        if (!state.IsInitialized)
        {
            return contacts;
        }

        var now = nowUtc;
        double deltaSeconds = Math.Clamp((now - state.LastTickAtUtc).TotalSeconds, 0d, 0.25d);
        state.LastTickAtUtc = now;
        var snapshot = new PlayerPositionSnapshot[participants.Count];
        int snapshotIndex = 0;
        foreach (var participant in participants)
        {
            snapshot[snapshotIndex++] = participant;
        }

        bool preMatch = !isGameplayActive;
        double moveDeltaSeconds = (now - state.StartsAtUtc).TotalSeconds >= Config.SWARM_MONSTER_ESCALATION_STAGE2_AT_SECONDS
            ? deltaSeconds * Config.SWARM_MONSTER_ESCALATION_STAGE2_MOVE_SPEED_MULTIPLIER
            : deltaSeconds;

        monsterSpawns.ProcessSupply(runtime, snapshot, now, preMatch);
        foreach (var monster in state.Entities.Values)
        {
            if (!monster.Alive || now < monster.ActivatesAtUtc)
            {
                continue;
            }

            monsterMovement.Move(runtime, monster, snapshot, now, moveDeltaSeconds, preMatch);
            monsterMovement.RescueMonsterFromBlockedCell(runtime, monster);
            monsterCombat.CollectContactDamage(runtime, monster, snapshot, now, contacts);
        }
        state.RemoveExpiredDead(now);
        return contacts;
    }

    internal static bool TryClaimSnapshotSlot(MatchRuntime runtime, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Match monster service requires the match lock.");
        }
        var state = runtime.Monsters;
        if (nowUtc < state.NextSnapshotAtUtc)
        {
            return false;
        }
        state.NextSnapshotAtUtc = nowUtc + TimeSpan.FromMilliseconds(100);
        return true;
    }

    public virtual void ProcessTick(MatchRuntime runtime)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat tick requires the match lock.");
        }
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

        if (!runtime.Monsters.IsInitialized)
        {
            runtime.Monsters.Initialize(DateTime.UtcNow);
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

        var contacts = ProcessMonsterTick(runtime, participants, runtime.IsGameplayActive(), nowUtc);

        if (!runtime.IsGameplayActive())
        {
            SendMonsterSnapshots(runtime, sessions, preMatch: true);
            return;
        }

        orbTrails.UpdateTrails(runtime, participants);
        trailCuts.ProcessTick(runtime, nowUtc, participants, sessions);
        orbAttacks.ProcessWaveDetonations(runtime, nowUtc);
        foreach (var player in runtime.GetAlivePlayers())
        {
            playerOrbs.ActivateWaveOrbs(runtime, player, nowUtc);
        }
        foreach (var player in runtime.GetAlivePlayers())
        {
            playerOrbs.ActivateWindOrbs(runtime, player, nowUtc);
        }
        orbAttacks.ProcessSunBurns(runtime, nowUtc);
        foreach (var damage in contacts)
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
        if (TryClaimSnapshotSlot(runtime, nowUtc))
        {
            SendMonsterSnapshots(runtime, sessions, preMatch: false);
        }

        var actors = actorBuilder.Build(runtime, players, nowUtc);
        playerOrbs.ProcessOrbRecovery(runtime, actors, nowUtc);
        var orbVisuals = MatchOrbVisual.Build(runtime, actors);
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
        orbAttacks.ProcessSunCrossfires(runtime, nowUtc);
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

        var crossfireCappedOwners = orbAttacks.CollectSunCrossfireCappedOwners(runtime, nowUtc);
        var crossfireAnchoredTargets = orbAttacks.CollectSunCrossfireAnchoredTargets(runtime);
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
                if (MatchOrbAttackService.IsSunCrossfireWeapon(attacker.WeaponItemId))
                {
                    if (crossfireCappedOwners.Contains(attacker.PlayerId))
                    {
                        return false;
                    }

                    if (crossfireAnchoredTargets.Contains((attacker.PlayerId, target.PlayerId)))
                    {
                        return false;
                    }

                    float range = attacker.AttackRange > 0f ? attacker.AttackRange : Config.SWARM_ORB_ATTACK_RANGE;
                    if (!GroundGeometry.IsWithinGroundRadius(attacker.Position, target.Position, range))
                    {
                        return false;
                    }
                }

                if (target.IsMonsterTarget)
                {
                    return true;
                }

                if (MatchOrbAttackService.IsSunCrossfireWeapon(attacker.WeaponItemId))
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
            var targetMonster = runtime.Monsters.FindAliveByCombatTarget(attack.TargetPlayerId);
            int monsterId = targetMonster?.MonsterId ?? 0;
            if (monsterId <= 0 && Monster.IsCombatTargetId(attack.TargetPlayerId))
            {
                continue;
            }
            if (MatchOrbAttackService.IsSunCrossfireWeapon(attack.WeaponItemId))
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
                targetMonster!.ReserveDamage(monsterDamage);
                targetMonster.RecordAttackEvent();
                combatDamage.ScheduleMonsterHit(runtime, new PendingMonsterHit(attack.TargetPlayerId, attack.AttackerPlayerId, monsterDamage, nowUtc.AddSeconds(delaySeconds)));
                continue;
            }

            if (Monster.IsCombatTargetId(attack.TargetPlayerId))
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

    private void SendMonsterSnapshots(MatchRuntime runtime, List<GameClientSession> sessions, bool preMatch)
    {
        var snapshots = runtime.Monsters.GetVisualStatesByArea();
        foreach (var session in sessions)
        {
            session.SendMonsterSnapshot(snapshots, preMatch);
        }
    }
    internal void ApplySwarmParticipantDamage(MatchRuntime runtime, MonsterContactDamage damage, List<GameClientSession> allSessions)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat tick requires the match lock.");
        }
        var victim = runtime.GetParticipant(damage.TargetPlayerId);
        if (runtime.IsEnded || victim == null || victim.IsEliminated)
        {
            return;
        }
        damage = damage with { Damage = Config.ScaleSwarmDamageTaken(damage.Damage) };
        if (runtime.Monsters.Entities.TryGetValue(damage.MonsterId, out var monster) && monster.Insignia == MonsterInsignia.Wave)
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
