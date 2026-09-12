using System.Collections.Concurrent;
using System.Globalization;
using game_server.matches;
using game_server.matches.combat;
using game_server.players;
using game_server.players.bots;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches.logging;

/// <summary>
///     이벤트 기록과 전투·계측 통계를 계산한다. 진행 중 상태는 MatchRuntime.EventLog에 쓰며,
///     이 객체는 프로세스 공용 이벤트 순번을 관리한다. 종료 기록은 별도 파일 저장 경로에서 남긴다.
///     기록 변경과 해제는 호출자가 해당 매치 잠금 안에서 실행한다.
/// </summary>
public class GameEventLogManager
{
    private const int MaxEventsPerMatching = 5_000;


    private readonly Func<long, MatchEventLogState?> _getMatchState;
    private long _nextSeq;

    internal GameEventLogManager(Func<long, MatchEventLogState?> getMatchState)
    {
        _getMatchState = getMatchState ?? throw new ArgumentNullException(nameof(getMatchState));
    }

    private MatchEventLogState GetActiveState(long matchingId)
    {
        var state = _getMatchState(matchingId);
        if (state == null || state.IsReleased)
            throw new InvalidOperationException($"Match event state is not active: {matchingId}");
        return state;
    }

    private MatchingEventLog GetLog(long matchingId) =>
        LazyInitializer.EnsureInitialized(ref GetActiveState(matchingId).Log, static () => new MatchingEventLog());

    private MatchCombatState GetCombat(long matchingId) =>
        LazyInitializer.EnsureInitialized(ref GetActiveState(matchingId).Combat, static () => new MatchCombatState());

    private MatchTelemetryState GetTelemetry(long matchingId) =>
        LazyInitializer.EnsureInitialized(ref GetActiveState(matchingId).Telemetry, static () => new MatchTelemetryState());

    private bool TryGetTelemetry(long matchingId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MatchTelemetryState? state)
    {
        state = _getMatchState(matchingId)?.Telemetry;
        return state != null;
    }

    private bool TryGetCombat(long matchingId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MatchCombatState? state)
    {
        state = _getMatchState(matchingId)?.Combat;
        return state != null;
    }

    public void SetPlayerArea(long matchingId, long playerId, string area)
    {
        var log = GetLog(matchingId);
        log.SetPlayerArea(playerId, area, DateTimeOffset.UtcNow);
    }

    public void LogMove(long matchingId, long playerId, string fromArea, string toArea, bool isBot)
    {
        var log = GetLog(matchingId);
        var now = DateTimeOffset.UtcNow;

        log.AddMoveAndDerived(
            playerId,
            fromArea,
            toArea,
            isBot,
            now,
            CreateEntry);
    }

    public void LogResource(long matchingId, long playerId, int healthDelta,
        int health, string reason, bool isBot)
    {
        var parts = new List<string>();
        if (healthDelta != 0) parts.Add($"체력{(healthDelta >= 0 ? "+" : "")}{healthDelta}");
        parts.Add($"(체력 {health})");
        if (!string.IsNullOrEmpty(reason)) parts.Add($"<{reason}>");
        Append(matchingId, GameEventType.Resource, playerId, isBot, string.Join(" ", parts));
    }

    public void LogMission(long matchingId, long playerId, string description, bool isBot)
    {
        Append(matchingId, GameEventType.Mission, playerId, isBot, description);
    }

