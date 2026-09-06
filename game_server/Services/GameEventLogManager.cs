using System.Collections.Concurrent;
using System.Globalization;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
///     매치별 최근 인게임 이벤트 로그(운영·디버깅용)와 이동에서 파생하는 AREA_ENTER, 전투·오브 보드·
///     폐쇄 텔레메트리 상태를 보관한다. 마니또 세대의 사회적 증거 파생(AREA_STAY·ENCOUNTER·
///     FOLLOW_IN_CANDIDATE)은 #325에서 삭제했다 — 매치 요약은 MOVE·AREA_ENTER만 읽는다.
/// </summary>
public class GameEventLogManager
{
    private const int MaxEventsPerMatching = 5_000;
    private const int MaxArchivedMatchings = 50;

    private readonly ConcurrentDictionary<long, MatchingEventLog> _logs = new();
    private readonly ConcurrentDictionary<long, MatchingEventLog> _archivedLogs = new();
    private readonly ConcurrentDictionary<long, byte> _finalizedMatchings = new();
    private readonly ConcurrentDictionary<long, MatchCombatState> _matchCombatStates = new();
    private readonly ConcurrentDictionary<long, MatchTelemetryState> _telemetryStates = new();
    private readonly Queue<long> _archivedMatchingIds = new();
    private readonly object _archiveLock = new();
    private long _nextSeq;

    public void SetPlayerArea(long matchingId, long playerId, string area)
    {
        var log = _logs.GetOrAdd(matchingId, _ => new MatchingEventLog());
        log.SetPlayerArea(playerId, area, DateTimeOffset.UtcNow);
    }

    public void LogMove(long matchingId, long playerId, string fromArea, string toArea, bool isBot)
    {
        var log = _logs.GetOrAdd(matchingId, _ => new MatchingEventLog());
        var now = DateTimeOffset.UtcNow;

        log.AddMoveAndDerived(
            playerId,
            fromArea,
            toArea,
            isBot,
            now,
            CreateEntry);
        LogClosureMovement(matchingId, playerId, fromArea, toArea, isBot, now);
    }

    public void LogResource(long matchingId, long playerId, int staminaDelta, int corruptionDelta,
        int stamina, int corruption, bool staminaConverted, string reason, bool isBot)
    {
        var parts = new List<string>();
        if (staminaDelta != 0) parts.Add($"체력{(staminaDelta >= 0 ? "+" : "")}{staminaDelta}");
        if (corruptionDelta != 0) parts.Add($"오염{(corruptionDelta >= 0 ? "+" : "")}{corruptionDelta}");
        parts.Add($"(체력 {stamina}/오염 {corruption})");
        if (staminaConverted) parts.Add("[전환]");
        if (!string.IsNullOrEmpty(reason)) parts.Add($"<{reason}>");
        Append(matchingId, "RESOURCE", playerId, isBot, string.Join(" ", parts));
    }

    public void LogMission(long matchingId, long playerId, string description, bool isBot)
    {
        Append(matchingId, "MISSION", playerId, isBot, description);
    }

    public void LogElimination(
        long matchingId,
        long playerId,
        string reason,
        bool isBot,
        long attackerPlayerId = 0,
        bool isAreaClosureElimination = false,
        bool isOvertimeElimination = false)
    {
        var occurredAt = DateTimeOffset.UtcNow;
        LogFirstElimination(matchingId, playerId, reason, isBot, occurredAt);
        string sourceType = isAreaClosureElimination || isOvertimeElimination
            ? "closure"
            : attackerPlayerId != 0
                ? "pvp"
                : "mental";
        AppendAt(matchingId, "ELIMINATE", playerId, isBot, reason, occurredAt, entry =>
        {
            entry.ActorPlayerId = attackerPlayerId;
            entry.TargetPlayerId = playerId;
            entry.DamageSourceType = sourceType;
            entry.Outcome = reason;
            entry.IsAreaClosureElimination = isAreaClosureElimination;
            entry.IsOvertimeElimination = isOvertimeElimination;
        });
    }

    public void LogTierReached(
        long matchingId,
        long playerId,
        int itemId,
        int tier,
        bool isBot,
        DateTimeOffset? occurredAt = null)
    {
        if (tier is < 2 or > 3)
            return;

        var timestamp = occurredAt ?? DateTimeOffset.UtcNow;
        var state = _matchCombatStates.GetOrAdd(matchingId, _ => new MatchCombatState());
        lock (state.SyncRoot)
        {
            state.KnownPlayerIds.Add(playerId);
            if (tier == 2)
            {
                if (state.FirstTier2AtUnixMs.HasValue)
                    return;
                state.FirstTier2AtUnixMs = timestamp.ToUnixTimeMilliseconds();
            }
            else
            {
                if (state.FirstTier3AtUnixMs.HasValue)
                    return;
                state.FirstTier3AtUnixMs = timestamp.ToUnixTimeMilliseconds();
            }
        }

        AppendAt(
            matchingId,
            $"SURVIVOR_FIRST_T{tier}",
            playerId,
            isBot,
            $"First T{tier}: {FormatPlayer(playerId)} equipped or crafted Item{itemId}.",
            timestamp,
            entry =>
            {
                entry.WeaponItemId = itemId;
                entry.WeaponTier = tier;
                entry.IsFirstMilestone = true;
                entry.OccurredAtUnixMs = entry.TimestampUnixMs;
            });
    }

    public void LogHit(
        long matchingId,
        long attackerPlayerId,
        long targetPlayerId,
        int weaponItemId,
        int damage,
        bool isLethal,
        bool isBot,
        DateTimeOffset occurredAt,
        string? damageSourceType = null)
    {
        var state = _matchCombatStates.GetOrAdd(matchingId, _ => new MatchCombatState());
        int weaponTier = BattleItemCombatData.Get(weaponItemId)?.Tier ?? 0;
        int targetWeaponTier;
        int hitCount;
        int killCount = 0;
        long elapsedMilliseconds;
        long? previousHitGapMilliseconds;
        bool isFirstElimination = false;

        lock (state.SyncRoot)
        {
            state.KnownPlayerIds.Add(attackerPlayerId);
            state.KnownPlayerIds.Add(targetPlayerId);
            state.DamageDealtByPlayer.TryGetValue(attackerPlayerId, out int previousDamage);
            state.DamageDealtByPlayer[attackerPlayerId] = previousDamage + Math.Max(0, damage);
            if (!state.EngagementsByAttacker.TryGetValue(attackerPlayerId, out var engagement) ||
                engagement.TargetPlayerId != targetPlayerId)
            {
                engagement = new MatchCombatEngagement(
                    targetPlayerId,
                    "",
                    weaponItemId,
                    weaponTier,
                    0,
                    occurredAt);
                state.EngagementsByAttacker[attackerPlayerId] = engagement;
            }

            targetWeaponTier = engagement.TargetWeaponTier;
            elapsedMilliseconds = Math.Max(
                0,
                (long)(occurredAt - engagement.StartedAt).TotalMilliseconds);
            previousHitGapMilliseconds = engagement.LastHitAt.HasValue
                ? Math.Max(0, (long)(occurredAt - engagement.LastHitAt.Value).TotalMilliseconds)
                : null;
            engagement.LastHitAt = occurredAt;
            engagement.HitCount++;
            hitCount = engagement.HitCount;

            if (isLethal)
            {
                state.KillCountsByPlayer.TryGetValue(attackerPlayerId, out int previousKillCount);
                killCount = previousKillCount + 1;
                state.KillCountsByPlayer[attackerPlayerId] = killCount;
                isFirstElimination = !state.FirstEliminationAtUnixMs.HasValue;
                if (isFirstElimination)
                    state.FirstEliminationAtUnixMs = occurredAt.ToUnixTimeMilliseconds();

                var endingAttackers = state.EngagementsByAttacker
                    .Where(pair => pair.Key == targetPlayerId || pair.Value.TargetPlayerId == targetPlayerId)
                    .Select(pair => pair.Key)
                    .ToList();
                foreach (long endingAttacker in endingAttackers)
                    state.EngagementsByAttacker.Remove(endingAttacker);
            }
        }

        AppendAt(
            matchingId,
            "SURVIVOR_HIT",
            attackerPlayerId,
            isBot,
            $"{FormatPlayer(attackerPlayerId)} hit {FormatPlayer(targetPlayerId)} for {damage}; hit={hitCount}, elapsedMs={elapsedMilliseconds}, gapMs={previousHitGapMilliseconds?.ToString() ?? "first"}.",
            occurredAt,
            entry =>
            {
                entry.TargetPlayerId = targetPlayerId;
                entry.WeaponItemId = weaponItemId;
                entry.WeaponTier = weaponTier;
                entry.TargetWeaponTier = targetWeaponTier;
                entry.Damage = damage;
                entry.ElapsedMilliseconds = elapsedMilliseconds;
                entry.PreviousHitGapMilliseconds = previousHitGapMilliseconds;
                entry.HitCount = hitCount;
                entry.Outcome = isLethal ? "eliminated" : "hit";
                entry.DamageSourceType = damageSourceType;
                entry.OccurredAtUnixMs = entry.TimestampUnixMs;
            });

        if (!isLethal)
            return;

        if (isFirstElimination)
        {
            AppendAt(
                matchingId,
                "SURVIVOR_FIRST_ELIMINATION",
                targetPlayerId,
                BotPlayerManager.IsBotPlayerId(targetPlayerId),
                $"First elimination: {FormatPlayer(targetPlayerId)} was eliminated by {FormatPlayer(attackerPlayerId)}.",
                occurredAt,
                entry =>
                {
                    entry.WeaponItemId = weaponItemId;
                    entry.WeaponTier = weaponTier;
                    entry.TargetWeaponTier = targetWeaponTier;
                    entry.ElapsedMilliseconds = elapsedMilliseconds;
                    entry.HitCount = hitCount;
                    entry.IsFirstMilestone = true;
                    entry.Outcome = "combat";
                    entry.OccurredAtUnixMs = entry.TimestampUnixMs;
                });
        }

        AppendAt(
            matchingId,
            "SURVIVOR_COMBAT_ELIMINATION",
            attackerPlayerId,
            isBot,
            $"{FormatPlayer(attackerPlayerId)} eliminated {FormatPlayer(targetPlayerId)} with T{weaponTier}; killCount={killCount}, elapsedMs={elapsedMilliseconds}.",
            occurredAt,
            entry =>
            {
                entry.TargetPlayerId = targetPlayerId;
                entry.WeaponItemId = weaponItemId;
                entry.WeaponTier = weaponTier;
                entry.TargetWeaponTier = targetWeaponTier;
                entry.ElapsedMilliseconds = elapsedMilliseconds;
                entry.HitCount = hitCount;
                entry.KillCount = killCount;
                entry.IsFirstMilestone = isFirstElimination;
                entry.Outcome = "eliminated";
                entry.OccurredAtUnixMs = entry.TimestampUnixMs;
            });
    }

