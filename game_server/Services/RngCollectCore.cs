using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
///     RNG 채집 공통 로직. 봇/플레이어 모두 동일한 분포를 사용.
///     #134 — 봇이 InteractObject 회수 시 동등한 결과를 얻도록 GameClientSession.RngCollect 본문에서 추출.
///     - 자기 직책 풀 매칭: 50/15/25/10 (선행 미사용 직책은 50/0/40/10)
///     - 자기 풀 외: 60/40 (소모품/빈손)
///     - 쿨타임 등록까지 본 메서드가 처리.
///     - 패킷 송신은 호출자 책임 (플레이어=GameClientSession, 봇=GameServer).
/// </summary>
public static class RngCollectCore
{
    private const int RngCollectCooldownSeconds = 30;
    private static readonly Random _rng = new();

    public static RngCollectOutcome Resolve(
        long matchingId,
        long playerId,
        JobTitle jobTitle,
        InteractableInfoData info,
        MissionManager missionManager,
        InGameInventoryManager inventoryManager,
        bool isBot)
    {
        var outcome = new RngCollectOutcome();

        var materials = GameMissionData.GetMaterials((short)jobTitle);
        var matchedPart = materials.FirstOrDefault(p =>
            p.TargetArea == info.ZoneId && p.TargetObjectType == (int)info.ObjectType);

        if (matchedPart != null)
        {
            // 자기 풀 50/15/25/10
            int roll = _rng.Next(100);
            bool hasPrerequisite = matchedPart.PrerequisiteShareGroup > 0;

            if (roll < 50)
            {
                var collectResult = missionManager.TryCollectPart(matchingId, playerId,
                    (AreaType)info.ZoneId, (int)info.ObjectType);
                if (collectResult is { Success: true, Part: not null })
                {
                    outcome.ResultType = 3;
                    outcome.ItemId = collectResult.Part.PartId;
                    outcome.ItemNameKr = collectResult.Part.PartNameKr;
                    outcome.StaminaReward = collectResult.StaminaReward;
                    outcome.CollectedPart = collectResult.Part;
                }
                else
                {
                    // 이미 회수/매칭 실패 → 디코이 폴백
                    outcome.ResultType = 1;
                    outcome.ItemNameKr = "쓸모없는 잡동사니";
                }
            }
            else if (roll < 65 && hasPrerequisite)
            {
                outcome.ResultType = 4;
                outcome.ItemNameKr = "선행 아이템";
            }
            else if (roll < 90)
            {
                outcome.ResultType = 1;
                outcome.ItemNameKr = "쓸모없는 잡동사니";
            }
            else
            {
                outcome.ResultType = 0;
                outcome.ItemNameKr = "아무것도 찾지 못했다";
            }
        }
        else
        {
            // 자기 풀 외 60/40
            int roll = _rng.Next(100);
            if (roll < 60)
            {
                int[] consumablePool = { 201000001, 201000002, 201000003, 201000006 };
                int itemId = consumablePool[_rng.Next(consumablePool.Length)];

                outcome.ResultType = 2;
                outcome.ItemId = itemId;
                outcome.ItemNameKr = GameItemData.Get(itemId)?.Name?.Get("kr") ?? "스태미나 회복제";
                outcome.StaminaReward = 10;

                // 인벤토리 추가 (봇/플레이어 동일하게 등록)
                outcome.AddedInventoryItem = inventoryManager.AddItem(matchingId, playerId, itemId, 1);
            }
            else
            {
                outcome.ResultType = 0;
                outcome.ItemNameKr = "아무것도 찾지 못했다";
            }
        }

        // 쿨타임 등록 (인스턴스 단위)
        RngCollectCooldownStore.SetCooldown(matchingId, info.Id, RngCollectCooldownSeconds);
        outcome.CooldownSeconds = RngCollectCooldownSeconds;

        return outcome;
    }
}

/// <summary>
///     RNG 채집 결과. 호출자가 패킷 송신/이벤트 로깅에 사용.
/// </summary>
public class RngCollectOutcome
{
    /// <summary>0=빈손, 1=디코이, 2=소모품, 3=부품, 4=선행</summary>
    public int ResultType { get; set; }

    /// <summary>부품 ID 또는 소모품 ID (빈손/디코이/선행은 0)</summary>
    public int ItemId { get; set; }

    /// <summary>결과 텍스트 (kr) — 클라 ItemAlert 표시용</summary>
    public string ItemNameKr { get; set; } = "";

    /// <summary>부품/선행 회수 시 보상 stamina (호출자가 ApplyStaminaReward 호출)</summary>
    public int StaminaReward { get; set; }

    public int CooldownSeconds { get; set; }

    /// <summary>부품 회수 성공 시 데이터 (호출자 G_TO_C_PART_COLLECTED 송신용)</summary>
    public MissionPartData? CollectedPart { get; set; }

    /// <summary>소모품 회수 시 인벤토리에 추가된 아이템 (호출자 G_TO_C_INGAME_INVENTORY_UPDATE 송신용)</summary>
    public InGameItemInfo? AddedInventoryItem { get; set; }
}
