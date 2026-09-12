using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches.logging;

public class GameEventEntry
{
    /// <summary>이벤트 본체와 변경 가능한 목록을 복사한다. 목록 안의 불변 record는 공유한다.</summary>
    internal GameEventEntry CopyForPersistence()
    {
        var copy = (GameEventEntry)MemberwiseClone();
        copy.AlreadyPresentPlayerIds = AlreadyPresentPlayerIds?.ToList();
        copy.MonsterDamageContributions = MonsterDamageContributions?.ToList();
        copy.GeneratedItemIds = GeneratedItemIds?.ToList();
        copy.BoardItemIds = BoardItemIds?.ToList();
        copy.PreviousBoardItemIds = PreviousBoardItemIds?.ToList();
        copy.AttackTargetPlayerIds = AttackTargetPlayerIds?.ToList();
        copy.OpenAreas = OpenAreas?.ToList();
        copy.EliminationDroppedItems = EliminationDroppedItems?.ToList();
        copy.DropPickupOrder = DropPickupOrder?.ToList();
        copy.UncollectedDroppedItemUids = UncollectedDroppedItemUids?.ToList();
        copy.FinalPlayerStats = FinalPlayerStats?.ToList();
        copy.LinkedLogIds = LinkedLogIds?.ToList();
        return copy;
    }

    public long Seq { get; set; }
    public long TimestampUnixMs { get; set; }
    public GameEventType Type { get; set; }
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
    // 과거 매치 로그를 읽기 위한 필드. 현재 틱 루프는 잠금 경합 시 건너뛰지 않는다.
    public int? BotMovementTickSkipCount { get; set; }
    public int? BotMovementMaxConsecutiveSkipCount { get; set; }
    public int? CoreCurrentHealth { get; set; }
    public int? CoreMaxHealth { get; set; }
    public string? DamageSourceType { get; set; }
    public long? FirstAttackerPlayerId { get; set; }
    public long? LastAttackerPlayerId { get; set; }
    public List<MonsterDamageContribution>? MonsterDamageContributions { get; set; }
    public int? HealthBefore { get; set; }
    public int? HealthAfter { get; set; }
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
    public List<string>? OpenAreas { get; set; }
    public int? Health { get; set; }
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
    public int? DamagePerSecond { get; set; }
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

/// <summary>같은 표적이 1초 창 안에 교차사격을 몇 발 맞았는지와 창이 열린 지 몇 ms인지.</summary>
public readonly record struct SwarmCrossfireConvergenceObservation(
    int HitCount,
    double WindowMilliseconds);