    /// <summary>
    ///     꼬리 절단 (#226 F 계측): 절단자·피해자·절단 지점·파괴 수 — 절단 압력과 점수 이동의
    ///     단일 출처. 요약의 절단 지표가 이 이벤트만 읽는다.
    /// </summary>
    public void LogSwarmTrailCut(
        long matchingId,
        long cutterPlayerId,
        long ownerPlayerId,
        int tailOrdinal,
        int destroyedOrbCount,
        int orbCountBefore,
        int orbCountAfter,
        int attackOrbCountBefore,
        int attackOrbCountAfter,
        int rankAfter,
        string area)
    {
        Append(
            matchingId,
            "ORB_SUFFIX_CUT",
            cutterPlayerId,
            BotPlayerManager.IsBotPlayerId(cutterPlayerId),
            $"{FormatPlayer(cutterPlayerId)} cut {FormatPlayer(ownerPlayerId)} tail at ordinal {tailOrdinal}; " +
            $"destroyed={destroyedOrbCount}, orbs {orbCountBefore}->{orbCountAfter}, " +
            $"attackOrbs {attackOrbCountBefore}->{attackOrbCountAfter}, rankAfter={rankAfter}.",
            entry =>
            {
                entry.TargetPlayerId = ownerPlayerId;
                entry.Area = area;
                entry.TailOrdinal = tailOrdinal;
                entry.DestroyedOrbCount = destroyedOrbCount;
                // 이 모드에서 오브 수 = 승리 점수다 — 점수를 따로 싣지 않는다.
                entry.OrbCountBefore = orbCountBefore;
                entry.OrbCountAfter = orbCountAfter;
                entry.AttackOrbCountBefore = attackOrbCountBefore;
                entry.AttackOrbCountAfter = attackOrbCountAfter;
                entry.RankAfter = rankAfter;
                entry.OccurredAtUnixMs = entry.TimestampUnixMs;
            });
    }

    /// <summary>
    ///     절단 진입 (#227 6단계 계측): 절단이 성립한 그 순간 절단자가 선 자리를 몇 개의 적 오브
    ///     사거리가 덮고 있었나. 결과(크랙/절단)와 무관하게 남는다 — "많이 자르려면 더 위험한
    ///     곳으로 들어가야 한다"가 성립하는지 보는 단일 근거다.
    ///     후미 절단(TailOrdinal 큼)은 겹침이 적고, 머리 절단(TailOrdinal 작음)은 많아야 한다.
    /// </summary>
    public void LogSwarmCutAttempt(
        long matchingId,
        long cutterPlayerId,
        long ownerPlayerId,
        int tailOrdinal,
        int overlappingOrbRanges,
        int victimOrbRanges,
        int durabilityBeforeHit,
        int expectedOrbLoss,
        bool breaksNow,
        string area)
    {
        Append(
            matchingId,
            "CUT_ATTEMPT",
            cutterPlayerId,
            BotPlayerManager.IsBotPlayerId(cutterPlayerId),
            $"{FormatPlayer(cutterPlayerId)} entered {FormatPlayer(ownerPlayerId)} trail at ordinal {tailOrdinal}; " +
            $"durability={durabilityBeforeHit}, expectedLoss={expectedOrbLoss}, breaks={breaksNow}, " +
            $"guns={overlappingOrbRanges} (victim={victimOrbRanges}).",
            entry =>
            {
                entry.TargetPlayerId = ownerPlayerId;
                entry.Area = area;
                entry.TailOrdinal = tailOrdinal;
                entry.OverlappingOrbRanges = overlappingOrbRanges;
                entry.VictimOrbRanges = victimOrbRanges;
                entry.DurabilityBeforeHit = durabilityBeforeHit;
                entry.ExpectedOrbLoss = expectedOrbLoss;
                entry.Outcome = breaksNow ? "cut" : "crack";
                entry.OccurredAtUnixMs = entry.TimestampUnixMs;
            });
    }

    /// <summary>
    ///     반격 보호 창 결산 (#227 7단계): 절단자–피해자 쌍의 1.2초가 닫힐 때 한 번.
    ///     차단한 피해·타격·추가 절단, 역절단 성립 여부, 양측 이탈 여부를 남긴다 —
    ///     "깊은 절단 뒤 역절단률이 후미 절단보다 높은가"를 이 이벤트만으로 계산한다.
    /// </summary>
    public void LogSwarmRetaliationWindow(
        long matchingId,
        long cutterPlayerId,
        long victimPlayerId,
        int blockedDamage,
        int blockedHits,
        int blockedCuts,
        bool retaliated,
        bool bothDisengaged,
        string area)
    {
        Append(
            matchingId,
            "CUT_RETALIATION_WINDOW",
            victimPlayerId,
            BotPlayerManager.IsBotPlayerId(victimPlayerId),
            $"{FormatPlayer(victimPlayerId)} guarded from {FormatPlayer(cutterPlayerId)}; " +
            $"blocked={blockedDamage} over {blockedHits} hits and {blockedCuts} cuts, " +
            $"retaliated={retaliated}, disengaged={bothDisengaged}.",
            entry =>
            {
                entry.TargetPlayerId = cutterPlayerId;
                entry.Area = area;
                entry.BlockedDamage = blockedDamage;
                entry.BlockedHits = blockedHits;
                entry.BlockedCuts = blockedCuts;
                entry.Retaliated = retaliated;
                entry.BothDisengaged = bothDisengaged;
                entry.Outcome = retaliated ? "retaliated" : bothDisengaged ? "disengaged" : "held";
                entry.OccurredAtUnixMs = entry.TimestampUnixMs;
            });
    }

    /// <summary>
    ///     크랙 생존 (#226 F 계측): 방어 강화 오브가 유효 교차를 흡수한 순간 —
    ///     "방어 강화가 실제로 몇 번의 절단을 막았나"의 근거.
    /// </summary>

    /// <summary>성장 오퍼 제시 (#226 F 계측) — 오퍼→선택 지연·미선택 오퍼율의 근거.</summary>
    public void LogSwarmGrowthOffered(
        long matchingId,
        long playerId,
        bool isBot,
        int baseCost,
        int scoreSurcharge,
        int finalCost,
        int orbCount)
    {
        Append(
            matchingId,
            "ORB_GROWTH_CARDS_OFFERED",
            playerId,
            isBot,
            $"{FormatPlayer(playerId)} offered growth cards; cost={finalCost} (base {baseCost} + surcharge {scoreSurcharge}), orbs={orbCount}.",
            entry =>
            {
                entry.GrowthBaseCost = baseCost;
                entry.GrowthScoreSurcharge = scoreSurcharge;
                entry.GrowthFinalCost = finalCost;
                entry.OrbCountBefore = orbCount;
                entry.OccurredAtUnixMs = entry.TimestampUnixMs;
            });
    }

    /// <summary>
    ///     성장 카드 선택 성공 (#226 F 계측): 역할·등급·비용 분해·선택 시점 상태를 남긴다 —
    ///     선택 간격·비용 곡선·역할 분포 검증의 단일 출처. 실패한 픽은 남기지 않는다.
    /// </summary>
    public void LogSwarmGrowthSelected(
        long matchingId,
        long playerId,
        bool isBot,
        string cardRole,
        int cardGrade,
        int baseCost,
        int scoreSurcharge,
        int finalCost,
        int successCountBefore,
        int orbCountBefore)
    {
        Append(
            matchingId,
            "ORB_GROWTH_CARD_SELECTED",
            playerId,
            isBot,
            $"{FormatPlayer(playerId)} picked {cardRole} grade {cardGrade}; cost={finalCost} (base {baseCost} + surcharge {scoreSurcharge}), n={successCountBefore}, orbs={orbCountBefore}.",
            entry =>
            {
                entry.CardRole = cardRole;
                entry.CardGrade = cardGrade;
                entry.GrowthBaseCost = baseCost;
                entry.GrowthScoreSurcharge = scoreSurcharge;
                entry.GrowthFinalCost = finalCost;
                entry.GrowthSuccessCountBefore = successCountBefore;
                entry.OrbCountBefore = orbCountBefore;
                entry.OccurredAtUnixMs = entry.TimestampUnixMs;
            });
    }

    public void LogSwarmAfterimageHit(
        long matchingId,
        int monsterId,
        long targetPlayerId,
        string area,
        int damage,
        int corruptionBefore,
        int corruptionAfter,
        bool isLethal,
        bool isBot,
        DateTimeOffset occurredAt)
    {
        AppendAt(
            matchingId,
            "AFTERIMAGE_HIT",
            targetPlayerId,
            isBot,
            $"Afterimage {monsterId} hit {FormatPlayer(targetPlayerId)} for {damage}; corruption={corruptionBefore}->{corruptionAfter}.",
            occurredAt,
            entry =>
            {
                entry.TargetPlayerId = targetPlayerId;
                entry.Area = area;
                entry.MonsterId = monsterId;
                entry.DamageSourceType = "emotion_afterimage";
                entry.Damage = damage;
                entry.CorruptionBefore = corruptionBefore;
                entry.CorruptionAfter = corruptionAfter;
                entry.Outcome = isLethal ? "eliminated" : "hit";
                entry.OccurredAtUnixMs = entry.TimestampUnixMs;
            });
    }
    public void LogSwarmAfterimageKilled(
        long matchingId,
        int monsterId,
        string area,
        bool isCore,
        long firstAttackerPlayerId,
        long lastAttackerPlayerId,
        IReadOnlyDictionary<long, int> damageByPlayer,
        bool isReinforcement = false)
    {
        var contributions = damageByPlayer
            .Where(pair => pair.Key != 0 && pair.Value > 0)
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key)
            .Select(pair => new MonsterDamageContribution(pair.Key, pair.Value))
            .ToList();

