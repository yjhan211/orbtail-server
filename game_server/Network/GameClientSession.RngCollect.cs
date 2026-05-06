using System.Collections.Concurrent;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.network;

/// <summary>
///     v0.2.1 (#79) — RNG 채집 흐름.
///     클라가 1.5초 progress 후 C_TO_G_RNG_COLLECT 송신 → 서버 RNG 풀 결정 → G_TO_C_RNG_COLLECT_RESULT 응답.
///     자기 직책 부품 풀 매칭: 50/15/25/10 (LB 등 선행 미사용 직책은 50/0/40/10)
///     자기 풀 외: 60/40 (자잘한 소모품/빈손)
/// </summary>
public partial class GameClientSession
{
    /// <summary>30초 쿨타임 추적: (PlayerId, InteractId) → 다음 회수 가능 시각.</summary>
    private static readonly ConcurrentDictionary<(long, int), DateTime> _rngCollectCooldowns = new();

    private const int RngCollectCooldownSeconds = 30;
    private const int RngCollectStaminaCost = 5;     // 채집 시작 시 차감 (기존 EXPLORE와 동등 부담, #79 결정)
    private static readonly Random _rngCollectRng = new();

    private Task HandleRngCollect(C_TO_G_RNG_COLLECT msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;
        if (IsEliminated) return Task.CompletedTask;

        var now = DateTime.UtcNow;
        var key = (PlayerId.Value, msg.InteractId);

        // 30초 쿨타임 체크
        if (_rngCollectCooldowns.TryGetValue(key, out var nextAvailable) && now < nextAvailable)
        {
            int remaining = (int)Math.Ceiling((nextAvailable - now).TotalSeconds);
            Logger.LogDebug("RNG 채집 쿨타임 거부: PlayerId={PlayerId}, InteractId={InteractId}, 남은={Sec}s",
                PlayerId, msg.InteractId, remaining);
            // 빈손 응답 + 남은 쿨타임 전달 (클라가 ItemAlert 표시)
            SendRngCollectResult(msg.InteractId, 0, 0, "아직 다시 살펴볼 수 없다", 0, remaining);
            return Task.CompletedTask;
        }

        // InteractableInfoData 조회
        var info = GameInteractableData.Get(msg.InteractId);
        if (info == null)
        {
            Logger.LogWarning("RNG 채집 InteractId 미존재: {InteractId}", msg.InteractId);
            SendRngCollectResult(msg.InteractId, 0, 0, "잘못된 오브젝트", 0, 0);
            return Task.CompletedTask;
        }

        // 채집 시작 비용 차감 (스태미나 0이어도 ModifyStats가 정신력 1:2 변환 처리)
        ModifyStats(-RngCollectStaminaCost);

        // 자기 직책 소재 부품 풀 매칭 (mission_step.csv target_area + target_object_type)
        var materials = GameMissionData.GetMaterials((short)MyJobTitle);
        var matchedPart = materials.FirstOrDefault(p =>
            p.TargetArea == info.ZoneId && p.TargetObjectType == (int)info.ObjectType);

        int resultType;
        int itemId = 0;
        string itemNameKr;
        int staminaReward = 0;

        if (matchedPart != null)
        {
            // 자기 풀 50/15/25/10 (선행 미사용 부품은 디코이로 흡수)
            int roll = _rngCollectRng.Next(100);
            bool hasPrerequisite = matchedPart.PrerequisiteShareGroup > 0;

            if (roll < 50)
            {
                // 부품 회수 (50%) — MissionManager.TryCollectPart로 PartCollectionState 갱신 + G_TO_C_PART_COLLECTED + MISSION_STEP_COMPLETE 송신
                var collectResult = _missionManager.TryCollectPart(CurrentMapSubId, PlayerId.Value,
                    (AreaType)info.ZoneId, (int)info.ObjectType);
                if (collectResult != null && collectResult.Success && collectResult.Part != null)
                {
                    resultType = 3;
                    itemId = collectResult.Part.PartId;
                    itemNameKr = collectResult.Part.PartNameKr;
                    staminaReward = collectResult.StaminaReward;
                    ApplyStaminaReward(staminaReward);

                    // G_TO_C_PART_COLLECTED — 부품 회수 알림 (기존 v0.2.0 흐름)
                    using var partPacket = Packet.Create((int)Protocol.G_TO_C_PART_COLLECTED, PlayerId.Value);
                    var partMsg = new G_TO_C_PART_COLLECTED
                    {
                        PartId = collectResult.Part.PartId,
                        PartNameKr = collectResult.Part.PartNameKr,
                        PartTier = (int)collectResult.Part.PartTier,
                        StaminaReward = collectResult.StaminaReward
                    };
                    partPacket.SetBody(MessagePackSerializer.Serialize(partMsg));
                    Send(partPacket);

                    // G_TO_C_MISSION_STEP_COMPLETE — MissionDisplay 갱신 (구 클라 호환)
                    using var stepPacket = Packet.Create((int)Protocol.G_TO_C_MISSION_STEP_COMPLETE, PlayerId.Value);
                    var stepMsg = new G_TO_C_MISSION_STEP_COMPLETE
                    {
                        CompletedStep = collectResult.Part.PartId,
                        StaminaReward = collectResult.StaminaReward,
                        NextTargetArea = 0,
                        NextTargetInteractId = 0,
                        NextTargetActionId = 0
                    };
                    stepPacket.SetBody(MessagePackSerializer.Serialize(stepMsg));
                    Send(stepPacket);

                    _gameEventLogManager.LogMission(CurrentMapSubId, PlayerId.Value,
                        $"RNG 부품 회수: {collectResult.Part.PartNameKr} (체력+{collectResult.StaminaReward})", isBot: false);
                }
                else
                {
                    // 이미 회수된 부품 또는 매칭 실패 → 디코이 폴백
                    resultType = 1;
                    itemNameKr = "쓸모없는 잡동사니";
                }
            }
            else if (roll < 65 && hasPrerequisite)
            {
                // 선행 (15%, 부품에 선행 정의 있을 때만)
                resultType = 4;
                itemNameKr = "선행 아이템";
                // TODO: 후속 — 선행 아이템 슬롯 갱신 + G_TO_C_PREREQUISITE_COLLECTED 송신
            }
            else if (roll < 90)
            {
                // 디코이 (25% 또는 LB 등 선행 미사용 시 40%)
                resultType = 1;
                itemNameKr = "쓸모없는 잡동사니";
            }
            else
            {
                // 빈손 (10%)
                resultType = 0;
                itemNameKr = "아무것도 찾지 못했다";
            }
        }
        else
        {
            // 자기 풀 외 60/40 (자잘한 소모품 / 빈손)
            int roll = _rngCollectRng.Next(100);
            if (roll < 60)
            {
                // 자잘한 소모품 4종 풀 — 랜덤 선택
                int[] consumablePool = { 201000001, 201000002, 201000003, 201000006 };
                itemId = consumablePool[_rngCollectRng.Next(consumablePool.Length)];

                resultType = 2;
                itemNameKr = GameItemData.Get(itemId)?.Name?.Get("kr") ?? "스태미나 회복제";
                staminaReward = 10; // 정보용 (실제 buff는 인벤토리 사용 시 적용)
                // 인벤토리 슬롯 +1 추가 + G_TO_C_INGAME_INVENTORY_UPDATE 자동 송신
                AddInGameItem(itemId, 1);
            }
            else
            {
                resultType = 0;
                itemNameKr = "아무것도 찾지 못했다";
            }
        }

        // 30초 쿨타임 등록
        _rngCollectCooldowns[key] = now.AddSeconds(RngCollectCooldownSeconds);

        Logger.LogInformation(
            "RNG 채집: PlayerId={PlayerId}, InteractId={InteractId}, ResultType={Type}, Item={Item}, Stamina={Sta}",
            PlayerId, msg.InteractId, resultType, itemNameKr, staminaReward);

        SendRngCollectResult(msg.InteractId, resultType, itemId, itemNameKr, staminaReward, RngCollectCooldownSeconds);
        return Task.CompletedTask;
    }

    private void SendRngCollectResult(int interactId, int resultType, int itemId, string itemNameKr,
        int staminaReward, int cooldownSeconds)
    {
        if (!PlayerId.HasValue) return;

        var msg = new G_TO_C_RNG_COLLECT_RESULT
        {
            InteractId = interactId,
            ResultType = resultType,
            ItemId = itemId,
            ItemNameKr = itemNameKr,
            StaminaReward = staminaReward,
            CooldownSeconds = cooldownSeconds
        };

        using var packet = Packet.Create((int)Protocol.G_TO_C_RNG_COLLECT_RESULT, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(msg));
        Send(packet);
    }
}
