using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;

namespace game_server.services;

/// <summary>
///     v0.2.0 — 부품 결합 시스템 미션 매니저 (이슈 #85).
///     기존 단계 기반 미션은 폐기. 직책별 7 부품(소재 4 + 중간재 2 + 최종 1) 회수/결합으로 race 진행.
///     최종 부품 결합 = 즉시 탈출 = race 완주 trigger (#87).
/// </summary>
public class MissionManager
{
    // matchingId → (playerId → PlayerPartState)
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<long, PlayerPartState>> _matchingStates = new();

    // matchingId → race 완주자 (최초 1명만 — 동시성 가드, #87 N12).
    // 동률 시각 시 PlayerId 낮은 쪽이 먼저 등록되도록 lock으로 직렬화한다.
    private readonly ConcurrentDictionary<long, RaceCompletionRecord> _raceWinners = new();
    private readonly object _raceCompletionLock = new();
    private readonly ILogger _logger;

    public MissionManager(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    ///     플레이어 부품 상태 초기화 (게임 시작 시).
    /// </summary>
    public void InitializePlayer(long matchingId, long playerId, JobTitle jobTitle)
    {
        var matchingDict = _matchingStates.GetOrAdd(matchingId,
            _ => new ConcurrentDictionary<long, PlayerPartState>());

        int totalParts = GameMissionData.GetTotalParts((short)jobTitle);

        // 방어: 부품 데이터 없는 직책 → 완료 상태로 초기화
        if (totalParts == 0)
        {
            _logger.LogWarning("부품 데이터 없는 직책: PlayerId={PlayerId}, JobTitle={JobTitle} — 부품 없음으로 초기화",
                playerId, jobTitle);

            matchingDict[playerId] = new PlayerPartState
            {
                PlayerId = playerId,
                JobTitle = jobTitle,
                IsCompleted = true
            };
            return;
        }

        var state = new PlayerPartState
        {
            PlayerId = playerId,
            JobTitle = jobTitle,
            IsCompleted = false
        };

        matchingDict[playerId] = state;
        _logger.LogInformation("부품 초기화: PlayerId={PlayerId}, 직책={JobTitle}, 총 {Total} 부품",
            playerId, jobTitle, totalParts);
    }

    /// <summary>
    ///     부품 회수 시도 (action 2/3 result_type=1 trigger).
    ///     자기 직책 발견 풀에서 (area, objectType) 매칭 부품을 찾아 인벤토리에 추가.
    /// </summary>
    public PartCollectResult? TryCollectPart(long matchingId, long playerId, AreaType area, int objectType)
    {
        if (!_matchingStates.TryGetValue(matchingId, out var matching)) return null;
        if (!matching.TryGetValue(playerId, out var state)) return null;
        if (state.IsCompleted) return null;

        // 직책 발견 풀에서 매칭 부품 찾기 — 영역(area) 단위 매칭 (#135). object_type 무시.
        var materials = GameMissionData.GetMaterials((short)state.JobTitle);
        var matchingPart = materials.FirstOrDefault(p => p.TargetArea == (int)area);

        if (matchingPart == null) return null;
        if (state.CollectedParts.Contains(matchingPart.PartId))
            return new PartCollectResult { ErrorCode = ErrorCode.ACTION_ALREADY_EXPLORED };

        // 선행 아이템 검증
        if (matchingPart.PrerequisiteShareGroup > 0 &&
            !state.CollectedPrereqGroups.Contains(matchingPart.PrerequisiteShareGroup))
        {
            return new PartCollectResult
            {
                ErrorCode = ErrorCode.PREREQUISITE_REQUIRED,
                MissingPrerequisiteGroup = matchingPart.PrerequisiteShareGroup
            };
        }

        state.CollectedParts.Add(matchingPart.PartId);
        _logger.LogInformation("부품 회수: PlayerId={PlayerId}, PartId={PartId} ({Name})",
            playerId, matchingPart.PartId, matchingPart.PartNameKr);

        return new PartCollectResult
        {
            Success = true,
            Part = matchingPart,
            StaminaReward = matchingPart.StaminaReward
        };
    }

    /// <summary>
    ///     선행 아이템 회수 시도. (area, objectType)이 PrerequisiteItemData에 매칭되면 share_group 등록.
    /// </summary>
    public bool TryCollectPrerequisite(long matchingId, long playerId, AreaType area, int objectType)
    {
        if (!_matchingStates.TryGetValue(matchingId, out var matching)) return false;
        if (!matching.TryGetValue(playerId, out var state)) return false;

        // 자기 직책의 선행 아이템 중 매칭 위치 찾기
        var materials = GameMissionData.GetMaterials((short)state.JobTitle);
        foreach (var part in materials)
        {
            if (part.PrerequisiteShareGroup <= 0) continue;
            var prereq = PrerequisiteItemData.GetForPart(part.PartId);
            if (prereq == null) continue;
            if (prereq.LocationArea == (int)area && prereq.LocationObjectType == objectType)
            {
                state.CollectedPrereqGroups.Add(prereq.ShareGroup);
                _logger.LogInformation("선행 아이템 회수: PlayerId={PlayerId}, ShareGroup={Group} ({Name})",
                    playerId, prereq.ShareGroup, prereq.ItemNameKr);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    ///     두 부품 결합 시도. 매칭 레시피가 있고 자기 직책이며 두 입력 모두 보유 시 결합.
    ///     #87 N12: 최종 결합(race 완주)은 매칭당 1명만 허용. 동시 호출은 lock으로 직렬화하고,
    ///     클라이언트 결합 시작 시각이 빠른 쪽 우선, 동률이면 PlayerId 낮은 쪽 우선.
    /// </summary>
    public PartCombineResult TryCombineParts(long matchingId, long playerId, int partA, int partB,
        long clientStartUnixMs = 0, bool requireCollectedParts = true)
    {
        if (!_matchingStates.TryGetValue(matchingId, out var matching))
            return new PartCombineResult { ErrorCode = ErrorCode.SERVER_INTERNAL_ERROR };
        if (!matching.TryGetValue(playerId, out var state))
            return new PartCombineResult { ErrorCode = ErrorCode.SERVER_INTERNAL_ERROR };
        if (state.IsCompleted)
            return new PartCombineResult { ErrorCode = ErrorCode.MISSION_ALREADY_COMPLETED };

        var recipe = PartRecipeData.TryCombine(partA, partB);
        if (recipe == null || recipe.JobTitle != (short)state.JobTitle)
            return new PartCombineResult { ErrorCode = ErrorCode.INVALID_PARAMETER };

        if (requireCollectedParts && (!state.CollectedParts.Contains(partA) || !state.CollectedParts.Contains(partB)))
            return new PartCombineResult { ErrorCode = ErrorCode.INSUFFICIENT_ITEM };

        if (state.CollectedParts.Contains(recipe.OutputPart))
            return new PartCombineResult { ErrorCode = ErrorCode.ACTION_ALREADY_EXPLORED };

        var outputPart = GameMissionData.GetPart(recipe.OutputPart);
        bool isFinal = outputPart?.PartTier == PartTier.Final;

        // 최종 결합은 동시성 직렬화 — 매칭 단위 lock으로 race 완주자 1명만 결정 (#87 N12)
        if (isFinal)
        {
            lock (_raceCompletionLock)
            {
                // 이미 race 완주자가 결정된 경우 — 본 호출은 "근소한 차이로 탈출 실패"
                if (_raceWinners.TryGetValue(matchingId, out var existing))
                {
                    _logger.LogInformation(
                        "race 완주 거절(이미 등록됨): MatchingId={MatchingId}, PlayerId={PlayerId}, 이미 등록된 winner={Winner}",
                        matchingId, playerId, existing.PlayerId);
                    return new PartCombineResult { ErrorCode = ErrorCode.MISSION_ALREADY_COMPLETED };
                }

                // 결합 실행 + winner 등록 (atomic)
                state.CollectedParts.Add(recipe.OutputPart);
                state.IsCompleted = true;

                _raceWinners[matchingId] = new RaceCompletionRecord
                {
                    PlayerId = playerId,
                    ServerCompletedAt = DateTime.UtcNow,
                    ClientStartUnixMs = clientStartUnixMs
                };

                _logger.LogInformation(
                    "race 완주 등록: MatchingId={MatchingId}, PlayerId={PlayerId}, ClientStartMs={Ms}",
                    matchingId, playerId, clientStartUnixMs);

                return new PartCombineResult
                {
                    Success = true,
                    Recipe = recipe,
                    OutputPart = outputPart,
                    IsRaceComplete = true,
                    StaminaReward = outputPart?.StaminaReward ?? 0
                };
            }
        }

        // 중간재 결합 — 동시성 가드 불필요 (자기 인벤토리에만 영향)
        state.CollectedParts.Add(recipe.OutputPart);

        _logger.LogInformation("부품 결합: PlayerId={PlayerId}, {A}+{B} → {Out} (Final=false)",
            playerId, partA, partB, recipe.OutputPart);

        return new PartCombineResult
        {
            Success = true,
            Recipe = recipe,
            OutputPart = outputPart,
            IsRaceComplete = false,
            StaminaReward = outputPart?.StaminaReward ?? 0
        };
    }

    public PlaceGiftResult TryPlaceGift(long matchingId, long playerId, long targetPlayerId,
        long itemUid, int itemId, AreaType area, int interactId)
    {
        if (!_matchingStates.TryGetValue(matchingId, out var matching))
            return new PlaceGiftResult { ErrorCode = ErrorCode.SERVER_INTERNAL_ERROR };
        if (!matching.TryGetValue(playerId, out var state))
            return new PlaceGiftResult { ErrorCode = ErrorCode.SERVER_INTERNAL_ERROR };
        if (state.IsCompleted)
            return new PlaceGiftResult { ErrorCode = ErrorCode.MISSION_ALREADY_COMPLETED };
        if (targetPlayerId == 0 || !matching.ContainsKey(targetPlayerId))
            return new PlaceGiftResult { ErrorCode = ErrorCode.PLAYER_NOT_FOUND };

        if (GameItemData.GetItemType(itemId) != ItemType.PART_GIFT ||
            !GameMissionData.TryGetPartIdFromItemId(itemId, out int partId))
            return new PlaceGiftResult { ErrorCode = ErrorCode.INVALID_ITEM_TYPE };

        var part = GameMissionData.GetPart(partId);
        if (part == null || part.PartTier != PartTier.Intermediate || part.JobTitle != (short)state.JobTitle)
            return new PlaceGiftResult { ErrorCode = ErrorCode.INVALID_ITEM };

        if (!state.CollectedParts.Contains(partId))
            return new PlaceGiftResult { ErrorCode = ErrorCode.INSUFFICIENT_ITEM };

        var interactable = GameInteractableData.Get(interactId);
        if (interactable == null)
            return new PlaceGiftResult { ErrorCode = ErrorCode.INTERACTABLE_NOT_FOUND };
        if (interactable.ZoneId != (int)area)
            return new PlaceGiftResult { ErrorCode = ErrorCode.AREA_MISMATCH };

        lock (state.SyncRoot)
        {
            if (state.IsCompleted)
                return new PlaceGiftResult { ErrorCode = ErrorCode.MISSION_ALREADY_COMPLETED };
            if (state.DeliveredGiftCount >= PlayerPartState.RequiredGiftDeliveries ||
                state.PlacedGifts.Count >= PlayerPartState.RequiredGiftDeliveries)
                return new PlaceGiftResult { ErrorCode = ErrorCode.ACTION_ALREADY_EXPLORED };
            if (state.PlacedGifts.Any(g => g.ItemUid == itemUid || g.ItemId == itemId))
                return new PlaceGiftResult { ErrorCode = ErrorCode.ACTION_ALREADY_EXPLORED };

            state.PlacedGifts.Add(new PlacedGift
            {
                ItemUid = itemUid,
                ItemId = itemId,
                PartId = partId,
                OwnerPlayerId = playerId,
                TargetPlayerId = targetPlayerId,
                AreaType = area,
                InteractId = interactId
            });
        }

        _logger.LogInformation(
            "비밀 선물 설치: MatchingId={MatchingId}, Owner={Owner}, Target={Target}, ItemId={ItemId}, InteractId={InteractId}",
            matchingId, playerId, targetPlayerId, itemId, interactId);

        return new PlaceGiftResult
        {
            Success = true,
            ItemUid = itemUid,
            ItemId = itemId,
            InteractId = interactId,
            AreaType = area,
            TargetPlayerId = targetPlayerId
        };
    }

    public void RollbackPlacedGift(long matchingId, long playerId, long itemUid)
    {
        if (!_matchingStates.TryGetValue(matchingId, out var matching)) return;
        if (!matching.TryGetValue(playerId, out var state)) return;

        lock (state.SyncRoot)
        {
            state.PlacedGifts.RemoveAll(g => g.ItemUid == itemUid && !g.IsDiscovered);
        }
    }

    public bool TryForceGiftDiscovery(long matchingId, long ownerPlayerId, long targetPlayerId,
        int interactId, AreaType area, out GiftDiscoveryResult result)
    {
        result = new GiftDiscoveryResult();
        if (!_matchingStates.TryGetValue(matchingId, out var matching)) return false;
        if (!matching.TryGetValue(ownerPlayerId, out var ownerState)) return false;
        if (!matching.ContainsKey(targetPlayerId)) return false;

        lock (ownerState.SyncRoot)
        {
            bool hasDiscoveredTargetGift = ownerState.PlacedGifts.Any(g =>
                g.IsDiscovered && g.TargetPlayerId == targetPlayerId && g.InteractId == interactId);
            if (hasDiscoveredTargetGift) return false;

            bool hasTargetGift = ownerState.PlacedGifts.Any(g =>
                !g.IsDiscovered && g.TargetPlayerId == targetPlayerId && g.InteractId == interactId);
            if (!hasTargetGift)
            {
                if (ownerState.PlacedGifts.Count >= PlayerPartState.RequiredGiftDeliveries) return false;

                var giftPart = GameMissionData.GetParts((short)ownerState.JobTitle)
                    .FirstOrDefault(p => p.PartTier == PartTier.Intermediate);
                if (giftPart == null) return false;

                int itemId = GameMissionData.GetPartItemId(giftPart.PartId);
                if (itemId <= 0) return false;

                ownerState.PlacedGifts.Add(new PlacedGift
                {
                    ItemUid = -Math.Abs(DateTime.UtcNow.Ticks),
                    ItemId = itemId,
                    PartId = giftPart.PartId,
                    OwnerPlayerId = ownerPlayerId,
                    TargetPlayerId = targetPlayerId,
                    AreaType = area,
                    InteractId = interactId
                });
            }
        }

        return TryDiscoverGift(matchingId, targetPlayerId, interactId, out result)
               && result.DiscoveryType == GiftDiscoveryType.Target
               && result.OwnerPlayerId == ownerPlayerId;
    }

    public RecallGiftResult TryRecallGift(long matchingId, long playerId, int interactId)
    {
        if (!_matchingStates.TryGetValue(matchingId, out var matching))
            return new RecallGiftResult { ErrorCode = ErrorCode.SERVER_INTERNAL_ERROR };
        if (!matching.TryGetValue(playerId, out var state))
            return new RecallGiftResult { ErrorCode = ErrorCode.SERVER_INTERNAL_ERROR };

        lock (state.SyncRoot)
        {
            var gift = state.PlacedGifts.FirstOrDefault(g =>
                g.OwnerPlayerId == playerId && g.InteractId == interactId && !g.IsDiscovered);
            if (gift == null)
                return new RecallGiftResult { ErrorCode = ErrorCode.ITEM_NOT_FOUND, InteractId = interactId };

            state.PlacedGifts.Remove(gift);

            bool hasPlacedGiftAtInteract = state.PlacedGifts.Any(g =>
                !g.IsDiscovered && g.InteractId == interactId);
            bool hasPlacedGiftInArea = state.PlacedGifts.Any(g =>
                !g.IsDiscovered && g.AreaType == gift.AreaType);

            _logger.LogInformation(
                "비밀 선물 회수: MatchingId={MatchingId}, Owner={Owner}, ItemId={ItemId}, InteractId={InteractId}",
                matchingId, playerId, gift.ItemId, interactId);

            return new RecallGiftResult
            {
                Success = true,
                ItemUid = gift.ItemUid,
                ItemId = gift.ItemId,
                InteractId = interactId,
                AreaType = gift.AreaType,
                TargetPlayerId = gift.TargetPlayerId,
                HasPlacedGiftAtInteract = hasPlacedGiftAtInteract,
                HasPlacedGiftInArea = hasPlacedGiftInArea
            };
        }
    }

    public bool TryDiscoverGift(long matchingId, long playerId, int interactId, out GiftDiscoveryResult result)
    {
        result = new GiftDiscoveryResult();
        if (!_matchingStates.TryGetValue(matchingId, out var matching)) return false;

        foreach (var ownerState in matching.Values)
        {
            lock (ownerState.SyncRoot)
            {
                var targetGift = ownerState.PlacedGifts.FirstOrDefault(g =>
                    !g.IsDiscovered && g.TargetPlayerId == playerId && g.InteractId == interactId);
                if (targetGift == null) continue;

                targetGift.IsDiscovered = true;
                ownerState.DeliveredGiftCount = ownerState.PlacedGifts.Count(g => g.IsDiscovered);
                bool hasPlacedGiftAtInteract = ownerState.PlacedGifts.Any(g =>
                    !g.IsDiscovered && g.InteractId == interactId);
                bool hasPlacedGiftInArea = ownerState.PlacedGifts.Any(g =>
                    !g.IsDiscovered && g.AreaType == targetGift.AreaType);

                int finalPartId = 0;
                bool raceComplete = false;
                if (ownerState.DeliveredGiftCount >= PlayerPartState.RequiredGiftDeliveries)
                {
                    var finalPart = GameMissionData.GetParts((short)ownerState.JobTitle)
                        .FirstOrDefault(p => p.PartTier == PartTier.Final);
                    if (finalPart != null)
                    {
                        finalPartId = finalPart.PartId;
                        ownerState.CollectedParts.Add(finalPart.PartId);
                    }

                    raceComplete = TryRegisterRaceCompletion(matchingId, ownerState.PlayerId, 0);
                    ownerState.IsCompleted = raceComplete || ownerState.IsCompleted;
                }

                result = new GiftDiscoveryResult
                {
                    DiscoveryType = GiftDiscoveryType.Target,
                    OwnerPlayerId = ownerState.PlayerId,
                    DiscovererPlayerId = playerId,
                    TargetPlayerId = targetGift.TargetPlayerId,
                    ItemId = targetGift.ItemId,
                    InteractId = interactId,
                    AreaType = targetGift.AreaType,
                    DeliveredCount = ownerState.DeliveredGiftCount,
                    RequiredCount = PlayerPartState.RequiredGiftDeliveries,
                    FinalPartId = finalPartId,
                    IsRaceComplete = raceComplete,
                    HasPlacedGiftAtInteract = hasPlacedGiftAtInteract,
                    HasPlacedGiftInArea = hasPlacedGiftInArea
                };

                _logger.LogInformation(
                    "비밀 선물 발견: MatchingId={MatchingId}, Owner={Owner}, Target={Target}, Delivered={Delivered}/{Required}",
                    matchingId, ownerState.PlayerId, playerId, result.DeliveredCount, result.RequiredCount);
                return true;
            }
        }

        foreach (var ownerState in matching.Values)
        {
            lock (ownerState.SyncRoot)
            {
                var otherGift = ownerState.PlacedGifts.FirstOrDefault(g =>
                    !g.IsDiscovered && g.TargetPlayerId != playerId && g.InteractId == interactId);
                if (otherGift == null) continue;

                result = new GiftDiscoveryResult
                {
                    DiscoveryType = GiftDiscoveryType.Other,
                    OwnerPlayerId = ownerState.PlayerId,
                    DiscovererPlayerId = playerId,
                    TargetPlayerId = otherGift.TargetPlayerId,
                    ItemId = otherGift.ItemId,
                    InteractId = interactId,
                    AreaType = otherGift.AreaType,
                    DeliveredCount = ownerState.DeliveredGiftCount,
                    RequiredCount = PlayerPartState.RequiredGiftDeliveries
                };
                return true;
            }
        }

        return false;
    }

    public bool TryGetNextPlacedGift(long matchingId, long ownerPlayerId, long targetPlayerId, out PlacedGift? gift)
    {
        gift = null;
        if (!_matchingStates.TryGetValue(matchingId, out var matching)) return false;
        if (!matching.TryGetValue(ownerPlayerId, out var ownerState)) return false;

        lock (ownerState.SyncRoot)
        {
            gift = ownerState.PlacedGifts.FirstOrDefault(g =>
                !g.IsDiscovered && g.TargetPlayerId == targetPlayerId);
            return gift != null;
        }
    }

    private bool TryRegisterRaceCompletion(long matchingId, long playerId, long clientStartUnixMs)
    {
        lock (_raceCompletionLock)
        {
            if (_raceWinners.ContainsKey(matchingId)) return false;

            _raceWinners[matchingId] = new RaceCompletionRecord
            {
                PlayerId = playerId,
                ServerCompletedAt = DateTime.UtcNow,
                ClientStartUnixMs = clientStartUnixMs
            };
            return true;
        }
    }

    /// <summary>
    ///     해당 매칭의 race 완주자 조회 (없으면 null).
    /// </summary>
    public RaceCompletionRecord? GetRaceWinner(long matchingId)
    {
        return _raceWinners.GetValueOrDefault(matchingId);
    }

    /// <summary>
    ///     색출 적중 시 마니또의 가장 가치 높은 부품 1개 본인에게 전이 (N10).
    ///     우선순위: Final &gt; Intermediate &gt; Material.
    /// </summary>
    public int? StealHighestPart(long matchingId, long sourcePlayerId, long targetPlayerId)
    {
        if (!_matchingStates.TryGetValue(matchingId, out var matching)) return null;
        if (!matching.TryGetValue(sourcePlayerId, out var sourceState)) return null;
        if (!matching.TryGetValue(targetPlayerId, out var targetState)) return null;
        if (sourceState.CollectedParts.Count == 0) return null;

        int? stolen = sourceState.CollectedParts
            .Select(GameMissionData.GetPart)
            .Where(p => p != null)
            .OrderByDescending(p => (int)p.PartTier)
            .ThenByDescending(p => p.PartId)
            .Select(p => (int?)p.PartId)
            .FirstOrDefault();

        if (stolen.HasValue)
        {
            sourceState.CollectedParts.Remove(stolen.Value);
            targetState.CollectedParts.Add(stolen.Value);
            _logger.LogInformation("색출 부품 전이: PartId={PartId} from {From} to {To}",
                stolen.Value, sourcePlayerId, targetPlayerId);
        }
        return stolen;
    }

    /// <summary>
    ///     사보타주 시 대상의 가장 가치 높은 부품 1개 무효화 (v0.2.0 — 미션 단계 무효화 → 부품 무효화).
    /// </summary>
    public int? InvalidateHighestPart(long matchingId, long targetPlayerId)
    {
        if (!_matchingStates.TryGetValue(matchingId, out var matching)) return null;
        if (!matching.TryGetValue(targetPlayerId, out var state)) return null;
        if (state.CollectedParts.Count == 0) return null;

        int? invalidated = state.CollectedParts
            .Select(GameMissionData.GetPart)
            .Where(p => p != null)
            .OrderByDescending(p => (int)p.PartTier)
            .ThenByDescending(p => p.PartId)
            .Select(p => (int?)p.PartId)
            .FirstOrDefault();

        if (invalidated.HasValue)
        {
            state.CollectedParts.Remove(invalidated.Value);
            _logger.LogInformation("사보타주 부품 무효화: PlayerId={Player}, PartId={PartId}",
                targetPlayerId, invalidated.Value);
        }
        return invalidated;
    }

    public PlayerPartState? GetState(long matchingId, long playerId)
    {
        if (!_matchingStates.TryGetValue(matchingId, out var matching)) return null;
        return matching.GetValueOrDefault(playerId);
    }

    public void CleanupMatching(long matchingId)
    {
        _matchingStates.TryRemove(matchingId, out _);
        _raceWinners.TryRemove(matchingId, out _);
    }
}

/// <summary>
///     #87 — race 완주 기록. 동시 완주 검증/디버깅용.
/// </summary>
public class RaceCompletionRecord
{
    public long PlayerId { get; set; }
    public DateTime ServerCompletedAt { get; set; }
    public long ClientStartUnixMs { get; set; }
}

public class PlayerPartState
{
    public const int RequiredGiftDeliveries = 2;

    public object SyncRoot { get; } = new();
    public long PlayerId { get; set; }
    public JobTitle JobTitle { get; set; }
    public HashSet<int> CollectedParts { get; set; } = new();           // 회수+결합 결과 부품 ID
    public HashSet<int> CollectedPrereqGroups { get; set; } = new();    // 회수한 선행 아이템 share_group
    public List<PlacedGift> PlacedGifts { get; set; } = new();
    public int DeliveredGiftCount { get; set; }
    public bool IsCompleted { get; set; }                               // 최종 결합 시 true (race 완주)
}

public class PlacedGift
{
    public long ItemUid { get; set; }
    public int ItemId { get; set; }
    public int PartId { get; set; }
    public long OwnerPlayerId { get; set; }
    public long TargetPlayerId { get; set; }
    public AreaType AreaType { get; set; }
    public int InteractId { get; set; }
    public bool IsDiscovered { get; set; }
}

public class PartCollectResult
{
    public bool Success { get; set; }
    public ErrorCode ErrorCode { get; set; } = ErrorCode.SUCCESS;
    public MissionPartData? Part { get; set; }
    public int StaminaReward { get; set; }
    public int MissingPrerequisiteGroup { get; set; }
}

public class PartCombineResult
{
    public bool Success { get; set; }
    public ErrorCode ErrorCode { get; set; } = ErrorCode.SUCCESS;
    public PartRecipe? Recipe { get; set; }
    public MissionPartData? OutputPart { get; set; }
    public bool IsRaceComplete { get; set; }
    public int StaminaReward { get; set; }
}

public class PlaceGiftResult
{
    public bool Success { get; set; }
    public ErrorCode ErrorCode { get; set; } = ErrorCode.SUCCESS;
    public long ItemUid { get; set; }
    public int ItemId { get; set; }
    public int InteractId { get; set; }
    public AreaType AreaType { get; set; }
    public long TargetPlayerId { get; set; }
}

public class RecallGiftResult
{
    public bool Success { get; set; }
    public ErrorCode ErrorCode { get; set; } = ErrorCode.SUCCESS;
    public long ItemUid { get; set; }
    public int ItemId { get; set; }
    public int InteractId { get; set; }
    public AreaType AreaType { get; set; }
    public long TargetPlayerId { get; set; }
    public bool HasPlacedGiftAtInteract { get; set; }
    public bool HasPlacedGiftInArea { get; set; }
}

public class GiftDiscoveryResult
{
    public GiftDiscoveryType DiscoveryType { get; set; }
    public long OwnerPlayerId { get; set; }
    public long DiscovererPlayerId { get; set; }
    public long TargetPlayerId { get; set; }
    public int ItemId { get; set; }
    public int InteractId { get; set; }
    public AreaType AreaType { get; set; }
    public int DeliveredCount { get; set; }
    public int RequiredCount { get; set; } = PlayerPartState.RequiredGiftDeliveries;
    public int FinalPartId { get; set; }
    public bool IsRaceComplete { get; set; }
    public bool HasPlacedGiftAtInteract { get; set; }
    public bool HasPlacedGiftInArea { get; set; }
}