        Append(matchingId, "AFTERIMAGE_KILLED", lastAttackerPlayerId,
            BotPlayerManager.IsBotPlayerId(lastAttackerPlayerId),
            $"Afterimage {monsterId} killed: core={isCore}, reinforcement={isReinforcement}, first={firstAttackerPlayerId}, last={lastAttackerPlayerId}, contributors={contributions.Count}.",
            entry =>
            {
                entry.MonsterId = monsterId;
                entry.Area = area;
                entry.Outcome = isCore ? "core" : isReinforcement ? "reinforcement" : "normal";
                entry.FirstAttackerPlayerId = firstAttackerPlayerId;
                entry.LastAttackerPlayerId = lastAttackerPlayerId;
                entry.MonsterDamageContributions = contributions;
            });
    }
    public void LogBotMovementTickPerformance(
        long matchingId,
        double p50Milliseconds,
        double p95Milliseconds,
        double p99Milliseconds,
        double snapshotP95Milliseconds,
        double planningP95Milliseconds,
        double walkingP95Milliseconds,
        double broadcastP95Milliseconds,
        int sampleCount,
        int skippedTickCount,
        int maxConsecutiveSkippedTicks)
    {
        if (matchingId <= 0 || sampleCount <= 0)
            return;

        Append(matchingId, "SURVIVOR_BOT_MOVEMENT_TICK_PERFORMANCE", 0, false,
            $"Bot movement tick: p50={p50Milliseconds:F1}ms, p95={p95Milliseconds:F1}ms, p99={p99Milliseconds:F1}ms, " +
            $"sections p95 snapshot={snapshotP95Milliseconds:F1}ms, planning={planningP95Milliseconds:F1}ms, " +
            $"walking={walkingP95Milliseconds:F1}ms, broadcast={broadcastP95Milliseconds:F1}ms, skips={skippedTickCount}.",
            entry =>
            {
                entry.BotMovementTickP50Milliseconds = p50Milliseconds;
                entry.BotMovementTickP95Milliseconds = p95Milliseconds;
                entry.BotMovementTickP99Milliseconds = p99Milliseconds;
                entry.BotMovementSnapshotP95Milliseconds = snapshotP95Milliseconds;
                entry.BotMovementPlanningP95Milliseconds = planningP95Milliseconds;
                entry.BotMovementWalkingP95Milliseconds = walkingP95Milliseconds;
                entry.BotMovementBroadcastP95Milliseconds = broadcastP95Milliseconds;
                entry.BotMovementTickSampleCount = sampleCount;
                entry.BotMovementTickSkipCount = skippedTickCount;
                entry.BotMovementMaxConsecutiveSkipCount = maxConsecutiveSkippedTicks;
            });
    }

    public void LogClosure(long matchingId, string area)
    {
        Append(matchingId, "CLOSURE", 0, false, $"구역 폐쇄: {area}");
    }

    public void LogSystem(long matchingId, string description)
    {
        Append(matchingId, "SYSTEM", 0, false, description);
    }

    public void BeginMatch(long matchingId, int seed)
    {
        if (matchingId <= 0) return;
        _archivedLogs.TryRemove(matchingId, out _);
        var state = _telemetryStates.GetOrAdd(matchingId, _ => new MatchTelemetryState());
        var startedAt = DateTimeOffset.UtcNow;
        lock (state.SyncRoot)
        {
            if (state.MatchStarted) return;
            state.MatchStarted = true;
            state.MatchStartedAtUtc = startedAt;
        }

        _finalizedMatchings.TryRemove(matchingId, out _);

        Append(matchingId, "MATCH_STARTED", 0, false, $"Match started: seed={seed}.", entry =>
        {
            entry.MatchSeed = seed;
            entry.OccurredAtUnixMs = entry.TimestampUnixMs;
        });
    }

    public void LogSpawnAssignment(long matchingId, long playerId, int seed, int anchorIndex, int cellX, int cellY,
        string area, bool isBot)
    {
        var state = _telemetryStates.GetOrAdd(matchingId, _ => new MatchTelemetryState());
        lock (state.SyncRoot)
        {
            if (!state.SpawnLoggedPlayerIds.Add(playerId)) return;
        }

        Append(matchingId, "SPAWN_ASSIGNMENT", playerId, isBot,
            $"Spawn: seed={seed}, anchor={anchorIndex}, cell=({cellX},{cellY}), area={area}.", entry =>
            {
                entry.MatchSeed = seed;
                entry.SpawnAnchorIndex = anchorIndex;
                entry.CellX = cellX;
                entry.CellY = cellY;
                entry.Area = area;
            });
    }

    public void LogExploreStart(long matchingId, long playerId, int interactId, string area, bool isBot)
    {
        var now = DateTimeOffset.UtcNow;
        var state = _telemetryStates.GetOrAdd(matchingId, _ => new MatchTelemetryState());
        lock (state.SyncRoot)
        {
            state.ExploreStarts[(playerId, interactId)] = now;
            foreach (var warning in state.ClosureWarnings.Values.Where(warning =>
                         warning.PlayerId == playerId && string.Equals(warning.Area, area, StringComparison.Ordinal) &&
                         !warning.ExitedAt.HasValue))
                warning.AdditionalExploreCount++;
        }

        AppendAt(matchingId, "EXPLORE_STARTED", playerId, isBot,
            $"Explore started: interact={interactId}, area={area}.", now, entry =>
            {
                entry.ActivityId = interactId;
                entry.Area = area;
                entry.StartedAtUnixMs = entry.TimestampUnixMs;
            });
    }

    public void LogExploreCancelled(long matchingId, long playerId, int interactId, string area, string reason,
        bool isBot) => LogExploreFinished(matchingId, playerId, interactId, area, "EXPLORE_CANCELLED", reason,
        [], isBot);

    public void LogGroundItemSpawned(long matchingId, long discovererPlayerId, long groundItemUid, int itemId,
        string area, long priorityExpiresAtUnixMs, bool isBot)
    {
        Append(matchingId, "GROUND_ITEM_SPAWNED", discovererPlayerId, isBot,
            $"Ground item spawned: uid={groundItemUid}, item={itemId}, priorityUntil={priorityExpiresAtUnixMs}.", entry =>
            {
                entry.Area = area;
                entry.GroundItemUid = groundItemUid;
                entry.ItemId = itemId;
                entry.DiscovererPlayerId = discovererPlayerId;
                entry.PriorityExpiresAtUnixMs = priorityExpiresAtUnixMs;
            });
    }

    public void LogGroundItemPickup(long matchingId, long pickerPlayerId, long discovererPlayerId,
        long groundItemUid, int itemId, string area, bool autoUsed, bool isBot)
    {
        TrackEliminationDropPickup(matchingId, groundItemUid, pickerPlayerId);
        Append(matchingId, "GROUND_ITEM_PICKED_UP", pickerPlayerId, isBot,
            $"Ground item picked up: uid={groundItemUid}, item={itemId}, discoverer={discovererPlayerId}, autoUsed={autoUsed}.", entry =>
            {
                entry.Area = area;
                entry.GroundItemUid = groundItemUid;
                entry.ItemId = itemId;
                entry.DiscovererPlayerId = discovererPlayerId;
                entry.PickerPlayerId = pickerPlayerId;
                entry.AutoUsed = autoUsed;
            });
    }

    public void LogClosureWarningSnapshot(long matchingId, long playerId, IReadOnlyCollection<string> warningAreas,
        string currentArea, int corruption, int inventorySlotsUsed, int inventorySlotCapacity,
        long closureAtUnixMs, bool isBot)
    {
        var now = DateTimeOffset.UtcNow;
        var state = _telemetryStates.GetOrAdd(matchingId, _ => new MatchTelemetryState());
        lock (state.SyncRoot)
        {
            foreach (string warningArea in warningAreas)
                state.ClosureWarnings[(playerId, warningArea, closureAtUnixMs)] =
                    new ClosureWarningResponse(playerId, warningArea, now);
        }

        AppendAt(matchingId, "CLOSURE_WARNING_SNAPSHOT", playerId, isBot,
            $"Closure warning: areas={string.Join(',', warningAreas)}, current={currentArea}, corruption={corruption}, slots={inventorySlotsUsed}/{inventorySlotCapacity}.",
            now, entry =>
            {
                entry.WarningAreas = warningAreas.ToList();
                entry.Area = currentArea;
                entry.Corruption = corruption;
                entry.InventorySlotsUsed = inventorySlotsUsed;
                entry.InventorySlotCapacity = inventorySlotCapacity;
                entry.ClosureAtUnixMs = closureAtUnixMs;
            });
    }

    public void LogRecoveryUse(long matchingId, long playerId, int itemId, int recoveryAmount, string source,
        bool isBot)
    {
        if (recoveryAmount <= 0) return;
        Append(matchingId, "RECOVERY_USED", playerId, isBot,
            $"Recovery used: item={itemId}, amount={recoveryAmount}, source={source}.", entry =>
            {
                entry.ItemId = itemId;
                entry.RecoveryAmount = recoveryAmount;
                entry.Outcome = source;
            });
    }

    public void LogPelletPickupOutcome(long matchingId, long playerId, int itemId, int requestedRecovery,
        int effectiveRecovery, string outcome, bool isBot)
    {
        if (requestedRecovery < 0 || effectiveRecovery < 0)
            return;
        Append(matchingId, "PELLET_PICKUP_OUTCOME", playerId, isBot,
            $"Pellet outcome: item={itemId}, requested={requestedRecovery}, effective={effectiveRecovery}, outcome={outcome}.", entry =>
            {
                entry.ItemId = itemId;
                entry.RequestedRecoveryAmount = requestedRecovery;
                entry.RecoveryAmount = effectiveRecovery;
                entry.WastedRecoveryAmount = Math.Max(0, requestedRecovery - effectiveRecovery);
                entry.Outcome = outcome;
            });
    }
    public void LogEliminationDrop(long matchingId, long playerId, string area,
        IReadOnlyCollection<int> itemIds, IReadOnlyList<GroundItemInfo> spawnedItems,
        int totalRecovery, bool isBot)
    {
        var now = DateTimeOffset.UtcNow;
        var droppedItems = spawnedItems.Select(EliminationDroppedItem.FromGroundItem).ToList();
        float scatterRadius = droppedItems.Count == 0 ? 0f : droppedItems.Max(item => item.DistanceFromOrigin);
        AppendAt(matchingId, "ELIMINATION_DROP", playerId, isBot,
            $"Elimination drop: items={itemIds.Count}, recovery={totalRecovery}.", now, entry =>
            {
                entry.Area = area;
                entry.GeneratedItemIds = itemIds.ToList();
                entry.DropRecoveryTotal = totalRecovery;
                entry.EliminationDroppedItems = droppedItems;
                entry.DropScatterRadius = scatterRadius;
            });

        if (droppedItems.Count == 0)
            return;

        var state = _telemetryStates.GetOrAdd(matchingId, _ => new MatchTelemetryState());
        lock (state.SyncRoot)
            state.PendingEliminationDrops.Add(new PendingEliminationDrop(
                playerId, area, isBot, now.AddSeconds(3), droppedItems));
    }


    public static int CalculateDropRecoveryTotal(IEnumerable<int> itemIds) =>
        itemIds.Sum(itemId => itemId switch
        {
            GroundItemPickupPolicy.BandageItemId => GroundItemPickupPolicy.BandageRecovery,
            GroundItemPickupPolicy.FirstAidKitItemId => GroundItemPickupPolicy.FirstAidKitRecovery,
            _ => 0
        });

    public void LogOrbBoardTransition(long matchingId, long playerId,
        IReadOnlyCollection<InGameItemInfo> items, int equippedItemId, string area, string reason, bool isBot)
    {
        var itemIds = items.Where(item => item.Count > 0)
            .SelectMany(item => Enumerable.Repeat(item.ItemId, item.Count)).ToList();
        bool resonance = OrbData.TryGetActivePair(itemIds, out OrbColor color, out int supportTier);
        var transition = TrackOrbTelemetry(matchingId, playerId, itemIds, equippedItemId, resonance, color);
        Append(matchingId, "SURVIVOR_ORB_BOARD_STATE", playerId, isBot,
            $"Orb board: reason={reason}, equipped={equippedItemId}, resonance={(resonance ? color : OrbColor.None)}.", entry =>
            {
                entry.Area = area;
                entry.Outcome = reason;
                entry.GeneratedItemIds = itemIds;
                entry.BoardItemIds = itemIds;
                entry.PreviousBoardItemIds = transition.PreviousBoardItemIds;
                entry.WeaponItemId = equippedItemId;
                entry.PreviousEquippedItemId = transition.PreviousEquippedItemId;
                entry.EquippedColor = transition.EquippedColor.ToString();
                entry.PreviousEquippedColor = transition.PreviousEquippedColor.ToString();
                entry.WeaponTier = resonance ? supportTier : 0;
                entry.ResonanceActive = resonance;
                entry.ResonanceColor = resonance ? color.ToString() : OrbColor.None.ToString();
                entry.PreviousResonanceActive = transition.PreviousResonanceActive;
                entry.PreviousResonanceColor = transition.PreviousResonanceColor.ToString();
                entry.ResonanceProfile = GetResonanceProfile(resonance ? color : OrbColor.None);
                entry.MergeCandidateDurationSeconds = transition.EndedMergeCandidateDurationSeconds;
            });

        if (transition.MergeCandidateBecameAvailable)
            Append(matchingId, "SURVIVOR_ORB_MERGE_AVAILABLE", playerId, isBot,
                $"Orb merge became available: reason={reason}.", entry =>
                {
                    entry.Area = area;
                    entry.Outcome = reason;
                    entry.BoardItemIds = itemIds;
                    entry.WeaponItemId = equippedItemId;
                });

        if (transition.EndedMergeCandidateDurationSeconds.HasValue)
            Append(matchingId, "SURVIVOR_ORB_MERGE_WINDOW_ENDED", playerId, isBot,
                $"Orb merge window ended: reason={reason}, held={transition.EndedMergeCandidateDurationSeconds.Value:F1}s.", entry =>
                {
                    entry.Area = area;
                    entry.Outcome = reason;
                    entry.MergeCandidateDurationSeconds = transition.EndedMergeCandidateDurationSeconds;
                    entry.BoardItemIds = itemIds;
                    entry.PreviousBoardItemIds = transition.PreviousBoardItemIds;
                });

        if (transition.FirstOrbPickup)
            Append(matchingId, "SURVIVOR_ORB_FIRST_PICKUP", playerId, isBot,
                $"First orb pickup: slots={transition.BoardItemCount}/{Config.LEGACY_INVENTORY_SLOT_COUNT}.", entry =>
                {
                    entry.Area = area;
                    entry.Outcome = reason;
                    entry.BoardItemIds = itemIds;
                    entry.InventorySlotsUsed = transition.BoardItemCount;
                    entry.InventorySlotCapacity = Config.LEGACY_INVENTORY_SLOT_COUNT;
                    entry.ElapsedMilliseconds = transition.MatchElapsedMilliseconds;
                });

        if (transition.BoardReachedCapacity)
            Append(matchingId, "SURVIVOR_ORB_BOARD_FULL", playerId, isBot,
                $"Orb board reached capacity: slots={transition.BoardItemCount}/{Config.LEGACY_INVENTORY_SLOT_COUNT}.", entry =>
                {
                    entry.Area = area;
                    entry.Outcome = reason;
                    entry.BoardItemIds = itemIds;
                    entry.InventorySlotsUsed = transition.BoardItemCount;
                    entry.InventorySlotCapacity = Config.LEGACY_INVENTORY_SLOT_COUNT;
                    entry.ElapsedMilliseconds = transition.MatchElapsedMilliseconds;
                });

        if (transition.PreviousResonanceActive != resonance ||
            transition.PreviousResonanceColor != (resonance ? color : OrbColor.None))
        {
            string type = resonance ? "SURVIVOR_ORB_RESONANCE_APPLIED" : "SURVIVOR_ORB_RESONANCE_REMOVED";
            var eventColor = resonance ? color : transition.PreviousResonanceColor;
            Append(matchingId, type, playerId, isBot,
                $"Orb resonance {(resonance ? "applied" : "removed")}: color={eventColor}, reason={reason}.", entry =>
                {
                    entry.Area = area;
                    entry.Outcome = reason;
                    entry.WeaponItemId = equippedItemId;
                    entry.WeaponTier = resonance ? supportTier : 0;
                    entry.ResonanceActive = resonance;
                    entry.ResonanceColor = eventColor.ToString();
                    entry.ResonanceProfile = GetResonanceProfile(eventColor);
                });
        }
    }

    public void LogSummonStoneAward(long matchingId, long playerId, int monsterId, int amount, int balance,
        string area, bool isCore, bool isBot)
    {
        if (amount <= 0) return;

        var now = DateTimeOffset.UtcNow;
        var state = _telemetryStates.GetOrAdd(matchingId, _ => new MatchTelemetryState());
        bool firstStone;
        long? elapsedMilliseconds;
        lock (state.SyncRoot)
        {
            firstStone = state.FirstSummonStoneLoggedPlayerIds.Add(playerId);
            elapsedMilliseconds = state.MatchStartedAtUtc.HasValue
                ? Math.Max(0, (long)(now - state.MatchStartedAtUtc.Value).TotalMilliseconds)
                : null;
        }

        AppendAt(matchingId, "SUMMON_STONE_AWARDED", playerId, isBot,
            $"Summon stones awarded: monster={monsterId}, amount={amount}, balance={balance}, core={isCore}.",
            now, entry =>
            {
                entry.ActivityId = monsterId;
                entry.Area = area;
                entry.Outcome = isCore ? "core" : "normal";
                entry.SummonStoneDelta = amount;
                entry.SummonStoneBalance = balance;
                entry.IsFirstMilestone = firstStone;
                entry.ElapsedMilliseconds = elapsedMilliseconds;
            });
    }

    public void LogOrbSummonAttempt(long matchingId, long playerId, bool success, ErrorCode errorCode,
        int summonedItemId, int stoneBalance, int nextCost, int successfulSummonCount, string area, bool isBot)
    {
        var now = DateTimeOffset.UtcNow;
        var state = _telemetryStates.GetOrAdd(matchingId, _ => new MatchTelemetryState());
        bool firstSuccessfulSummon = false;
        long? elapsedMilliseconds;
        lock (state.SyncRoot)
        {
            if (success)
                firstSuccessfulSummon = state.FirstSuccessfulSummonLoggedPlayerIds.Add(playerId);
            elapsedMilliseconds = state.MatchStartedAtUtc.HasValue
                ? Math.Max(0, (long)(now - state.MatchStartedAtUtc.Value).TotalMilliseconds)
                : null;
        }

        string type = success ? "ORB_SUMMON_SUCCEEDED" : "ORB_SUMMON_BLOCKED";
        AppendAt(matchingId, type, playerId, isBot,
            $"Orb summon: success={success}, error={errorCode}, item={summonedItemId}, balance={stoneBalance}, nextCost={nextCost}, count={successfulSummonCount}.",
            now, entry =>
            {
                entry.Area = area;
                entry.Outcome = errorCode.ToString();
                entry.ItemId = summonedItemId > 0 ? summonedItemId : null;
                entry.SummonStoneBalance = stoneBalance;
                entry.NextSummonCost = nextCost;
                entry.SuccessfulSummonCount = successfulSummonCount;
                entry.IsFirstMilestone = firstSuccessfulSummon;
                entry.ElapsedMilliseconds = elapsedMilliseconds;
            });
    }
    public void LogOrbPickupBlockedFull(
        long matchingId,
        long playerId,
        int itemId,
        string area,
        IReadOnlyCollection<InGameItemInfo> items,
        bool isBot)
    {
        if (!OrbData.IsOrbItem(itemId) && !OrbData.IsRecoveryOrb(itemId))
            return;

        var boardItemIds = items.Where(item => item.Count > 0)
            .SelectMany(item => Enumerable.Repeat(item.ItemId, item.Count))
            .ToList();
        int boardItemCount = CountBoardOrbs(boardItemIds);
        Append(matchingId, "SURVIVOR_ORB_PICKUP_BLOCKED_FULL", playerId, isBot,
            $"Orb pickup blocked: item={itemId}, slots={boardItemCount}/{Config.LEGACY_INVENTORY_SLOT_COUNT}.", entry =>
            {
                entry.Area = area;
                entry.ItemId = itemId;
                entry.BoardItemIds = boardItemIds;
                entry.InventorySlotsUsed = boardItemCount;
                entry.InventorySlotCapacity = Config.LEGACY_INVENTORY_SLOT_COUNT;
            });
    }

    public void LogOrbAttackTargets(long matchingId, IReadOnlyCollection<ProximityCombatAttack> attacks,
        IReadOnlyCollection<ProximityCombatAttack> actualHits,
        IReadOnlyDictionary<long, OrbColor> activeOrbColors)
    {
        foreach (var attackerAttacks in attacks.GroupBy(attack => attack.AttackerPlayerId))
        {
            if (!activeOrbColors.TryGetValue(attackerAttacks.Key, out var color) || color == OrbColor.None)
                continue;

            var volley = attackerAttacks.ToList();
            var hitTargets = actualHits
                .Where(attack => attack.AttackerPlayerId == attackerAttacks.Key)
                .Select(attack => attack.TargetPlayerId)
                .Distinct()
                .ToList();
            var primary = volley[0];
            Append(matchingId, "SURVIVOR_ORB_ATTACK_TARGETS", primary.AttackerPlayerId,
                BotPlayerManager.IsBotPlayerId(primary.AttackerPlayerId),
                $"Orb volley: color={color}, candidates={primary.CandidateTargetCount}, targets={volley.Count}.", entry =>
                {
                    entry.Area = primary.Area.ToString();
                    entry.WeaponItemId = primary.WeaponItemId;
                    entry.ResonanceActive = true;
                    entry.ResonanceColor = color.ToString();
                    entry.ResonanceProfile = GetResonanceProfile(color);
                    entry.CandidateTargetCount = primary.CandidateTargetCount;
                    entry.ValidTargetCount = volley.Count;
                    entry.HitTargetCount = hitTargets.Count;
                    entry.AttackTargetPlayerIds = hitTargets;
                });
        }
    }

    private OrbTransitionSnapshot TrackOrbTelemetry(long matchingId, long playerId, IReadOnlyList<int> itemIds,
        int equippedItemId, bool active, OrbColor color)
    {
        var state = _telemetryStates.GetOrAdd(matchingId, _ => new MatchTelemetryState());
        var now = DateTimeOffset.UtcNow;
        lock (state.SyncRoot)
        {
            var telemetry = state.OrbTelemetry.GetValueOrDefault(playerId) ?? new OrbTelemetry();
            state.OrbTelemetry[playerId] = telemetry;
            var previousBoard = telemetry.BoardItemIds.ToList();
            int previousEquippedItemId = telemetry.EquippedItemId;
            bool previousActive = telemetry.Active;
            OrbColor previousActiveColor = telemetry.ActiveColor;
            OrbColor previousEquippedColor = telemetry.EquippedColor;
            var equippedColor = OrbData.TryGetColorAndTier(equippedItemId, out var resolvedColor, out _)
                ? resolvedColor : OrbColor.None;
            if (telemetry.EquippedColor != OrbColor.None && equippedColor != OrbColor.None && telemetry.EquippedColor != equippedColor)
                telemetry.EquipChanges++;
            telemetry.EquippedColor = equippedColor;
            telemetry.EquippedItemId = equippedItemId;
            if (telemetry.Active && (!active || telemetry.ActiveColor != color))
            {
                double elapsed = Math.Max(0d, (now - telemetry.ActiveSince).TotalSeconds);
                telemetry.ActiveSeconds += elapsed;
                telemetry.ActiveSecondsByColor.TryGetValue(telemetry.ActiveColor, out double previousColorSeconds);
                telemetry.ActiveSecondsByColor[telemetry.ActiveColor] = previousColorSeconds + elapsed;
            }
            if (active && (!telemetry.Active || telemetry.ActiveColor != color))
                telemetry.ActiveSince = now;
            telemetry.Active = active;
            telemetry.ActiveColor = active ? color : OrbColor.None;

            bool hasMergeCandidate = HasMergeCandidate(itemIds);
            bool mergeCandidateBecameAvailable = !telemetry.HasMergeCandidate && hasMergeCandidate;
            double? endedMergeCandidateDurationSeconds = null;
            if (mergeCandidateBecameAvailable)
                telemetry.MergeCandidateSince = now;
            else if (telemetry.HasMergeCandidate && !hasMergeCandidate)
                endedMergeCandidateDurationSeconds = Math.Max(0d, (now - telemetry.MergeCandidateSince).TotalSeconds);
            telemetry.HasMergeCandidate = hasMergeCandidate;
            int previousBoardItemCount = CountBoardOrbs(previousBoard);
            int boardItemCount = CountBoardOrbs(itemIds);
            bool firstOrbPickup = previousBoardItemCount == 0 && boardItemCount > 0 && !telemetry.FirstOrbPickupLogged;
            if (firstOrbPickup)
                telemetry.FirstOrbPickupLogged = true;
            bool boardReachedCapacity = previousBoardItemCount < Config.LEGACY_INVENTORY_SLOT_COUNT &&
                                        boardItemCount >= Config.LEGACY_INVENTORY_SLOT_COUNT &&
                                        !telemetry.BoardFullLogged;
            if (boardReachedCapacity)
                telemetry.BoardFullLogged = true;
            telemetry.BoardItemIds = itemIds.ToList();

            long? matchElapsedMilliseconds = state.MatchStartedAtUtc.HasValue
                ? Math.Max(0, (long)(now - state.MatchStartedAtUtc.Value).TotalMilliseconds)
                : null;

            return new OrbTransitionSnapshot(
                previousBoard,
                previousEquippedItemId,
                previousEquippedColor,
                previousActive,
                previousActiveColor,
                equippedColor,
                mergeCandidateBecameAvailable,
                endedMergeCandidateDurationSeconds,
                firstOrbPickup,
                boardReachedCapacity,
                boardItemCount,
                matchElapsedMilliseconds);
        }
    }

    private void LogOrbTelemetrySummaries(long matchingId, IReadOnlyCollection<MatchFinalPlayerStats> players)
    {
        if (!_telemetryStates.TryGetValue(matchingId, out var state)) return;
        var now = DateTimeOffset.UtcNow;
        lock (state.SyncRoot)
        {
            if (state.OrbSummariesLogged)
                return;
            state.OrbSummariesLogged = true;
            foreach (var player in players)
            {
                if (!state.OrbTelemetry.TryGetValue(player.PlayerId, out var telemetry)) continue;
                var secondsByColor = telemetry.ActiveSecondsByColor.ToDictionary(pair => pair.Key, pair => pair.Value);
                double active = telemetry.ActiveSeconds;
                if (telemetry.Active)
                {
                    double currentElapsed = Math.Max(0d, (now - telemetry.ActiveSince).TotalSeconds);
                    active += currentElapsed;
                    secondsByColor.TryGetValue(telemetry.ActiveColor, out double colorSeconds);
                    secondsByColor[telemetry.ActiveColor] = colorSeconds + currentElapsed;
                }
                double rate = player.SurvivalTimeSeconds <= 0 ? 0d : active / player.SurvivalTimeSeconds;
                Append(matchingId, "SURVIVOR_ORB_SUMMARY", player.PlayerId, BotPlayerManager.IsBotPlayerId(player.PlayerId),
                    $"Orb summary: active={active:F1}s, rate={rate:P0}, colorChanges={telemetry.EquipChanges}.", entry =>
                    { entry.DurationSeconds = active; entry.ScoreDelta = (float)rate; entry.ContributionDelta = telemetry.EquipChanges; });

                foreach (var color in new[] { OrbColor.Red, OrbColor.Green, OrbColor.Blue })
                {
                    secondsByColor.TryGetValue(color, out double colorActive);
                    double colorRate = player.SurvivalTimeSeconds <= 0 ? 0d : colorActive / player.SurvivalTimeSeconds;
                    Append(matchingId, "SURVIVOR_ORB_COLOR_SUMMARY", player.PlayerId,
                        BotPlayerManager.IsBotPlayerId(player.PlayerId),
                        $"Orb color summary: color={color}, active={colorActive:F1}s, rate={colorRate:P0}.", entry =>
                        {
                            entry.ResonanceColor = color.ToString();
                            entry.ResonanceProfile = GetResonanceProfile(color);
                            entry.DurationSeconds = colorActive;
                            entry.ScoreDelta = (float)colorRate;
                        });
                }
            }
        }
    }

    private static bool HasMergeCandidate(IEnumerable<int> itemIds) =>
        itemIds.GroupBy(itemId => itemId).Any(group => group.Count() >= 2 && OrbData.CanMerge(group.Key, group.Key));

    private static int CountBoardOrbs(IEnumerable<int> itemIds) =>
        itemIds.Count(itemId => OrbData.IsOrbItem(itemId) || OrbData.IsRecoveryOrb(itemId));

    private static string GetResonanceProfile(OrbColor color) => color switch
    {
        OrbColor.Red => "sun_single_target",
        OrbColor.Green => "wind_multi_target",
        OrbColor.Blue => "wave_burst",
        _ => "none"
    };
    public void LogOvertimeStageChanged(long matchingId, int stage, int corruptionPerSecond)
    {
        if (stage <= 0 || corruptionPerSecond <= 0) return;
        var state = _telemetryStates.GetOrAdd(matchingId, _ => new MatchTelemetryState());
        lock (state.SyncRoot)
        {
            if (state.OvertimeStage >= stage) return;
            state.OvertimeStage = stage;
        }

        Append(matchingId, "OVERTIME_STAGE_CHANGED", 0, false,
            $"Overtime stage {stage}: corruption={corruptionPerSecond}/s.", entry =>
            {
                entry.OvertimeStage = stage;
                entry.CorruptionPerSecond = corruptionPerSecond;
            });
    }

    public void LogMatchEnded(long matchingId, long winnerPlayerId, string endReason, string tieBreakCriterion,
        IReadOnlyCollection<MatchFinalPlayerStats> players)
    {
        Append(matchingId, "MATCH_ENDED", winnerPlayerId, BotPlayerManager.IsBotPlayerId(winnerPlayerId),
            $"Match ended: winner={winnerPlayerId}, reason={endReason}, tieBreak={tieBreakCriterion}.", entry =>
            {
                entry.WinnerPlayerId = winnerPlayerId;
                entry.EndReason = endReason;
                entry.TieBreakCriterion = tieBreakCriterion;
                entry.FinalPlayerStats = players.ToList();
                entry.CompletedAtUnixMs = entry.TimestampUnixMs;
            });
        LogOrbTelemetrySummaries(matchingId, players);
    }

    public void LogMatchAbandoned(long matchingId, string endReason,
        IReadOnlyCollection<MatchFinalPlayerStats>? players = null)
    {
        Append(matchingId, "MATCH_ABANDONED", 0, false,
            $"Match abandoned: reason={endReason}.", entry =>
            {
                entry.EndReason = endReason;
                entry.FinalPlayerStats = players?.ToList();
                entry.CompletedAtUnixMs = entry.TimestampUnixMs;
            });
        if (players is { Count: > 0 })
            LogOrbTelemetrySummaries(matchingId, players);
    }

    public bool TryBeginFinalization(long matchingId) =>
        matchingId > 0 && _finalizedMatchings.TryAdd(matchingId, 0);

    public List<GameEventEntry> GetRecent(long matchingId, int limit = MaxEventsPerMatching, long? sinceSeq = null)
    {
        if (!_logs.TryGetValue(matchingId, out var log) && !_archivedLogs.TryGetValue(matchingId, out log))
            return new List<GameEventEntry>();
        return log.Snapshot(limit, sinceSeq);
    }

    public List<GameEventEntry> GetForPersistence(long matchingId)
    {
        if (!_logs.TryGetValue(matchingId, out var log) && !_archivedLogs.TryGetValue(matchingId, out log))
            return new List<GameEventEntry>();
        return log.FullSnapshot();
    }

    public void Clear(long matchingId)
    {
        if (_logs.TryRemove(matchingId, out var log))
        {
            log.CompactForArchive();
            _archivedLogs[matchingId] = log;
            lock (_archiveLock)
            {
                _archivedMatchingIds.Enqueue(matchingId);
                while (_archivedMatchingIds.Count > MaxArchivedMatchings)
                {
                    long removedMatchingId = _archivedMatchingIds.Dequeue();
                    _archivedLogs.TryRemove(removedMatchingId, out _);
                    _finalizedMatchings.TryRemove(removedMatchingId, out _);
                }
            }
        }
        _matchCombatStates.TryRemove(matchingId, out _);
        _telemetryStates.TryRemove(matchingId, out _);
    }

    private void LogExploreFinished(long matchingId, long playerId, int interactId, string area, string type,
        string outcome, IReadOnlyCollection<int> generatedItemIds, bool isBot)
    {
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? startedAt = null;
        var state = _telemetryStates.GetOrAdd(matchingId, _ => new MatchTelemetryState());
        lock (state.SyncRoot)
            if (state.ExploreStarts.Remove((playerId, interactId), out var value)) startedAt = value;

        AppendAt(matchingId, type, playerId, isBot,
            $"Explore {outcome}: interact={interactId}, area={area}, items={string.Join(',', generatedItemIds)}.",
            now, entry =>
            {
                entry.ActivityId = interactId;
                entry.Area = area;
                entry.StartedAtUnixMs = startedAt?.ToUnixTimeMilliseconds();
                entry.CompletedAtUnixMs = entry.TimestampUnixMs;
                entry.ElapsedMilliseconds = startedAt.HasValue
                    ? Math.Max(0, (long)(now - startedAt.Value).TotalMilliseconds)
                    : null;
                entry.GeneratedItemIds = generatedItemIds.ToList();
                entry.Outcome = outcome;
            });
    }

    private void LogClosureMovement(long matchingId, long playerId, string fromArea, string toArea, bool isBot,
        DateTimeOffset now)
    {
        if (!_telemetryStates.TryGetValue(matchingId, out var state)) return;
        var derived = new List<(string Type, ClosureWarningResponse Warning)>();
        lock (state.SyncRoot)
        {
            foreach (var warning in state.ClosureWarnings.Values.Where(w => w.PlayerId == playerId))
            {
                if (string.Equals(fromArea, warning.Area, StringComparison.Ordinal) &&
                    !string.Equals(toArea, warning.Area, StringComparison.Ordinal) && !warning.ExitedAt.HasValue)
                {
                    warning.ExitedAt = now;
                    derived.Add(("CLOSURE_WARNING_EXIT", warning));
                }
                else if (string.Equals(toArea, warning.Area, StringComparison.Ordinal) &&
                         warning.ExitedAt.HasValue && !warning.ReenteredAt.HasValue)
                {
                    warning.ReenteredAt = now;
                    derived.Add(("CLOSURE_WARNING_REENTRY", warning));
                }
            }
        }

        foreach (var (type, warning) in derived)
            AppendAt(matchingId, type, playerId, isBot,
                $"Closure response: area={warning.Area}, explores={warning.AdditionalExploreCount}.", now, entry =>
                {
                    entry.Area = warning.Area;
                    entry.AdditionalExploreCount = warning.AdditionalExploreCount;
                    entry.ElapsedMilliseconds = Math.Max(0, (long)(now - warning.WarnedAt).TotalMilliseconds);
                    entry.ExitedAtUnixMs = warning.ExitedAt?.ToUnixTimeMilliseconds();
                    entry.ReenteredAtUnixMs = warning.ReenteredAt?.ToUnixTimeMilliseconds();
                });
    }

    private GameEventEntry Append(long matchingId, string type, long playerId, bool isBot, string description,
        Action<GameEventEntry>? configure = null)
    {
        return AppendAt(matchingId, type, playerId, isBot, description, DateTimeOffset.UtcNow, configure);
    }

    private GameEventEntry AppendAt(long matchingId, string type, long playerId, bool isBot, string description,
        DateTimeOffset timestamp, Action<GameEventEntry>? configure = null)
    {
        var entry = CreateEntry(type, playerId, isBot, description, timestamp, configure);
        var log = _logs.GetOrAdd(matchingId, _ => new MatchingEventLog());
        log.Add(entry);
        return entry;
    }

    private GameEventEntry CreateEntry(string type, long playerId, bool isBot, string description,
        DateTimeOffset timestamp, Action<GameEventEntry>? configure = null)
    {
        var entry = new GameEventEntry
        {
            Seq = Interlocked.Increment(ref _nextSeq),
            TimestampUnixMs = timestamp.ToUnixTimeMilliseconds(),
            Type = type,
            PlayerId = playerId,
            ActorPlayerId = playerId,
            IsBot = isBot,
            Description = description
        };

        configure?.Invoke(entry);
        return entry;
    }

    private void LogFirstElimination(
        long matchingId,
        long playerId,
        string reason,
        bool isBot,
        DateTimeOffset occurredAt)
    {
        var state = _matchCombatStates.GetOrAdd(matchingId, _ => new MatchCombatState());
        bool isFirstElimination;
        lock (state.SyncRoot)
        {
            state.KnownPlayerIds.Add(playerId);
            isFirstElimination = !state.FirstEliminationAtUnixMs.HasValue;
            if (isFirstElimination)
                state.FirstEliminationAtUnixMs = occurredAt.ToUnixTimeMilliseconds();

            var endingAttackers = state.EngagementsByAttacker
                .Where(pair => pair.Key == playerId || pair.Value.TargetPlayerId == playerId)
                .Select(pair => pair.Key)
                .ToList();
            foreach (long endingAttacker in endingAttackers)
                state.EngagementsByAttacker.Remove(endingAttacker);
        }

        if (!isFirstElimination)
            return;

        AppendAt(
            matchingId,
            "SURVIVOR_FIRST_ELIMINATION",
            playerId,
            isBot,
            $"First elimination: {FormatPlayer(playerId)}, reason={reason}.",
            occurredAt,
            entry =>
            {
                entry.IsFirstMilestone = true;
                entry.Outcome = reason;
                entry.OccurredAtUnixMs = entry.TimestampUnixMs;
            });
    }


    public void RecordRecovery(long matchingId, long playerId, int amount)
    {
        if (playerId == 0 || amount <= 0)
            return;

        var state = _matchCombatStates.GetOrAdd(matchingId, _ => new MatchCombatState());
        lock (state.SyncRoot)
        {
            state.KnownPlayerIds.Add(playerId);
            state.RecoveryByPlayer.TryGetValue(playerId, out int previousRecovery);
            state.RecoveryByPlayer[playerId] = previousRecovery + amount;
        }
    }

    /// <summary>
    ///     몹 피해·처치 누적 (#229). 스웜의 플레이어 전투는 전부 몹 상대인데 어떤 카운터에도
    ///     쌓이지 않아 결과 화면이 531킬을 "처치 0회"로 표시했다.
    ///     ProcessPendingSwarmMonsterHits 한 곳이 유일한 진입점이라 여기서만 부른다.
    /// </summary>
    public void RecordMonsterHit(long matchingId, long playerId, int damage, bool killed)
    {
        if (playerId == 0)
            return;

        var state = _matchCombatStates.GetOrAdd(matchingId, _ => new MatchCombatState());
        lock (state.SyncRoot)
        {
            state.KnownPlayerIds.Add(playerId);
            if (damage > 0)
            {
                state.MonsterDamageByPlayer.TryGetValue(playerId, out int previousDamage);
                state.MonsterDamageByPlayer[playerId] = previousDamage + damage;
            }

            if (!killed) return;

            state.MonsterKillsByPlayer.TryGetValue(playerId, out int previousKills);
            state.MonsterKillsByPlayer[playerId] = previousKills + 1;
        }
    }

    public ResultStats GetResultStats(long matchingId, long playerId)
    {
        if (!_matchCombatStates.TryGetValue(matchingId, out var state))
            return default;

        lock (state.SyncRoot)
        {
            state.KillCountsByPlayer.TryGetValue(playerId, out int kills);
            state.DamageDealtByPlayer.TryGetValue(playerId, out int damage);
            state.RecoveryByPlayer.TryGetValue(playerId, out int recovery);
            state.MonsterKillsByPlayer.TryGetValue(playerId, out int monsterKills);
            state.MonsterDamageByPlayer.TryGetValue(playerId, out int monsterDamage);
            return new ResultStats(kills, damage, recovery, monsterKills, monsterDamage);
        }
    }

    private sealed class MatchCombatState
    {
        public object SyncRoot { get; } = new();
        public long? FirstEncounterAtUnixMs { get; set; }
        public long? FirstTier2AtUnixMs { get; set; }
        public long? FirstTier3AtUnixMs { get; set; }
        public long? FirstEliminationAtUnixMs { get; set; }
        public HashSet<long> KnownPlayerIds { get; } = new();
        public Dictionary<long, int> KillCountsByPlayer { get; } = new();
        public Dictionary<long, int> DamageDealtByPlayer { get; } = new();

        // 몹 처치·피해는 PvP와 따로 센다 (#229). DamageDealtByPlayer는 동시 탈락 시
        // 생존자를 가르는 기준(MatchSettlementResolver)이라 의미를 섞으면 판정이 바뀐다.
        public Dictionary<long, int> MonsterKillsByPlayer { get; } = new();
        public Dictionary<long, int> MonsterDamageByPlayer { get; } = new();
        public Dictionary<long, int> RecoveryByPlayer { get; } = new();
        public Dictionary<long, MatchCombatEngagement> EngagementsByAttacker { get; } = new();
    }

    private sealed class MatchTelemetryState
    {
        public object SyncRoot { get; } = new();
        public bool MatchStarted { get; set; }
        public DateTimeOffset? MatchStartedAtUtc { get; set; }
        public int OvertimeStage { get; set; }
        public HashSet<long> SpawnLoggedPlayerIds { get; } = new();
        public HashSet<long> FirstSummonStoneLoggedPlayerIds { get; } = new();
        public HashSet<long> FirstSuccessfulSummonLoggedPlayerIds { get; } = new();
        public Dictionary<(long PlayerId, int InteractId), DateTimeOffset> ExploreStarts { get; } = new();
        public Dictionary<(long PlayerId, string Area, long ClosureAtUnixMs), ClosureWarningResponse>
            ClosureWarnings
        { get; } = new();
        public List<PendingEliminationDrop> PendingEliminationDrops { get; } = new();
        public Dictionary<long, OrbTelemetry> OrbTelemetry { get; } = new();
        public bool OrbSummariesLogged { get; set; }
    }

    private void TrackEliminationDropPickup(long matchingId, long groundItemUid, long pickerPlayerId)
    {
        if (!_telemetryStates.TryGetValue(matchingId, out var state))
            return;

        lock (state.SyncRoot)
        {
            foreach (var drop in state.PendingEliminationDrops)
            {
                if (drop.Items.All(item => item.GroundItemUid != groundItemUid) ||
                    drop.PickupOrder.Any(pickup => pickup.GroundItemUid == groundItemUid))
                    continue;
                drop.PickupOrder.Add(new EliminationDropPickup(groundItemUid, pickerPlayerId));
                return;
            }
        }
    }

    private sealed class PendingEliminationDrop(
        long playerId, string area, bool isBot, DateTimeOffset observeAtUtc, List<EliminationDroppedItem> items)
    {
        public long PlayerId { get; } = playerId;
        public string Area { get; } = area;
        public bool IsBot { get; } = isBot;
        public DateTimeOffset ObserveAtUtc { get; } = observeAtUtc;
        public List<EliminationDroppedItem> Items { get; } = items;
        public List<EliminationDropPickup> PickupOrder { get; } = new();
    }

    private sealed class OrbTelemetry
    {
        public bool Active;
        public OrbColor ActiveColor;
        public DateTimeOffset ActiveSince;
        public double ActiveSeconds;
        public Dictionary<OrbColor, double> ActiveSecondsByColor { get; } = new();
        public OrbColor EquippedColor;
        public int EquippedItemId;
        public int EquipChanges;
        public List<int> BoardItemIds { get; set; } = new();
        public bool HasMergeCandidate;
        public DateTimeOffset MergeCandidateSince;
        public bool FirstOrbPickupLogged;
        public bool BoardFullLogged;
    }

    private readonly record struct OrbTransitionSnapshot(
        List<int> PreviousBoardItemIds,
        int PreviousEquippedItemId,
        OrbColor PreviousEquippedColor,
        bool PreviousResonanceActive,
        OrbColor PreviousResonanceColor,
        OrbColor EquippedColor,
        bool MergeCandidateBecameAvailable,
        double? EndedMergeCandidateDurationSeconds,
        bool FirstOrbPickup,
        bool BoardReachedCapacity,
        int BoardItemCount,
        long? MatchElapsedMilliseconds);
    private sealed class ClosureWarningResponse(long playerId, string area, DateTimeOffset warnedAt)
    {
        public long PlayerId { get; } = playerId;
        public string Area { get; } = area;
        public DateTimeOffset WarnedAt { get; } = warnedAt;
        public DateTimeOffset? ExitedAt { get; set; }
        public DateTimeOffset? ReenteredAt { get; set; }
        public int AdditionalExploreCount { get; set; }
    }

    public readonly record struct ResultStats(
        int KillCount,
        int TotalDamageDealt,
        int TotalRecovery,
        int MonsterKillCount = 0,
        int MonsterDamageDealt = 0);

    private sealed class MatchCombatEngagement
    {
        public MatchCombatEngagement(
            long targetPlayerId,
            string area,
            int weaponItemId,
            int weaponTier,
            int targetWeaponTier,
            DateTimeOffset startedAt)
        {
            TargetPlayerId = targetPlayerId;
            Area = area;
            WeaponItemId = weaponItemId;
            WeaponTier = weaponTier;
            TargetWeaponTier = targetWeaponTier;
            StartedAt = startedAt;
        }

        public long TargetPlayerId { get; }
        public string Area { get; }
        public int WeaponItemId { get; }
        public int WeaponTier { get; }
        public int TargetWeaponTier { get; }
        public DateTimeOffset StartedAt { get; }
        public DateTimeOffset? LastHitAt { get; set; }
        public int HitCount { get; set; }
    }

    private static string FormatPlayer(long playerId) => $"Player{playerId}";

    private sealed class MatchingEventLog
    {
        private readonly LinkedList<GameEventEntry> _entries = new();
        private readonly List<GameEventEntry> _fullEntries = new();
        private readonly Dictionary<long, AreaPresenceState> _playerAreas = new();
        private readonly object _lock = new();

        public void SetPlayerArea(long playerId, string area, DateTimeOffset timestamp)
        {
            lock (_lock)
            {
                if (!IsTrackableArea(area))
                {
                    _playerAreas.Remove(playerId);
                    return;
                }

                if (_playerAreas.TryGetValue(playerId, out var existing) &&
                    string.Equals(existing.Area, area, StringComparison.Ordinal))
                    return;

                _playerAreas[playerId] = new AreaPresenceState(area, timestamp);
            }
        }

        public void AddMoveAndDerived(
            long playerId,
            string fromArea,
            string toArea,
            bool isBot,
            DateTimeOffset timestamp,
            Func<string, long, bool, string, DateTimeOffset, Action<GameEventEntry>?, GameEventEntry> createEntry)
        {
            lock (_lock)
            {
                var rawMove = createEntry(
                    "MOVE",
                    playerId,
                    isBot,
                    $"{fromArea} -> {toArea}",
                    timestamp,
                    entry =>
                    {
                        entry.FromArea = fromArea;
                        entry.ToArea = toArea;
                    });
                AddNoLock(rawMove);

                if (!IsTrackableArea(toArea))
                {
                    _playerAreas.Remove(playerId);
                    return;
                }

                var alreadyPresent = _playerAreas
                    .Where(pair => pair.Key != playerId && string.Equals(pair.Value.Area, toArea, StringComparison.Ordinal))
                    .OrderBy(pair => pair.Value.EnteredAt)
                    .ToList();
                var alreadyPresentIds = alreadyPresent.Select(pair => pair.Key).ToList();

                var areaEnter = createEntry(
                    "AREA_ENTER",
                    playerId,
                    isBot,
                    BuildAreaEnterDescription(playerId, fromArea, toArea, alreadyPresentIds),
                    timestamp,
                    entry =>
                    {
                        entry.FromArea = fromArea;
                        entry.ToArea = toArea;
                        entry.Area = toArea;
                        entry.EnteredAtUnixMs = timestamp.ToUnixTimeMilliseconds();
                        entry.AlreadyPresentPlayerIds = alreadyPresentIds;
                        entry.SourceEventSeq = rawMove.Seq;
                    });
                AddNoLock(areaEnter);

                _playerAreas[playerId] = new AreaPresenceState(toArea, timestamp);
            }
        }

        public void Add(GameEventEntry entry)
        {
            lock (_lock)
            {
                AddNoLock(entry);
            }
        }

        public List<GameEventEntry> Snapshot(int limit, long? sinceSeq)
        {
            lock (_lock)
            {
                IEnumerable<GameEventEntry> q = _entries;
                if (sinceSeq.HasValue) q = q.Where(e => e.Seq > sinceSeq.Value);
                return q.Reverse().Take(limit).ToList();
            }
        }

        public List<GameEventEntry> FullSnapshot()
        {
            lock (_lock)
                return _fullEntries.ToList();
        }

        public void CompactForArchive()
        {
            lock (_lock)
            {
                _fullEntries.Clear();
                _fullEntries.AddRange(_entries);
            }
        }

        private void AddNoLock(GameEventEntry entry)
        {
            _entries.AddLast(entry);
            _fullEntries.Add(entry);
            while (_entries.Count > MaxEventsPerMatching) _entries.RemoveFirst();
        }

        private static bool IsTrackableArea(string area) =>
            !string.IsNullOrWhiteSpace(area) &&
            !string.Equals(area, "None", StringComparison.OrdinalIgnoreCase);

        private static string BuildAreaEnterDescription(long playerId, string fromArea, string toArea,
            IReadOnlyCollection<long> alreadyPresentIds)
        {
            var description = $"{FormatPlayer(playerId)} entered {toArea} from {fromArea}.";
            return alreadyPresentIds.Count == 0
                ? description
                : $"{description} Already present: {FormatPlayers(alreadyPresentIds)}.";
        }

        private static string FormatPlayers(IEnumerable<long> playerIds) =>
            string.Join(", ", playerIds.Select(FormatPlayer));

        private static string FormatPlayer(long playerId) => $"Player{playerId}";
    }

    private readonly record struct AreaPresenceState(string Area, DateTimeOffset EnteredAt);
}

