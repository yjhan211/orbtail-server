using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
///     RNG 채집 공통 로직. 봇/플레이어 모두 동일한 분포를 사용.
///     - 자기 직책 풀 매칭: 50/15/25/10 (선행 미사용 직책은 50/0/40/10)
///     - 자기 풀 외: 60/40 (소모품 — object_action.csv reward_pool / 빈손)
///     - 빈손/디코이는 통일 메시지 "아무것도 발견하지 못했다."
///     - 소모품 풀: object_action.csv (object_type, action_id=1)의 result_id를 풀 ID로 ItemPoolManager.GetNextItemFromPool
///     - 발견 시 result_text(kr/en/jp): object_action.csv (object_type, action_id=1) row의 result_text
///     - 쿨타임 등록까지 본 메서드가 처리. 패킷 송신은 호출자 책임.
/// </summary>
public static class RngCollectCore
{
    private const int RngCollectCooldownSeconds = 30;
    private const int FailStaminaReward = 0;
    private const int ConsumableStaminaReward = 10;
    private static readonly Random _rng = new();

    /// <summary>빈손/디코이 통일 메시지 (kr).</summary>
    public const string EmptyResultKr = "아무것도 발견하지 못했다.";
    public const string EmptyResultEn = "Nothing was found.";
    public const string EmptyResultJp = "何も見つからなかった。";

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

        // object_action.csv (object_type, action_id=1) — result_text + reward_pool_id
        var primaryAction = info.Actions.FirstOrDefault(a => a.ActionId == 1) ?? info.Actions.FirstOrDefault();
        string foundTextKr = primaryAction?.ResultText?.Get("kr") ?? "";
        string foundTextEn = primaryAction?.ResultText?.Get("en") ?? "";
        string foundTextJp = primaryAction?.ResultText?.Get("jp") ?? "";
        int rewardPoolId = primaryAction != null && primaryAction.ResultType == ActionResultType.REWARD_POOL
            ? primaryAction.ResultId : 0;

        var materials = GameMissionData.GetMaterials((short)jobTitle);
        var matchedPart = materials.FirstOrDefault(p =>
            p.TargetArea == info.ZoneId && p.TargetObjectType == (int)info.ObjectType);

        if (matchedPart != null)
        {
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
                    outcome.ResultTextKr = foundTextKr;
                    outcome.ResultTextEn = foundTextEn;
                    outcome.ResultTextJp = foundTextJp;
                }
                else
                {
                    SetEmpty(outcome, resultType: 1);
                }
            }
            else if (roll < 65 && hasPrerequisite)
            {
                outcome.ResultType = 4;
                outcome.ItemNameKr = "선행 아이템";
                outcome.ResultTextKr = foundTextKr;
                outcome.ResultTextEn = foundTextEn;
                outcome.ResultTextJp = foundTextJp;
            }
            else if (roll < 90)
            {
                SetEmpty(outcome, resultType: 1);
            }
            else
            {
                SetEmpty(outcome, resultType: 0);
            }
        }
        else
        {
            int roll = _rng.Next(100);
            if (roll < 60)
            {
                int? itemId = itemPoolManager.GetNextItemFromPool(matchingId, info.Id, rewardPoolId);
                if (itemId.HasValue && itemId.Value > 0)
                {
                    outcome.ResultType = 2;
                    outcome.ItemId = itemId.Value;
                    outcome.ItemNameKr = GameItemData.Get(itemId.Value)?.Name?.Get("kr") ?? "";
                    outcome.StaminaReward = ConsumableStaminaReward;
                    outcome.AddedInventoryItem = inventoryManager.AddItem(matchingId, playerId, itemId.Value, 1);
                    outcome.ResultTextKr = foundTextKr;
                    outcome.ResultTextEn = foundTextEn;
                    outcome.ResultTextJp = foundTextJp;
                }
                else
                {
                    // 풀 비었거나 정의 없음 → 빈손 처리
                    SetEmpty(outcome, resultType: 0);
                }
            }
            else
            {
                SetEmpty(outcome, resultType: 0);
            }
        }

        RngCollectCooldownStore.SetCooldown(matchingId, info.Id, RngCollectCooldownSeconds);
        outcome.CooldownSeconds = RngCollectCooldownSeconds;

        return outcome;
    }

    private static void SetEmpty(RngCollectOutcome outcome, int resultType)
    {
        outcome.ResultType = resultType;
        outcome.ItemId = 0;
        outcome.ItemNameKr = "";
        outcome.StaminaReward = FailStaminaReward;
        outcome.ResultTextKr = EmptyResultKr;
        outcome.ResultTextEn = EmptyResultEn;
        outcome.ResultTextJp = EmptyResultJp;
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

    /// <summary>아이템 이름 kr (소모품/부품 명)</summary>
    public string ItemNameKr { get; set; } = "";

    /// <summary>발견 결과 텍스트 (object_action.csv result_text 또는 빈손/디코이 통일 메시지)</summary>
    public string ResultTextKr { get; set; } = "";
    public string ResultTextEn { get; set; } = "";
    public string ResultTextJp { get; set; } = "";

    /// <summary>부품/선행 회수 시 보상 stamina</summary>
    public int StaminaReward { get; set; }

    public int CooldownSeconds { get; set; }

    /// <summary>부품 회수 성공 시 데이터 (호출자 G_TO_C_PART_COLLECTED 송신용)</summary>
    public MissionPartData? CollectedPart { get; set; }

    /// <summary>소모품 회수 시 인벤토리에 추가된 아이템 (호출자 G_TO_C_INGAME_INVENTORY_UPDATE 송신용)</summary>
    public InGameItemInfo? AddedInventoryItem { get; set; }
}
