using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
///     RNG 채집 공통 로직. 봇/플레이어 모두 동일한 분포를 사용.
///     - 자기 직책 풀: 50/15/25/10 (선행 미사용 직책은 50/0/40/10)
///     - 자기 풀 외: 60/40 (소모품 — object_action.csv reward_pool / 빈손)
///     - 결과 텍스트는 패킷에 담지 않음 — 클라가 InteractId로 csv 조회.
///     - 쿨타임 등록까지 본 메서드가 처리. 패킷 송신은 호출자 책임.
/// </summary>
public static class RngCollectCore
{
    private const int RngCollectCooldownSeconds = 30;
    private const int FailStaminaReward = 0;
    // 소모품 회수 stamina 보상 제거 (#135) — 회복은 아이템 사용 시점에만.
    private const int ConsumableStaminaReward = 0;
    private static readonly Random _rng = new();

    public static RngCollectOutcome Resolve(
        long matchingId,
        long playerId,
        JobTitle jobTitle,
        InteractableInfoData info,
        MissionManager missionManager,
        InGameInventoryManager inventoryManager,
        ItemPoolManager itemPoolManager,
        bool isBot)
    {
        var outcome = new RngCollectOutcome();

        // 자기 풀 매칭은 영역(area) 단위 (#135) — object_type 무시. 한 영역에 자기 부품 1개씩 배치된다는 가정.
        var materials = GameMissionData.GetMaterials((short)jobTitle);
        var matchedPart = materials.FirstOrDefault(p =>
            p.TargetArea == info.ZoneId &&
            (p.TargetObjectType == 0 || p.TargetObjectType == (int)info.ObjectType));

        // 자기 부품 이미 회수했으면 그 영역은 자기 풀 외 분기(영역 풀 소모품)로 처리 (#135).
        if (matchedPart != null)
        {
            var state = missionManager.GetState(matchingId, playerId);
            if (state != null && state.CollectedParts.Contains(matchedPart.PartId))
                matchedPart = null;
        }

        if (matchedPart != null)
        {
            int roll = _rng.Next(100);
            bool hasPrerequisite = matchedPart.PrerequisiteShareGroup > 0;

            // 자기 풀: 90% 부품 / 7% 선행(있을 때) / 디코이/빈손 — 시연 시간 내 회수 가능하도록 상향 (#135)
            if (roll < 90)
            {
                var collectResult = missionManager.TryCollectPart(matchingId, playerId,
                    (AreaType)info.ZoneId, (int)info.ObjectType, info.Id);
                if (collectResult is { Success: true, Part: not null })
                {
                    outcome.ResultType = 3;
                    outcome.ItemId = collectResult.Part.PartId;
                    outcome.StaminaReward = 0; // 부품 회수 stamina 보상 제거 (#135)
                    outcome.CollectedPart = collectResult.Part;
                    outcome.CompletedMissionNodeIds = collectResult.CompletedMissionNodeIds;

                    // #135 — 부품을 인벤토리에 추가 (본체/충전재 ID 대역 분리)
                    int partItemId = GameMissionData.GetPartItemId(collectResult.Part.PartId);
                    if (partItemId > 0)
                        outcome.AddedInventoryItem = inventoryManager.AddItem(matchingId, playerId, partItemId, 1);
                }
                else
                {
                    outcome.ResultType = 1; // 디코이 폴백
                }
            }
            else if (roll < 97 && hasPrerequisite)
            {
                outcome.ResultType = 4;
                outcome.ItemId = matchedPart.PrerequisiteShareGroup;
            }
            else if (roll < 99)
            {
                outcome.ResultType = 1; // 디코이
            }
            else
            {
                outcome.ResultType = 0; // 빈손
            }
        }
        else
        {
            int roll = _rng.Next(100);
            // 자기 풀 외: 75% 소모품 / 25% 빈손. 풀은 영역(AreaType) 단위 — area_item_pool.csv (#135)
            if (roll < 75)
            {
                var areaPool = GameInteractableData.GetItemPoolByArea(info.ZoneId);
                if (areaPool.Count > 0)
                {
                    int itemId = areaPool[_rng.Next(areaPool.Count)];
                    outcome.ResultType = 2;
                    outcome.ItemId = itemId;
                    outcome.StaminaReward = ConsumableStaminaReward;
                    outcome.AddedInventoryItem = inventoryManager.AddItem(matchingId, playerId, itemId, 1);
                }
                else
                {
                    outcome.ResultType = 0; // 영역 풀 비어있으면 빈손
                }
            }
            else
            {
                outcome.ResultType = 0; // 빈손
            }
        }

        RngCollectCooldownStore.SetCooldown(matchingId, info.Id, RngCollectCooldownSeconds);
        outcome.CooldownSeconds = RngCollectCooldownSeconds;

        return outcome;
    }
}

/// <summary>
///     RNG 채집 결과. 호출자가 패킷 송신/이벤트 로깅에 사용. 텍스트는 클라가 csv로 조회.
/// </summary>
public class RngCollectOutcome
{
    /// <summary>0=빈손, 1=디코이, 2=소모품, 3=부품, 4=선행</summary>
    public int ResultType { get; set; }

    /// <summary>부품 ID / 소모품 ID / 선행 share_group (빈손/디코이는 0)</summary>
    public int ItemId { get; set; }

    /// <summary>부품/선행 회수 시 보상 stamina</summary>
    public int StaminaReward { get; set; }

    public int CooldownSeconds { get; set; }

    /// <summary>부품 회수 성공 시 데이터 (호출자 G_TO_C_PART_COLLECTED 송신용)</summary>
    public MissionPartData? CollectedPart { get; set; }

    /// <summary>부품 회수와 함께 완료된 미션 그래프 노드 id.</summary>
    public List<int> CompletedMissionNodeIds { get; set; } = new();

    /// <summary>소모품 회수 시 인벤토리에 추가된 아이템 (호출자 G_TO_C_INGAME_INVENTORY_UPDATE 송신용)</summary>
    public InGameItemInfo? AddedInventoryItem { get; set; }
}
