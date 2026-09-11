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

        UpdateSwarmOrbTrails(runtime, participants);
        ProcessSwarmTrailCuts(runtime, nowUtc, participants, sessions);
        ProcessSwarmRetaliationWindows(runtime, nowUtc, participants);

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

                    if (!IsWithinSwarmOrbRange(attacker, target))
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

    private const float SwarmTrailSampleMinDistance = 0.08f;
    private const float SwarmTrailTeleportResetDistance = 5f;
    private static double SwarmTrailCutSameOrbDebounceSeconds => SwarmConfigData.GetDouble("SWARM_TRAIL_CUT_SAME_ORB_DEBOUNCE_SECONDS", 0.8d);
    private const float SwarmTrailCutMaxSegmentLength = 2f;
    private static int SwarmSingleCutHealthCost => SwarmConfigData.GetInt("SWARM_SINGLE_CUT_HEALTH_COST", 35);
    private static double SwarmSingleCutHealLockSeconds => SwarmConfigData.GetDouble("SWARM_SINGLE_CUT_HEAL_LOCK_SECONDS", 8d);
    private static double SwarmCutRetaliationWindowSeconds => SwarmConfigData.GetDouble("SWARM_CUT_RETALIATION_WINDOW_SECONDS", 1.2d);
    private const float SwarmTrailCutOrbHitYOffset = 0.15f;
    private const float SwarmTrailCutFlashRadius = PlayerOrbTrailService.CutFlashRadius;

    private void ProcessSwarmRetaliationWindows(MatchRuntime runtime, DateTime nowUtc, List<SwarmParticipantSpatial> participants)
    {
        var expired = runtime.TrailCombat.CutRetaliationWindows.Where(pair => nowUtc >= pair.Value.ExpiresAtUtc).ToList();
        foreach (var (key, window) in expired)
        {
            runtime.TrailCombat.CutRetaliationWindows.Remove(key);
            var cutter = participants.Where(participant => participant.PlayerId == key.CutterId).Select(static participant => (SwarmParticipantSpatial?)participant).FirstOrDefault();
            var victim = participants.Where(participant => participant.PlayerId == key.VictimId).Select(static participant => (SwarmParticipantSpatial?)participant).FirstOrDefault();
            bool bothDisengaged = !cutter.HasValue || !victim.HasValue || cutter.Value.Area != victim.Value.Area;
            eventLogs.LogSwarmRetaliationWindow(runtime.MatchingId, key.CutterId, key.VictimId, window.BlockedDamage, window.BlockedHits, window.BlockedCuts, window.Retaliated, bothDisengaged, window.OpenedArea.ToString());
        }
    }

    private void UpdateSwarmOrbTrails(MatchRuntime runtime, List<SwarmParticipantSpatial> participants)
    {
        foreach (var participant in participants)
        {
            long key = participant.PlayerId;
            if (!runtime.TrailCombat.OrbTrails.TryGetValue(key, out var points))
            {
                points = new List<Vector3f>();
                runtime.TrailCombat.OrbTrails[key] = points;
            }

            var center = participant.Position;
            if (points.Count == 0)
            {
                points.Add(new Vector3f(center.X, center.Y, 0f));
                continue;
            }

            float moved = Vector3f.Distance(center, points[0]);
            if (moved >= SwarmTrailTeleportResetDistance)
            {
                points.Clear();
                points.Add(new Vector3f(center.X, center.Y, 0f));
                continue;
            }

            if (moved >= SwarmTrailSampleMinDistance)
            {
                points.Insert(0, new Vector3f(center.X, center.Y, 0f));
            }

            var player = runtime.GetParticipant(participant.PlayerId)!;
            int trailOrbCount = Math.Max(orbTrails.CountOrbs(runtime, player) + 2, 4);
            float neededLength = Config.SWARM_ORB_TRAIL_FIRST_OFFSET + trailOrbCount * Config.SWARM_ORB_TRAIL_SPACING + 1f;
            float accumulated = 0f;
            for (int pointIndex = 1; pointIndex < points.Count; pointIndex++)
            {
                accumulated += Vector3f.Distance(points[pointIndex - 1], points[pointIndex]);
                if (accumulated <= neededLength)
                {
                    continue;
                }
                points.RemoveRange(pointIndex + 1, points.Count - pointIndex - 1);
                break;
            }
        }
    }

    private void ProcessSwarmTrailCuts(MatchRuntime runtime, DateTime nowUtc, List<SwarmParticipantSpatial> participants, List<GameClientSession> allSessions)
    {
        var chains = new Dictionary<long, (AreaType Area, Vector3f OwnerPosition, List<Vector3f> Points, List<long> Uids, List<int> ItemIds)>();
        foreach (var owner in participants)
        {
            var orbs = runtime.GetOrbs(owner.PlayerId).GetAllItems()
                .Where(item => item.Count > 0 && GetSquadOrbTier(item.ItemId) > 0)
                .OrderBy(item => item.ItemUid)
                .ToList();
            if (orbs.Count == 0)
            {
                continue;
            }
            var points = new List<Vector3f>(orbs.Count);
            var uids = new List<long>(orbs.Count);
            var itemIds = new List<int>(orbs.Count);
            var chainTiers = orbs.Select(item => GetSquadOrbTier(item.ItemId)).ToList();
            for (int ordinal = 0; ordinal < orbs.Count; ordinal++)
            {
                var player = runtime.GetParticipant(owner.PlayerId)!;
                points.Add(orbTrails.GetOrbPosition(runtime, player, ordinal, owner.Position, chainTiers));
                uids.Add(orbs[ordinal].ItemUid);
                itemIds.Add(orbs[ordinal].ItemId);
            }
            chains[owner.PlayerId] = (owner.Area, owner.Position, points, uids, itemIds);
        }

        foreach (var cutter in participants)
        {
            var positionKey = cutter.PlayerId;
            bool hasPrevious = runtime.TrailCombat.TrailLastTickPositions.TryGetValue(positionKey, out var previous);
            runtime.TrailCombat.TrailLastTickPositions[positionKey] = new Vector3f(cutter.Position.X, cutter.Position.Y, 0f);
            if (!hasPrevious)
            {
                continue;
            }

            if (!chains.ContainsKey(cutter.PlayerId))
            {
                continue;
            }
            TryPerformSwarmTrailCut(runtime, cutter.PlayerId, cutter.PlayerId, cutter.Area, previous!, cutter.Position, chains, nowUtc, allSessions);
        }
    }

    private static int CountSwarmAttackOrbs(IReadOnlyList<int> orderedItemIds)
    {
        int count = 0;
        foreach (int itemId in orderedItemIds)
        {
            if (OrbData.TryGetColorAndTier(itemId, out _, out _))
            {
                count++;
            }
        }
        return count;
    }

    private static int CountSwarmOrbGunsCovering(Dictionary<long, (AreaType Area, Vector3f OwnerPosition, List<Vector3f> Points, List<long> Uids, List<int> ItemIds)> chains, long excludePlayerId, AreaType area, Vector3f position, long? ownerFilter = null)
    {
        int count = 0;
        foreach (var (ownerId, chain) in chains)
        {
            if (ownerId == excludePlayerId || chain.Area != area)
            {
                continue;
            }

            if (ownerFilter.HasValue && ownerId != ownerFilter.Value)
            {
                continue;
            }

            for (int ordinal = 0; ordinal < chain.Points.Count; ordinal++)
            {
                float range = OrbData.TryGetColorAndTier(chain.ItemIds[ordinal], out var orbColor, out _) ? orbColor switch
                {
                    OrbColor.Red => SwarmSunAttackRange,
                    OrbColor.Green => SwarmWindAttackRange,
                    _ => 0f
                }
                    : 0f;
                if (range > 0f && IsWithinSwarmOrbRange(chain.Points[ordinal], range, position))
                {
                    count++;
                }
            }
        }

        return count;
    }

    private void TryPerformSwarmTrailCut(MatchRuntime runtime, long cutterId, long creditPlayerId, AreaType cutterArea,
        Vector3f previous,
        Vector3f current,
        Dictionary<long, (AreaType Area, Vector3f OwnerPosition, List<Vector3f> Points,
            List<long> Uids, List<int> ItemIds)> chains,
        DateTime nowUtc,
        List<GameClientSession> allSessions)
    {
        var cutter = runtime.GetParticipant(cutterId);
        if (runtime.IsEnded || cutter == null || cutter.IsEliminated) return;

        float segmentDx = current.X - previous.X;
        float segmentDy = current.Y - previous.Y;
        float segmentLengthSquared = segmentDx * segmentDx + segmentDy * segmentDy;
        if (segmentLengthSquared < 0.0004f ||
            segmentLengthSquared > SwarmTrailCutMaxSegmentLength * SwarmTrailCutMaxSegmentLength)
            return;

        long bestOwnerId = 0;
        int bestTailOrdinal = -1;
        long bestOrbUid = 0;
        float bestT = float.MaxValue;
        Vector3f? bestOrbPosition = null;
        var bestArea = AreaType.None;
        foreach (var (ownerId, chain) in chains)
        {
            if (ownerId == cutterId || chain.Area != cutterArea || runtime.GetParticipant(ownerId) is not { IsEliminated: false })
            {
                continue;
            }
            bool guarded = runtime.TrailCombat.CutRetaliationWindows.TryGetValue((cutterId, ownerId), out var activeGuard) && nowUtc < activeGuard.ExpiresAtUtc;
            for (int ordinal = 0; ordinal < chain.Points.Count; ordinal++)
            {
                var hitPoint = new Vector3f(chain.Points[ordinal].X, chain.Points[ordinal].Y + SwarmTrailCutOrbHitYOffset, 0f);
                if (runtime.TrailCombat.OrbCutLatches.TryGetValue(
                        (cutterId, chain.Uids[ordinal]), out var lastHitAtUtc) &&
                    ((nowUtc - lastHitAtUtc).TotalSeconds < SwarmTrailCutSameOrbDebounceSeconds ||
                     SwarmCombatGeometry.IsInsideOrbHitEllipse(previous, hitPoint)))
                {
                    continue;
                }

                bool hit = SwarmCombatGeometry.TrySegmentHitsPoint(previous, current, hitPoint, out float t);
                if (!hit)
                {
                    var linkStart = ordinal == 0 ? chain.OwnerPosition : chain.Points[ordinal - 1];
                    hit = SwarmCombatGeometry.TrySegmentIntersection(previous, current, linkStart, chain.Points[ordinal], out t);
                }

                if (!hit)
                {
                    continue;
                }
                if (guarded)
                {
                    if (runtime.TrailCombat.CutRetaliationWindows.TryGetValue((cutterId, ownerId), out var guardWindow)) guardWindow.BlockedCuts++;
                    {
                        break;
                    }
                }

                if (t >= bestT)
                {
                    continue;
                }
                bestT = t;
                bestOwnerId = ownerId;
                bestTailOrdinal = ordinal;
                bestOrbUid = chain.Uids[ordinal];
                bestOrbPosition = chain.Points[ordinal];
                bestArea = chain.Area;
            }
        }

        if (bestOwnerId == 0 || bestOrbPosition == null)
        {
            return;
        }

        var cutterBot = runtime.Bots.GetBot(cutterId);
        int cutterHealthBefore = cutter.Health;
        if (cutterHealthBefore - SwarmSingleCutHealthCost <= 0)
        {
            eventLogs.LogSystem(runtime.MatchingId, $"ORB_SINGLE_CUT_REFUSED attacker={creditPlayerId} victim={bestOwnerId} targetOrbUid={bestOrbUid} " + $"targetIndex={bestTailOrdinal} reason=cost attackerHealth={cutterHealthBefore}");
            return;
        }

        if (cutterBot != null && !botDecisions.IsSwarmBotCutAllowed(runtime, cutterId, cutterHealthBefore, nowUtc, SwarmSingleCutHealthCost))
        {
            runtime.TrailCombat.OrbCutLatches[(cutterId, bestOrbUid)] = nowUtc;
            return;
        }

        runtime.TrailCombat.OrbCutLatches[(cutterId, bestOrbUid)] = nowUtc;
        cutter.MarkSwarmCombat(nowUtc);

        var owner = runtime.GetParticipant(bestOwnerId)!;
        var destroyedItems = orbTrails.DestroyOrbsFromOrdinal(runtime, owner, bestTailOrdinal);
        if (destroyedItems.Count == 0)
        {
            return;
        }
        var destroyedItem = destroyedItems[0];
        if (runtime.TrailCombat.CutRetaliationWindows.TryGetValue((bestOwnerId, creditPlayerId), out var oppositeWindow) && nowUtc < oppositeWindow.ExpiresAtUtc)
        {
            oppositeWindow.Retaliated = true;
        }
        var retaliationKey = (creditPlayerId, bestOwnerId);
        if (!runtime.TrailCombat.CutRetaliationWindows.TryGetValue(retaliationKey, out var retaliationWindow))
        {
            retaliationWindow = new SwarmRetaliationWindow { OpenedAtUtc = nowUtc, OpenedArea = bestArea };
            runtime.TrailCombat.CutRetaliationWindows[retaliationKey] = retaliationWindow;
        }
        retaliationWindow.ExpiresAtUtc = nowUtc.AddSeconds(SwarmCutRetaliationWindowSeconds);
        combatDamage.SendSwarmRetaliationVfx(runtime, creditPlayerId, bestOwnerId, bestArea, SwarmRingVfxKindRetaliationGuard, (float)SwarmCutRetaliationWindowSeconds, allSessions);
        var ownerChain = chains[bestOwnerId];
        foreach (var lost in destroyedItems)
        {
            runtime.TrailCombat.OrbDurabilityBonus.Remove((bestOwnerId, lost.ItemUid));
            owner.Session?.SendOrbUpdate(lost);
        }

        using (var ringPacket = Packet.Create((int)Protocol.G_TO_C_ORB_RING_EFFECT))
        {
            ringPacket.SetBody(MessagePackSerializer.Serialize(new G_TO_C_ORB_RING_EFFECT
            {
                OwnerPlayerId = creditPlayerId,
                CenterX = bestOrbPosition.X,
                CenterY = bestOrbPosition.Y,
                Radius = SwarmTrailCutFlashRadius,
                Kind = SwarmRingVfxKindCut,
                VictimPlayerId = bestOwnerId,
                FromOrdinal = bestTailOrdinal
            }));
            foreach (var session in allSessions)
            {
                if (session.PlayerId.HasValue && session.Player.CurrentArea == bestArea)
                {
                    session.TrySend(ringPacket);
                }
            }
        }

        var healLockUntil = nowUtc.AddSeconds(SwarmSingleCutHealLockSeconds);
        combatDamage.ApplyProximityAutoCombatHit(runtime, healthService, cutter, cutterId, cutterArea, destroyedItem.ItemId, SwarmSingleCutHealthCost);
        cutter.BlockHealingUntil(healLockUntil);
        int cutterHealthAfter = cutter.Health;
        if (cutterBot != null)
        {
            runtime.BotTactics.LastTrailCutAtUtc[cutterBot.PlayerId] = nowUtc;
        }

        var ownerBot = runtime.Bots.GetBot(bestOwnerId);
        if (ownerBot != null)
        {
            ownerBot.LastProximityAttackerPlayerId = creditPlayerId;
            ownerBot.LastDamagedAtUtc = nowUtc;
            runtime.BotTactics.LastDamagedAtUtc[ownerBot.PlayerId] = nowUtc;
            ownerBot.Player.MarkSwarmCombat(nowUtc);
        }

        int attackOrbsBefore = CountSwarmAttackOrbs(ownerChain.ItemIds);
        int orbsBefore = ownerChain.ItemIds.Count;
        int orbsAfter = orbTrails.CountOrbs(runtime, owner);
        int attackOrbsAfter = CountSwarmAttackOrbs(runtime.GetOrbs(bestOwnerId).GetOrderedOrbs().Select(item => item.ItemId).ToList());
        var rankingAfter = runtime.GetAlivePlayers()
            .Select(player => (Id: player.PlayerId, Score: runtime.GetOrbs(player.PlayerId).GetOrbScore()))
            .OrderByDescending(entry => entry.Score.OrbCount)
            .ThenByDescending(entry => entry.Score.TierSum)
            .ThenBy(entry => entry.Id)
            .ToList();
        int rankAfter = rankingAfter.FindIndex(entry => entry.Id == bestOwnerId) + 1;

        eventLogs.LogSwarmTrailCut(
            runtime.MatchingId, creditPlayerId, bestOwnerId, bestTailOrdinal, destroyedItems.Count,
            orbsBefore, orbsAfter, attackOrbsBefore, attackOrbsAfter, rankAfter,
            bestArea.ToString());
        eventLogs.LogSystem(
            runtime.MatchingId,
            $"ORB_TAIL_CUT attacker={creditPlayerId} victim={bestOwnerId} cutIndex={bestTailOrdinal} " +
            $"lostOrbs={destroyedItems.Count} firstOrbUid={destroyedItem.ItemUid} " +
            $"attackerHealthBefore={cutterHealthBefore} attackerHealthAfter={cutterHealthAfter} " +
            $"healLockUntil={healLockUntil:O} victimOrbsBefore={orbsBefore} victimOrbsAfter={orbsAfter} area={bestArea}");
        logger.LogInformation(
            "Swarm tail cut: MatchingId={MatchingId}, CutterId={CutterId}, OwnerId={OwnerId}, TailOrdinal={TailOrdinal}, Lost={Lost}, AttackerHealth={Before}->{After}",
            runtime.MatchingId, cutterId, bestOwnerId, bestTailOrdinal, destroyedItems.Count,
            cutterHealthBefore, cutterHealthAfter);
    }

    private const int SwarmRingVfxKindCut = PlayerOrbTrailService.CutVfxKind;
    private const int SwarmRingVfxKindRetaliationGuard = 5;
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

    private static int GetSquadOrbTier(int itemId)
    {
        if (OrbData.TryGetColorAndTier(itemId, out _, out int tier))
        {
            return tier;
        }
        return OrbData.TryGetRecoveryTier(itemId, out int recoveryTier) ? recoveryTier : 0;
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
                float actorAttackRange = crossfireSun ? Config.SWARM_CROSSFIRE_SUN_RANGE_BY_TIER[Math.Clamp(actorTier, 1, 3) - 1] : SwarmPveSameAreaAttackRange;
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

    private const float SwarmSunAttackRange = 6f;
    private const float SwarmWindAttackRange = SwarmSunAttackRange;
    private const float SwarmPveSameAreaAttackRange = 7f;

    private static bool IsWithinSwarmOrbRange(ProximityCombatActor attacker, ProximityCombatActor target)
    {
        float range = attacker.AttackRange > 0f ? attacker.AttackRange : Config.SWARM_ORB_ATTACK_RANGE;
        return IsWithinSwarmOrbRange(attacker.Position, range, target.Position);
    }

    private static bool IsWithinSwarmOrbRange(Vector3f origin, float range, Vector3f target)
    {
        float dx = target.X - origin.X;
        float dy = (target.Y - origin.Y) * 2f;
        return dx * dx + dy * dy <= range * range;
    }
}
