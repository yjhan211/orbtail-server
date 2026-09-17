using game_server.players;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches;

/// <summary>
///     매치의 구역 폐쇄와 자기장 피해를 처리한다.
///     폐쇄된 구역의 문을 닫고 잔류 오브를 제거하며, 자기장 피해로 동시에 탈락하는 참가자의 순위를 결정한다.
///     상태는 MatchRuntime이 소유하고, 호출자는 매치 잠금을 보유해야 한다.
/// </summary>
internal class MatchFieldService(
    ILogger<MatchFieldService> logger,
    PlayerOrbTrailService orbTrails,
    PlayerHealthService healthService,
    MatchCleanupService matchCleanup,
    PlayerEliminationService matchEliminations,
    MatchResultService matchResults,
    MatchSynchronizationService synchronization)
{
    internal static readonly Lazy<IReadOnlyList<(AreaType Area, int ClosureAtSeconds)>> SwarmFieldClosureSchedule = new(BuildClosureSchedule, LazyThreadSafetyMode.ExecutionAndPublication);

    private static IReadOnlyList<(AreaType Area, int ClosureAtSeconds)> BuildClosureSchedule()
    {
        var indexed = new List<(AreaType Area, int ClosureAtSeconds, int Index)>();
        foreach (var area in SwarmPressureField.GetKnownAreas())
        {
            indexed.Add((area, SwarmPressureField.GetAreaClosureSeconds(area), indexed.Count));
        }
        indexed.Sort(static (left, right) =>
        {
            int byTime = left.ClosureAtSeconds.CompareTo(right.ClosureAtSeconds);
            return byTime != 0 ? byTime : left.Index.CompareTo(right.Index);
        });

        var schedule = new List<(AreaType Area, int ClosureAtSeconds)>(indexed.Count);
        foreach (var entry in indexed)
        {
            schedule.Add((entry.Area, entry.ClosureAtSeconds));
        }
        return schedule.AsReadOnly();
    }

    public void ProcessTick(MatchRuntime runtime, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Field tick requires the match lock.");
        }
        if (!runtime.IsGameplayActive(nowUtc) || runtime.StartsAtUtc is not { } startedAtUtc)
        {
            return;
        }

        long elapsedSeconds = (nowUtc - startedAtUtc).Ticks / TimeSpan.TicksPerSecond;
        long damageInterval = elapsedSeconds / Config.ENVIRONMENTAL_TICK_INTERVAL_SECONDS;
        if (damageInterval > runtime.LastFieldDamageInterval)
        {
            runtime.LastFieldDamageInterval = damageInterval;
            ProcessDamageTick(runtime, nowUtc);
            if (runtime.IsEnded)
            {
                return;
            }
        }

        if (elapsedSeconds > runtime.LastAreaClosureSecond)
        {
            runtime.LastAreaClosureSecond = elapsedSeconds;
            ProcessClosureTick(runtime, nowUtc);
        }
    }

    public virtual void ProcessClosureTick(MatchRuntime runtime, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Closure tick requires the match lock.");
        }

        var closures = runtime.Closures;
        closures.InitializeMatching(SwarmFieldClosureSchedule.Value);
        if (!runtime.InitialFieldStateSent)
        {
            runtime.InitialFieldStateSent = true;
            synchronization.QueueBroadcastPacket(runtime, Protocol.G_TO_C_SWARM_FIELD_STATE, new G_TO_C_SWARM_FIELD_STATE
            {
                StartedAtUnixMs = new DateTimeOffset(closures.GameStartTime!.Value).ToUnixTimeMilliseconds()
            });
        }

        var closedAreas = closures.CloseDueAreas();
        if (closedAreas.Count == 0)
        {
            return;
        }

        var closedAreaSet = new HashSet<AreaType>();
        foreach (var area in closedAreas)
        {
            closedAreaSet.Add(area);
            logger.LogInformation("Area closed: MatchingId={MatchingId}, Area={Area}", runtime.MatchingId, area);
            synchronization.QueueBroadcastPacket(runtime, Protocol.G_TO_C_AREA_CLOSED, new G_TO_C_AREA_CLOSED
            {
                AreaType = area,
                IsClosed = true
            });
        }

        runtime.Doors.CloseDoorsForAreas(closedAreas);

        foreach (var owner in runtime.GetAlivePlayers())
        {
            if (owner.Position is not { } ownerPosition)
            {
                continue;
            }
            var ownerCell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, ownerPosition);
            if (closedAreaSet.Contains(GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, ownerCell)))
            {
                continue;
            }

            var orbTiers = PlayerOrbTrailService.GetOrbTiersInOrder(runtime, owner);
            int firstClosedOrdinal = orbTiers.Count;
            Vector3f? firstClosedOrbPosition = null;
            var cutArea = AreaType.None;
            for (int ordinal = orbTiers.Count - 1; ordinal >= 0; ordinal--)
            {
                var orbPosition = orbTrails.GetOrbPosition(runtime, owner, ordinal, ownerPosition, orbTiers);
                var orbCell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, orbPosition);
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

            var destroyedOrbs = orbTrails.DestroyOrbsFromOrdinal(runtime, owner, firstClosedOrdinal, nowUtc);
            foreach (var orb in destroyedOrbs)
            {
                owner.Session?.SendOrbUpdate(orb);
            }

            synchronization.QueueAreaPacket(runtime, cutArea, Protocol.G_TO_C_ORB_TAIL_CUT, new G_TO_C_ORB_TAIL_CUT
            {
                CutterPlayerId = owner.PlayerId,
                VictimPlayerId = owner.PlayerId,
                FromOrdinal = firstClosedOrdinal,
                X = firstClosedOrbPosition.X,
                Y = firstClosedOrbPosition.Y
            });
        }
    }

    public virtual void ProcessDamageTick(MatchRuntime runtime, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Field damage settlement requires the match lock.");
        }
        if (runtime.IsEnded)
        {
            return;
        }

        var alivePlayers = runtime.GetAlivePlayers();
        int aliveCount = alivePlayers.Count;
        if (aliveCount <= 1)
        {
            long lastPlayerId = aliveCount == 1 ? alivePlayers[0].PlayerId : 0;
            if (lastPlayerId > 0)
            {
                matchResults.FinalizeMatch(runtime, lastPlayerId);
                return;
            }
            matchCleanup.EndBotOnlyMatchIfSettled(runtime.MatchingId, lastPlayerId);
            return;
        }

        var lethalCandidates = new List<MatchSettlementCandidate>();
        foreach (var player in alivePlayers)
        {
            int healthBefore = player.Health;
            int damage = GetDamagePerTick(runtime, player.Position, nowUtc);
            if (damage == 0)
            {
                continue;
            }

            healthService.ApplyDamage(runtime, player, damage, handleElimination: false);
            if (healthBefore - damage <= 0)
            {
                lethalCandidates.Add(new MatchSettlementCandidate(player.PlayerId, healthBefore, player.PvpDamageDealt, damage));
            }
        }
        if (lethalCandidates.Count == 0)
        {
            return;
        }

        var resolution = ResolveEliminationOrder(lethalCandidates);
        if (resolution.BestToWorst.Count > 1)
        {
            var order = new List<long>(resolution.BestToWorst.Count);
            foreach (var candidate in resolution.BestToWorst)
            {
                order.Add(candidate.PlayerId);
            }
            logger.LogInformation("Simultaneous elimination tie-break: MatchingId={MatchingId}, Criterion={Criterion}, BestToWorst={Order}", runtime.MatchingId, resolution.DecisiveCriterion, string.Join(",", order));
        }

        int firstEliminatedIndex = lethalCandidates.Count == aliveCount ? 1 : 0;
        int rank = aliveCount;
        for (int index = resolution.BestToWorst.Count - 1; index >= firstEliminatedIndex; index--)
        {
            var target = runtime.GetPlayer(resolution.BestToWorst[index].PlayerId)!;
            matchEliminations.EliminatePlayer(runtime, target, EliminationReason.PRESSURE_FIELD, deferGameOver: true, forcedRank: rank);
            rank--;
        }

        (bool isGameOver, long? winnerId) = runtime.CheckGameOver();
        if (isGameOver && winnerId.HasValue)
        {
            matchResults.FinalizeMatch(runtime, winnerId.Value, MatchEndReason.PressureFieldSettlement, resolution.DecisiveCriterion);
        }
    }

    internal static int GetDamagePerTick(MatchRuntime runtime, Vector3f? worldPosition, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Field damage lookup requires the match lock.");
        }
        if (worldPosition == null)
        {
            return 0;
        }
        double safeDistance = runtime.Closures.GetSafeDistance(nowUtc);
        if (safeDistance >= double.MaxValue)
        {
            return 0;
        }

        var cell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, worldPosition);
        double over = SwarmPressureField.GetDistance(cell) - safeDistance;
        if (over <= 0)
        {
            return 0;
        }
        return Config.SWARM_FIELD_BASE_DAMAGE_PER_TICK + (int)(over * Config.SWARM_FIELD_DAMAGE_PER_EXTRA_CELL);
    }

    internal readonly record struct MatchSettlementCandidate(
        long PlayerId,
        int PreDamageHealth,
        int TotalPvpDamage,
        int FieldDamage);


    internal static (IReadOnlyList<MatchSettlementCandidate> BestToWorst, MatchTieBreakCriterion DecisiveCriterion) ResolveEliminationOrder(IEnumerable<MatchSettlementCandidate> candidates)
    {
        var ordered = new List<MatchSettlementCandidate>();
        var seenPlayerIds = new HashSet<long>();
        foreach (var candidate in candidates)
        {
            if (seenPlayerIds.Add(candidate.PlayerId))
            {
                ordered.Add(candidate);
            }
        }

        ordered.Sort(static (left, right) =>
        {
            int byHealth = right.PreDamageHealth.CompareTo(left.PreDamageHealth);
            if (byHealth != 0)
            {
                return byHealth;
            }
            int byPvpDamage = right.TotalPvpDamage.CompareTo(left.TotalPvpDamage);
            if (byPvpDamage != 0)
            {
                return byPvpDamage;
            }
            int byFieldDamage = left.FieldDamage.CompareTo(right.FieldDamage);
            if (byFieldDamage != 0)
            {
                return byFieldDamage;
            }
            bool leftIsBot = left.PlayerId < 0;
            bool rightIsBot = right.PlayerId < 0;
            if (leftIsBot != rightIsBot)
            {
                return leftIsBot ? 1 : -1;
            }
            return left.PlayerId.CompareTo(right.PlayerId);
        });

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