public class GameEventEntry
{
    public long Seq { get; set; }
    public long TimestampUnixMs { get; set; }
    public string Type { get; set; } = "";
    public long PlayerId { get; set; }
    public long ActorPlayerId { get; set; }
    public bool IsBot { get; set; }
    public string Description { get; set; } = "";

    public string? FromArea { get; set; }
    public string? ToArea { get; set; }
    public string? Area { get; set; }
    public long? EnteredAtUnixMs { get; set; }
    public long? ExitedAtUnixMs { get; set; }
    public long? OccurredAtUnixMs { get; set; }
    public double? DurationSeconds { get; set; }
    public List<long>? AlreadyPresentPlayerIds { get; set; }
    public long? SourceEventSeq { get; set; }

    public int? TaskId { get; set; }
    public int? ActivityId { get; set; }
    public long? StartedAtUnixMs { get; set; }
    public long? CompletedAtUnixMs { get; set; }
    public float? ScoreDelta { get; set; }
    public int? ContributionDelta { get; set; }
    public string? ActivityReason { get; set; }

    public long? TargetPlayerId { get; set; }
    public int? WeaponItemId { get; set; }
    public int? WeaponTier { get; set; }
    public int? TargetWeaponTier { get; set; }
    public int? MonsterId { get; set; }
    public int? PhaseIndex { get; set; }
    public int? ReinforcementReleasedCount { get; set; }
    public int? ReinforcementRemainingBudget { get; set; }
    public int? AliveMonsterCount { get; set; }
    public int? GlobalAliveMonsterCount { get; set; }
    public bool? HasAttackableMonster { get; set; }
    public double? BotMovementTickP50Milliseconds { get; set; }
    public double? BotMovementTickP95Milliseconds { get; set; }
    public double? BotMovementTickP99Milliseconds { get; set; }
    public double? BotMovementSnapshotP95Milliseconds { get; set; }
    public double? BotMovementPlanningP95Milliseconds { get; set; }
    public double? BotMovementWalkingP95Milliseconds { get; set; }
    public double? BotMovementBroadcastP95Milliseconds { get; set; }
    public int? BotMovementTickSampleCount { get; set; }
    public int? BotMovementTickSkipCount { get; set; }
    public int? BotMovementMaxConsecutiveSkipCount { get; set; }
    public int? CoreCurrentHealth { get; set; }
    public int? CoreMaxHealth { get; set; }
    public string? DamageSourceType { get; set; }
    public long? FirstAttackerPlayerId { get; set; }
    public long? LastAttackerPlayerId { get; set; }
    public List<MonsterDamageContribution>? MonsterDamageContributions { get; set; }
    public bool? IsAreaClosureElimination { get; set; }
    public bool? IsOvertimeElimination { get; set; }
    public int? CorruptionBefore { get; set; }
    public int? CorruptionAfter { get; set; }
    public int? Damage { get; set; }
    public long? ElapsedMilliseconds { get; set; }
    public long? PreviousHitGapMilliseconds { get; set; }
    public int? HitCount { get; set; }
    public int? KillCount { get; set; }
    public bool? Escaped { get; set; }
    public bool? IsFirstMilestone { get; set; }
    public string? Outcome { get; set; }

