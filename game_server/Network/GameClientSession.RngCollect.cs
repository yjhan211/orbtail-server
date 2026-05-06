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

    private const int RngCollectCooldownSeconds = 5; // TODO: 테스트 후 30으로 복원 (#79 시연 빌드 직전)
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
                // 부품 회수 (50%)
                resultType = 3;
                itemId = matchedPart.PartId;
                itemNameKr = matchedPart.PartNameKr;
                staminaReward = matchedPart.StaminaReward;
                // TODO: 후속 — 부품 인벤토리 갱신 + G_TO_C_PART_COLLECTED 송신 (기존 v0.2.0 흐름 통합)
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
                resultType = 2;
                itemNameKr = "스태미나 회복제 +10";
                staminaReward = 10;
                // TODO: 후속 — 인벤토리 스태미나 회복 처리
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