    public void LogElimination(
        long matchingId,
        long playerId,
        string reason,
        bool isBot,
        long attackerPlayerId = 0)
    {
        var occurredAt = DateTimeOffset.UtcNow;
        LogFirstElimination(matchingId, playerId, reason, isBot, occurredAt);
        string sourceType = reason == nameof(EliminationReason.PRESSURE_FIELD)
            ? "pressure_field"
            : attackerPlayerId != 0
                ? "pvp"
                : "mental";
        AppendAt(matchingId, GameEventType.Eliminate, playerId, isBot, reason, occurredAt, entry =>
        {
            entry.ActorPlayerId = attackerPlayerId;
            entry.TargetPlayerId = playerId;
            entry.DamageSourceType = sourceType;
            entry.Outcome = reason;
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
        var state = GetCombat(matchingId);
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
            tier == 2 ? GameEventType.SurvivorFirstT2 : GameEventType.SurvivorFirstT3,
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
        var state = GetCombat(matchingId);
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
            GameEventType.SurvivorHit,
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
                GameEventType.SurvivorFirstElimination,
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
            GameEventType.SurvivorCombatElimination,
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
            GameEventType.OrbSuffixCut,
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
            GameEventType.CutAttempt,
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
            GameEventType.CutRetaliationWindow,
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
            GameEventType.OrbGrowthCardsOffered,
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
            GameEventType.OrbGrowthCardSelected,
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
        int healthBefore,
        int healthAfter,
        bool isLethal,
        bool isBot,
        DateTimeOffset occurredAt)
    {
        AppendAt(
            matchingId,
            GameEventType.AfterimageHit,
            targetPlayerId,
            isBot,
            $"Afterimage {monsterId} hit {FormatPlayer(targetPlayerId)} for {damage}; health={healthBefore}->{healthAfter}.",
            occurredAt,
            entry =>
            {
                entry.TargetPlayerId = targetPlayerId;
                entry.Area = area;
                entry.MonsterId = monsterId;
                entry.DamageSourceType = "emotion_afterimage";
                entry.Damage = damage;
                entry.HealthBefore = healthBefore;
                entry.HealthAfter = healthAfter;
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

        Append(matchingId, GameEventType.AfterimageKilled, lastAttackerPlayerId,
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
        int sampleCount)
    {
        if (matchingId <= 0 || sampleCount <= 0)
            return;

        Append(matchingId, GameEventType.SurvivorBotMovementTickPerformance, 0, false,
            $"Bot movement tick: p50={p50Milliseconds:F1}ms, p95={p95Milliseconds:F1}ms, p99={p99Milliseconds:F1}ms, " +
            $"sections p95 snapshot={snapshotP95Milliseconds:F1}ms, planning={planningP95Milliseconds:F1}ms, " +
            $"walking={walkingP95Milliseconds:F1}ms, broadcast={broadcastP95Milliseconds:F1}ms.",
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
            });
    }

    public void LogClosure(long matchingId, string area)
    {
        Append(matchingId, GameEventType.Closure, 0, false, $"구역 폐쇄: {area}");
    }

    public void LogSystem(long matchingId, string description)
    {
        Append(matchingId, GameEventType.System, 0, false, description);
    }

    public void BeginMatch(long matchingId, int seed)
    {
        if (matchingId <= 0) return;
        var state = GetTelemetry(matchingId);
        var startedAt = DateTimeOffset.UtcNow;
        lock (state.SyncRoot)
        {
            if (state.MatchStarted) return;
            state.MatchStarted = true;
            state.MatchStartedAtUtc = startedAt;
        }

        Interlocked.Exchange(ref GetLog(matchingId).Finalized, 0);

        Append(matchingId, GameEventType.MatchStarted, 0, false, $"Match started: seed={seed}.", entry =>
        {
            entry.MatchSeed = seed;
            entry.OccurredAtUnixMs = entry.TimestampUnixMs;
        });
    }

    public void LogSpawnAssignment(long matchingId, long playerId, int seed, int anchorIndex, int cellX, int cellY,
        string area, bool isBot)
    {
        var state = GetTelemetry(matchingId);
        lock (state.SyncRoot)
        {
            if (!state.SpawnLoggedPlayerIds.Add(playerId)) return;
        }

        Append(matchingId, GameEventType.SpawnAssignment, playerId, isBot,
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
        var state = GetTelemetry(matchingId);
        lock (state.SyncRoot)
        {
            state.ExploreStarts[(playerId, interactId)] = now;
        }

        AppendAt(matchingId, GameEventType.ExploreStarted, playerId, isBot,
            $"Explore started: interact={interactId}, area={area}.", now, entry =>
            {
                entry.ActivityId = interactId;
                entry.Area = area;
                entry.StartedAtUnixMs = entry.TimestampUnixMs;
            });
    }

    public void LogExploreCancelled(long matchingId, long playerId, int interactId, string area, string reason,
        bool isBot) => LogExploreFinished(matchingId, playerId, interactId, area, GameEventType.ExploreCancelled, reason,
        [], isBot);

    public void LogGroundItemSpawned(long matchingId, long discovererPlayerId, long groundItemUid, int itemId,
        string area, long priorityExpiresAtUnixMs, bool isBot)
    {
        Append(matchingId, GameEventType.GroundItemSpawned, discovererPlayerId, isBot,
            $"Ground item spawned: uid={groundItemUid}, item={itemId}, priorityUntil={priorityExpiresAtUnixMs}.", entry =>
            {
                entry.Area = area;
                entry.GroundItemUid = groundItemUid;
                entry.ItemId = itemId;
                entry.DiscovererPlayerId = discovererPlayerId;
                entry.PriorityExpiresAtUnixMs = priorityExpiresAtUnixMs;
            });
    }

    public void LogGroundItemPickup(long matchingId, long pickerPlayerId,
        long groundItemUid, int itemId, string area, bool autoUsed, bool isBot)
    {
        TrackEliminationDropPickup(matchingId, groundItemUid, pickerPlayerId);
        Append(matchingId, GameEventType.GroundItemPickedUp, pickerPlayerId, isBot,
            $"Ground item picked up: uid={groundItemUid}, item={itemId}, autoUsed={autoUsed}.", entry =>
            {
                entry.Area = area;
                entry.GroundItemUid = groundItemUid;
                entry.ItemId = itemId;
                entry.PickerPlayerId = pickerPlayerId;
                entry.AutoUsed = autoUsed;
            });
    }

    public void LogRecoveryUse(long matchingId, long playerId, int itemId, int recoveryAmount, string source,
        bool isBot)
    {
        if (recoveryAmount <= 0) return;
        Append(matchingId, GameEventType.RecoveryUsed, playerId, isBot,
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
        Append(matchingId, GameEventType.PelletPickupOutcome, playerId, isBot,
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
        AppendAt(matchingId, GameEventType.EliminationDrop, playerId, isBot,
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

        var state = GetTelemetry(matchingId);
        lock (state.SyncRoot)
            state.PendingEliminationDrops.Add(new PendingEliminationDrop(
                playerId, area, isBot, now.AddSeconds(3), droppedItems));
    }


    public static int CalculateDropRecoveryTotal(IEnumerable<int> itemIds) =>
        itemIds.Sum(itemId => itemId switch
        {
            PlayerPickupService.BandageItemId => PlayerPickupService.BandageRecovery,
            PlayerPickupService.FirstAidKitItemId => PlayerPickupService.FirstAidKitRecovery,
            _ => 0
        });

    /// <summary>사람 플레이어의 입장 당시 아이템과 장비 상태를 기록한다.</summary>
    public void LogInitialInventory(long matchingId, long playerId,
        IReadOnlyCollection<InGameItemInfo> items, int equippedItemId, string area)
    {
        LogOrbBoardTransition(matchingId, playerId, items, equippedItemId, area, "connection_sync", isBot: false);
    }

    public void LogOrbBoardTransition(long matchingId, long playerId,
        IReadOnlyCollection<InGameItemInfo> items, int equippedItemId, string area, string reason, bool isBot)
    {
        var itemIds = items.Where(item => item.Count > 0)
            .SelectMany(item => Enumerable.Repeat(item.ItemId, item.Count)).ToList();
        bool resonance = OrbData.TryGetActivePair(itemIds, out OrbColor color, out int supportTier);
        var transition = TrackOrbTelemetry(matchingId, playerId, itemIds, equippedItemId, resonance, color);
        Append(matchingId, GameEventType.SurvivorOrbBoardState, playerId, isBot,
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
            });


        if (transition.FirstOrbPickup)
            Append(matchingId, GameEventType.SurvivorOrbFirstPickup, playerId, isBot,
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
            Append(matchingId, GameEventType.SurvivorOrbBoardFull, playerId, isBot,
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
            GameEventType type = resonance ? GameEventType.SurvivorOrbResonanceApplied : GameEventType.SurvivorOrbResonanceRemoved;
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
        var state = GetTelemetry(matchingId);
        bool firstStone;
        long? elapsedMilliseconds;
        lock (state.SyncRoot)
        {
            firstStone = state.FirstSummonStoneLoggedPlayerIds.Add(playerId);
            elapsedMilliseconds = state.MatchStartedAtUtc.HasValue
                ? Math.Max(0, (long)(now - state.MatchStartedAtUtc.Value).TotalMilliseconds)
                : null;
        }

        AppendAt(matchingId, GameEventType.SummonStoneAwarded, playerId, isBot,
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
        var state = GetTelemetry(matchingId);
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

        GameEventType type = success ? GameEventType.OrbSummonSucceeded : GameEventType.OrbSummonBlocked;
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
        Append(matchingId, GameEventType.SurvivorOrbPickupBlockedFull, playerId, isBot,
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
            Append(matchingId, GameEventType.SurvivorOrbAttackTargets, primary.AttackerPlayerId,
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
        var state = GetTelemetry(matchingId);
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
                firstOrbPickup,
                boardReachedCapacity,
                boardItemCount,
                matchElapsedMilliseconds);
        }
    }

    private void LogOrbTelemetrySummaries(long matchingId, IReadOnlyCollection<MatchFinalPlayerStats> players)
    {
        if (!TryGetTelemetry(matchingId, out var state)) return;
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
                Append(matchingId, GameEventType.SurvivorOrbSummary, player.PlayerId, BotPlayerManager.IsBotPlayerId(player.PlayerId),
                    $"Orb summary: active={active:F1}s, rate={rate:P0}, colorChanges={telemetry.EquipChanges}.", entry =>
                    { entry.DurationSeconds = active; entry.ScoreDelta = (float)rate; entry.ContributionDelta = telemetry.EquipChanges; });

                foreach (var color in new[] { OrbColor.Red, OrbColor.Green, OrbColor.Blue })
                {
                    secondsByColor.TryGetValue(color, out double colorActive);
                    double colorRate = player.SurvivalTimeSeconds <= 0 ? 0d : colorActive / player.SurvivalTimeSeconds;
                    Append(matchingId, GameEventType.SurvivorOrbColorSummary, player.PlayerId,
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


    private static int CountBoardOrbs(IEnumerable<int> itemIds) =>
        itemIds.Count(itemId => OrbData.IsOrbItem(itemId) || OrbData.IsRecoveryOrb(itemId));

    private static string GetResonanceProfile(OrbColor color) => color switch
    {
        OrbColor.Red => "sun_single_target",
        OrbColor.Green => "wind_multi_target",
        OrbColor.Blue => "wave_burst",
        _ => "none"
    };
    public void LogOvertimeStageChanged(long matchingId, int stage, int damagePerSecond)
    {
        if (stage <= 0 || damagePerSecond <= 0) return;
        var state = GetTelemetry(matchingId);
        lock (state.SyncRoot)
        {
            if (state.OvertimeStage >= stage) return;
            state.OvertimeStage = stage;
        }

        Append(matchingId, GameEventType.OvertimeStageChanged, 0, false,
            $"Overtime stage {stage}: damage={damagePerSecond}/s.", entry =>
            {
                entry.OvertimeStage = stage;
                entry.DamagePerSecond = damagePerSecond;
            });
    }

    public void LogMatchEnded(long matchingId, long winnerPlayerId, string endReason, string tieBreakCriterion,
        IReadOnlyCollection<MatchFinalPlayerStats> players)
    {
        Append(matchingId, GameEventType.MatchEnded, winnerPlayerId, BotPlayerManager.IsBotPlayerId(winnerPlayerId),
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
        Append(matchingId, GameEventType.MatchAbandoned, 0, false,
            $"Match abandoned: reason={endReason}.", entry =>
            {
                entry.EndReason = endReason;
                entry.FinalPlayerStats = players?.ToList();
                entry.CompletedAtUnixMs = entry.TimestampUnixMs;
            });
        if (players is { Count: > 0 })
            LogOrbTelemetrySummaries(matchingId, players);
    }

    public bool TryBeginFinalization(long matchingId)
    {
        if (matchingId <= 0) return false;
        if (_getMatchState(matchingId) is not { IsReleased: false }) return false;
        return Interlocked.CompareExchange(ref GetLog(matchingId).Finalized, 1, 0) == 0;
    }

    public List<GameEventEntry> GetRecent(long matchingId, int limit = MaxEventsPerMatching, long? sinceSeq = null)
    {
        var log = _getMatchState(matchingId)?.Log;
        if (log == null)
            return new List<GameEventEntry>();
        return log.Snapshot(limit, sinceSeq);
    }

    public List<GameEventEntry> GetForPersistence(long matchingId)
    {
        var log = _getMatchState(matchingId)?.Log;
        if (log == null)
            return new List<GameEventEntry>();
        return log.FullSnapshot();
    }

    public void Clear(long matchingId) => _getMatchState(matchingId)?.Release();

    private void LogExploreFinished(long matchingId, long playerId, int interactId, string area, GameEventType type,
        string outcome, IReadOnlyCollection<int> generatedItemIds, bool isBot)
    {
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? startedAt = null;
        var state = GetTelemetry(matchingId);
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

    private GameEventEntry Append(long matchingId, GameEventType type, long playerId, bool isBot, string description,
        Action<GameEventEntry>? configure = null)
    {
        return AppendAt(matchingId, type, playerId, isBot, description, DateTimeOffset.UtcNow, configure);
    }

    private GameEventEntry AppendAt(long matchingId, GameEventType type, long playerId, bool isBot, string description,
        DateTimeOffset timestamp, Action<GameEventEntry>? configure = null)
    {
        var entry = CreateEntry(type, playerId, isBot, description, timestamp, configure);
        var log = GetLog(matchingId);
        log.Add(entry);
        return entry;
    }

    private GameEventEntry CreateEntry(GameEventType type, long playerId, bool isBot, string description,
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
        var state = GetCombat(matchingId);
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
            GameEventType.SurvivorFirstElimination,
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

        var state = GetCombat(matchingId);
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

        var state = GetCombat(matchingId);
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
        if (!TryGetCombat(matchingId, out var state))
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

    internal sealed class MatchCombatState
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
        // 생존자를 가르는 기준(MatchFieldService.ResolveEliminationOrder)이라 의미를 섞으면 판정이 바뀐다.
        public Dictionary<long, int> MonsterKillsByPlayer { get; } = new();
        public Dictionary<long, int> MonsterDamageByPlayer { get; } = new();
        public Dictionary<long, int> RecoveryByPlayer { get; } = new();
        public Dictionary<long, MatchCombatEngagement> EngagementsByAttacker { get; } = new();
    }

    internal sealed class MatchTelemetryState
    {
        public object SyncRoot { get; } = new();
        public bool MatchStarted { get; set; }
        public DateTimeOffset? MatchStartedAtUtc { get; set; }
        public int OvertimeStage { get; set; }
        public HashSet<long> SpawnLoggedPlayerIds { get; } = new();
        public HashSet<long> FirstSummonStoneLoggedPlayerIds { get; } = new();
        public HashSet<long> FirstSuccessfulSummonLoggedPlayerIds { get; } = new();
        public Dictionary<(long PlayerId, int InteractId), DateTimeOffset> ExploreStarts { get; } = new();
        public List<PendingEliminationDrop> PendingEliminationDrops { get; } = new();
        public Dictionary<long, OrbTelemetry> OrbTelemetry { get; } = new();
        public bool OrbSummariesLogged { get; set; }
    }

    private void TrackEliminationDropPickup(long matchingId, long groundItemUid, long pickerPlayerId)
    {
        if (!TryGetTelemetry(matchingId, out var state))
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

    internal sealed class PendingEliminationDrop(
        long playerId, string area, bool isBot, DateTimeOffset observeAtUtc, List<EliminationDroppedItem> items)
    {
        public long PlayerId { get; } = playerId;
        public string Area { get; } = area;
        public bool IsBot { get; } = isBot;
        public DateTimeOffset ObserveAtUtc { get; } = observeAtUtc;
        public List<EliminationDroppedItem> Items { get; } = items;
        public List<EliminationDropPickup> PickupOrder { get; } = new();
    }

    internal sealed class OrbTelemetry
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
        bool FirstOrbPickup,
        bool BoardReachedCapacity,
        int BoardItemCount,
        long? MatchElapsedMilliseconds);
    public readonly record struct ResultStats(
        int KillCount,
        int TotalDamageDealt,
        int TotalRecovery,
        int MonsterKillCount = 0,
        int MonsterDamageDealt = 0);

    internal sealed class MatchCombatEngagement
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

    internal sealed class MatchingEventLog
    {
        internal int Finalized;
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
            Func<GameEventType, long, bool, string, DateTimeOffset, Action<GameEventEntry>?, GameEventEntry> createEntry)
        {
            lock (_lock)
            {
                var rawMove = createEntry(
                    GameEventType.Move,
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
                    GameEventType.AreaEnter,
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