    public int? MatchSeed { get; set; }
    public int? SpawnAnchorIndex { get; set; }
    public int? CellX { get; set; }
    public int? CellY { get; set; }
    public long? GroundItemUid { get; set; }
    public int? ItemId { get; set; }
    public int? SummonStoneDelta { get; set; }
    public int? SummonStoneBalance { get; set; }
    public int? NextSummonCost { get; set; }
    public int? SuccessfulSummonCount { get; set; }
    public List<int>? GeneratedItemIds { get; set; }
    public List<int>? BoardItemIds { get; set; }
    public List<int>? PreviousBoardItemIds { get; set; }
    public int? PreviousEquippedItemId { get; set; }
    public string? EquippedColor { get; set; }
    public string? PreviousEquippedColor { get; set; }
    public bool? ResonanceActive { get; set; }
    public bool? PreviousResonanceActive { get; set; }
    public string? ResonanceColor { get; set; }
    public string? PreviousResonanceColor { get; set; }
    public string? ResonanceProfile { get; set; }
    public double? MergeCandidateDurationSeconds { get; set; }
    public List<long>? AttackTargetPlayerIds { get; set; }
    public int? CandidateTargetCount { get; set; }
    public int? ValidTargetCount { get; set; }
    public int? HitTargetCount { get; set; }
    public long? ProjectileId { get; set; }
    public double? ProjectileTravelSeconds { get; set; }
    public float? TargetDisplacement { get; set; }
    public float? ProjectileHitRadius { get; set; }
    public int? RequestedRecoveryAmount { get; set; }
    public int? WastedRecoveryAmount { get; set; }
    public long? DiscovererPlayerId { get; set; }
    public long? PickerPlayerId { get; set; }
    public long? PriorityExpiresAtUnixMs { get; set; }
    public bool? PriorityExpired { get; set; }
    public bool? AutoUsed { get; set; }
    public List<string>? WarningAreas { get; set; }
    public List<string>? OpenAreas { get; set; }
    public int? Corruption { get; set; }
    public int? InventorySlotsUsed { get; set; }
    public int? InventorySlotCapacity { get; set; }
    public long? ClosureAtUnixMs { get; set; }
    public int? AdditionalExploreCount { get; set; }
    public long? ReenteredAtUnixMs { get; set; }
    public int? RecoveryAmount { get; set; }
    public int? DropRecoveryTotal { get; set; }
    public float? DropScatterRadius { get; set; }
    public List<EliminationDroppedItem>? EliminationDroppedItems { get; set; }
    public List<EliminationDropPickup>? DropPickupOrder { get; set; }
    public List<long>? UncollectedDroppedItemUids { get; set; }
    public int? OvertimeStage { get; set; }
    public int? CorruptionPerSecond { get; set; }
    public long? WinnerPlayerId { get; set; }
    public string? EndReason { get; set; }
    public string? TieBreakCriterion { get; set; }
    public List<MatchFinalPlayerStats>? FinalPlayerStats { get; set; }

