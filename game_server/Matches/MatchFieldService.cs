using game_server.matches.combat;
using game_server.matches.logging;
using game_server.players;
using MessagePack;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.matches;

/// <summary>
///     매치의 구역 폐쇄와 자기장 피해를 처리한다.
///     폐쇄된 구역의 문을 닫고 잔류 오브를 제거하며, 자기장 피해로 동시에 탈락하는 참가자의 순위를 결정한다.
///     상태는 MatchRuntime이 소유하고, 호출자는 매치 잠금을 보유해야 한다.
/// </summary>
internal class MatchFieldService(
    GameEventLogManager eventLogs,
    PlayerOrbTrailService orbTrails,
    PlayerHealthService healthService,
    MatchCleanupService matchCleanup,
    PlayerEliminationService matchEliminations,
    MatchResultService matchResults)
{
    internal static readonly Lazy<IReadOnlyList<(AreaType Area, int ClosureAtSeconds)>> SwarmFieldClosureSchedule =
        new(() => SwarmPressureField.GetKnownAreas()
                .Select(area => (Area: area, ClosureAtSeconds: SwarmPressureField.GetAreaClosureSeconds(area)))
                .OrderBy(entry => entry.ClosureAtSeconds)
                .ToList()
                .AsReadOnly(),
            LazyThreadSafetyMode.ExecutionAndPublication);

    private static int BaseDamagePerTick => SwarmConfigData.GetInt("SWARM_FIELD_BASE_DAMAGE_PER_TICK", 12);
    private static int DamagePerExtraCell => SwarmConfigData.GetInt("SWARM_FIELD_DAMAGE_PER_EXTRA_CELL", 5);

    public virtual void ProcessClosureTick(MatchRuntime runtime)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Closure tick requires the match lock.");
        }

        var sessions = runtime.GetSessions();
        var closures = runtime.Closures;
        if (closures.InitializeMatching(SwarmFieldClosureSchedule.Value))
        {
            eventLogs.LogSystem(runtime.MatchingId, "closure_schedule " + string.Join("|", SwarmFieldClosureSchedule.Value.Select(entry => $"{entry.ClosureAtSeconds}s:{entry.Area}")));
        }
        if (!runtime.InitialFieldStateSent)
        {
            runtime.InitialFieldStateSent = true;
            using var packet = Packet.Create((int)Protocol.G_TO_C_SWARM_FIELD_STATE);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SWARM_FIELD_STATE
            {
                StartedAtUnixMs = new DateTimeOffset(closures.GameStartTime!.Value).ToUnixTimeMilliseconds()
            }));
            foreach (var session in sessions)
            {
                session.TrySend(packet);
            }
        }

        var closedAreas = closures.CloseDueAreas();
        foreach (var area in closedAreas)
        {
            eventLogs.LogClosure(runtime.MatchingId, area.ToString());
            using var packet = Packet.Create((int)Protocol.G_TO_C_AREA_CLOSED);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_AREA_CLOSED
            {
                AreaType = area,
                IsClosed = true
            }));
            foreach (var session in sessions)
            {
                session.TrySend(packet);
            }
        }

        if (closedAreas.Count == 0)
        {
            return;
        }

        foreach (int doorId in runtime.Doors.CloseDoorsForAreas(closedAreas))
        {
            using var packet = PacketMaker.G_TO_C_DOOR_STATE_UPDATE(doorId, false);
            foreach (var session in sessions)
            {
                session.TrySend(packet);
            }
        }

        var closedAreaSet = closedAreas.ToHashSet();
        foreach (var owner in runtime.GetAlivePlayers())
        {
            if (owner.Position is not { } ownerPosition)
            {
                continue;
            }
            var ownerCell = ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, ownerPosition);
            if (closedAreaSet.Contains(GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, ownerCell)))
            {
                continue;
            }

            var orbTiers = orbTrails.GetOrbTiersInOrder(runtime, owner);
            int firstClosedOrdinal = orbTiers.Count;
            Vector3f? firstClosedOrbPosition = null;
            var cutArea = AreaType.None;
            for (int ordinal = orbTiers.Count - 1; ordinal >= 0; ordinal--)
            {
                var orbPosition = orbTrails.GetOrbPosition(runtime, owner, ordinal, ownerPosition, orbTiers);
                var orbCell = ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, orbPosition);
                var orbArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, orbCell);
                if (!closedAreaSet.Contains(orbArea))
                {
                    break;
                }
                firstClosedOrdinal = ordinal;
                firstClosedOrbPosition = orbPosition;
                cutArea = orbArea;
            }

            if (firstClosedOrbPosition == null)
            {
                continue;
            }

            var destroyedOrbs = orbTrails.DestroyOrbsFromOrdinal(runtime, owner, firstClosedOrdinal);
            foreach (var orb in destroyedOrbs)
            {
                owner.Session?.SendOrbUpdate(orb);
            }

            using var ringPacket = Packet.Create((int)Protocol.G_TO_C_ORB_RING_EFFECT);
            ringPacket.SetBody(MessagePackSerializer.Serialize(new G_TO_C_ORB_RING_EFFECT
            {
                OwnerPlayerId = owner.PlayerId,
                CenterX = firstClosedOrbPosition.X,
                CenterY = firstClosedOrbPosition.Y,
                Radius = PlayerOrbTrailService.CutFlashRadius,
                Kind = PlayerOrbTrailService.CutVfxKind,
                VictimPlayerId = owner.PlayerId,
                FromOrdinal = firstClosedOrdinal
            }));
            foreach (var viewer in runtime.GetPlayers())
            {
                if (viewer.CurrentArea == cutArea)
                {
                    viewer.Session?.TrySend(ringPacket);
                }
            }
            eventLogs.LogSystem(runtime.MatchingId, $"closure_orb_destroyed player={owner.PlayerId} from={firstClosedOrdinal} count={destroyedOrbs.Count}");
        }
    }

    public virtual void ProcessDamageTick(MatchRuntime runtime)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Field damage settlement requires the match lock.");
        }
        if (runtime.IsEnded)
        {
            return;
        }

        long matchingId = runtime.MatchingId;
        var alivePlayers = runtime.GetAlivePlayers();
        int aliveCount = alivePlayers.Count;
        if (aliveCount <= 1)
        {
            long lastPlayerId = aliveCount == 1 ? alivePlayers[0].PlayerId : 0;
            if (lastPlayerId > 0)
            {
                matchResults.FinalizeMatch(matchingId, lastPlayerId);
                return;
            }
            matchCleanup.EndBotOnlyMatchIfSettled(matchingId, lastPlayerId);
            return;
        }

        var nowUtc = DateTime.UtcNow;
        var targets = alivePlayers.Select(player => (Player: player, HealthBefore: player.Health, Damage: GetDamagePerTick(runtime, player.Position, nowUtc))).ToList();
        foreach (var target in targets)
        {
            if (target.Damage == 0)
            {
                continue;
            }
            healthService.ApplyDamage(runtime, target.Player, target.Damage, handleElimination: false);
        }

        var lethalTargets = targets.Where(target => target.Damage > 0 && target.HealthBefore - target.Damage <= 0).ToList();
        if (lethalTargets.Count == 0)
        {
            return;
        }

        var resolution = ResolveEliminationOrder(lethalTargets.Select(target => new MatchSettlementCandidate(target.Player.PlayerId, target.HealthBefore, eventLogs.GetResultStats(matchingId, target.Player.PlayerId).TotalDamageDealt, target.Damage)));
        var eliminationBestToWorst = resolution.BestToWorst.ToList();
        if (lethalTargets.Count == aliveCount)
        {
            eliminationBestToWorst.RemoveAt(0);
        }

        if (resolution.BestToWorst.Count > 1)
        {
            string orderedPlayers = string.Join(",", resolution.BestToWorst.Select(candidate => candidate.PlayerId));
            eventLogs.LogSystem(matchingId, $"environment_tiebreak criterion={resolution.DecisiveCriterion} best_to_worst={orderedPlayers}");
        }

        int rank = aliveCount;
        foreach (var candidate in eliminationBestToWorst.AsEnumerable().Reverse())
        {
            var target = lethalTargets.First(entry => entry.Player.PlayerId == candidate.PlayerId);
            matchEliminations.EliminatePlayer(runtime, target.Player, EliminationReason.PRESSURE_FIELD, deferGameOver: true, forcedRank: rank);
            rank--;
        }

        (bool isGameOver, long? winnerId) = runtime.CheckGameOver();
        if (isGameOver && winnerId.HasValue)
        {
            matchResults.FinalizeMatch(matchingId, winnerId.Value, MatchEndReason.PressureFieldSettlement, resolution.DecisiveCriterion);
        }
    }

    internal static int GetDamagePerTick(MatchRuntime runtime, Vector3f? worldPosition, DateTime nowUtc)
    {
        if (worldPosition == null)
        {
            return 0;
        }
        double safeDistance = runtime.Closures.GetSafeDistance(nowUtc);
        if (safeDistance >= double.MaxValue)
        {
            return 0;
        }

        var cell = ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, worldPosition);
        double over = SwarmPressureField.GetDistance(cell) - safeDistance;
        if (over <= 0)
        {
            return 0;
        }
        return BaseDamagePerTick + (int)(over * DamagePerExtraCell);
    }

    internal readonly record struct MatchSettlementCandidate(
        long PlayerId,
        int PreDamageHealth,
        int TotalPvpDamage,
        int FieldDamage);

    internal static (IReadOnlyList<MatchSettlementCandidate> BestToWorst, MatchTieBreakCriterion DecisiveCriterion) ResolveEliminationOrder(IEnumerable<MatchSettlementCandidate> candidates)
    {
        var ordered = candidates
            .DistinctBy(candidate => candidate.PlayerId)
            .OrderByDescending(candidate => candidate.PreDamageHealth)
            .ThenByDescending(candidate => candidate.TotalPvpDamage)
            .ThenBy(candidate => candidate.FieldDamage)
            .ThenBy(candidate => candidate.PlayerId < 0)
            .ThenBy(candidate => candidate.PlayerId)
            .ToList();

        var criterion = MatchTieBreakCriterion.SingleCandidate;
        if (ordered.Count > 1)
        {
            var first = ordered[0];
            var second = ordered[1];
            if (first.PreDamageHealth != second.PreDamageHealth)
            {
                criterion = MatchTieBreakCriterion.PreDamageHealth;
            }
            else if (first.TotalPvpDamage != second.TotalPvpDamage)
            {
                criterion = MatchTieBreakCriterion.CumulativePvpDamage;
            }
            else if (first.FieldDamage != second.FieldDamage)
            {
                criterion = MatchTieBreakCriterion.FieldDamage;
            }
            else
            {
                criterion = MatchTieBreakCriterion.PlayerId;
            }
        }

        return (ordered, criterion);
    }
}