    // #226 F 계측 — 절단·크랙·성장 카드 전용 필드
    public int? TailOrdinal { get; set; }
    public int? DestroyedOrbCount { get; set; }
    public int? CrackCount { get; set; }
    public int? RequiredHits { get; set; }

    // #227 3·6단계 — 절단 진입 시점의 판단 재료
    // 화망 밀도(그 자리를 덮는 적 오브 사거리 수) + 맞기 직전 내구 + 끊었을 때의 손실
    public int? OverlappingOrbRanges { get; set; }
    public int? VictimOrbRanges { get; set; }
    public int? DurabilityBeforeHit { get; set; }
    public int? ExpectedOrbLoss { get; set; }

    // #227 7단계 — 절단자 한정 반격 보호 창의 결산
    public int? BlockedDamage { get; set; }
    public int? BlockedHits { get; set; }
    public int? BlockedCuts { get; set; }
    public bool? Retaliated { get; set; }
    public bool? BothDisengaged { get; set; }
    public string? CardRole { get; set; }
    public int? CardGrade { get; set; }
    public int? GrowthBaseCost { get; set; }
    public int? GrowthScoreSurcharge { get; set; }
    public int? GrowthFinalCost { get; set; }
    public int? GrowthSuccessCountBefore { get; set; }
    public int? OrbCountBefore { get; set; }

    // #227 5단계 — 절단 전후 대차대조 (오브 수 = 점수)
    public int? OrbCountAfter { get; set; }
    public int? AttackOrbCountBefore { get; set; }
    public int? AttackOrbCountAfter { get; set; }
    public int? RankAfter { get; set; }

    public long? StatementId { get; set; }
    public int? RoundId { get; set; }
    public long? SpeakerPlayerId { get; set; }
    public long? ListenerPlayerId { get; set; }
    public string? AreaId { get; set; }
    public string? QuestionId { get; set; }
    public string? QuestionText { get; set; }
    public string? AnswerType { get; set; }
    public string? AnswerText { get; set; }
    public List<long>? LinkedLogIds { get; set; }
    public long? SaidAtUnixMs { get; set; }
}

public sealed record MonsterDamageContribution(long PlayerId, int Damage);
public sealed record MatchFinalPlayerStats(
    long PlayerId,
    int Rank,
    int SurvivalTimeSeconds,
    int KillCount,
    int TotalDamageDealt,
    int TotalRecovery,
    // 승점 (#229): 결과 화면과 같은 오브 수. 이게 없으면 매치 로그만 보고는
    // 누가 왜 이겼는지 되짚을 수 없다 — 나머지 세 지표는 스웜에서 상시 0이다.
    int OrbCount = 0);

public sealed record EliminationDroppedItem(
    long GroundItemUid,
    int ItemId,
    string? OrbColor,
    int? OrbTier,
    float PositionX,
    float PositionY,
    float DistanceFromOrigin)
{
    public static EliminationDroppedItem FromGroundItem(GroundItemInfo item)
    {
        bool isOrb = OrbData.TryGetColorAndTier(item.ItemId, out OrbColor color, out int tier);
        float dx = item.PositionX - item.SpawnOriginX;
        float dy = item.PositionY - item.SpawnOriginY;
        return new EliminationDroppedItem(
            item.GroundItemUid, item.ItemId, isOrb ? color.ToString() : null, isOrb ? tier : null,
            item.PositionX, item.PositionY, MathF.Sqrt(dx * dx + dy * dy));
    }
}

public sealed record EliminationDropPickup(long GroundItemUid, long PickerPlayerId);
