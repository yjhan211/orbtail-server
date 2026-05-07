using game_server.services;
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
    private const int RngCollectCooldownSeconds = 30;
    private const int RngCollectStaminaCost = 5;     // 채집 시작 시 차감 (기존 EXPLORE와 동등 부담, #79 결정)

    private Task HandleRngCollect(C_TO_G_RNG_COLLECT msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;
        if (IsEliminated) return Task.CompletedTask;

        // 30초 쿨타임 체크 (인스턴스 단위 — 같은 매칭 누구든 회수 시 차단)
        if (RngCollectCooldownStore.IsInCooldown(CurrentMapSubId, msg.InteractId, out int remaining))
        {
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

        // 공통 로직 — 봇/플레이어 동일한 RNG 분포 + 인벤토리/부품 처리.
        var outcome = RngCollectCore.Resolve(
            matchingId: CurrentMapSubId,
            playerId: PlayerId.Value,
            jobTitle: MyJobTitle,
            info: info,
            missionManager: _missionManager,
            inventoryManager: _inGameInventoryManager,
            isBot: false);

        // 부품 회수 시 패킷 송신 (PART_COLLECTED + MISSION_STEP_COMPLETE)
        if (outcome is { ResultType: 3, CollectedPart: not null })
        {
            ApplyStaminaReward(outcome.StaminaReward);

            using var partPacket = Packet.Create((int)Protocol.G_TO_C_PART_COLLECTED, PlayerId.Value);
            var partMsg = new G_TO_C_PART_COLLECTED
            {
                PartId = outcome.CollectedPart.PartId,
                PartNameKr = outcome.CollectedPart.PartNameKr,
                PartTier = (int)outcome.CollectedPart.PartTier,
                StaminaReward = outcome.StaminaReward
            };
            partPacket.SetBody(MessagePackSerializer.Serialize(partMsg));
            Send(partPacket);

            using var stepPacket = Packet.Create((int)Protocol.G_TO_C_MISSION_STEP_COMPLETE, PlayerId.Value);
            var stepMsg = new G_TO_C_MISSION_STEP_COMPLETE
            {
                CompletedStep = outcome.CollectedPart.PartId,
                StaminaReward = outcome.StaminaReward,
                NextTargetArea = 0,
                NextTargetInteractId = 0,
                NextTargetActionId = 0
            };
            stepPacket.SetBody(MessagePackSerializer.Serialize(stepMsg));
            Send(stepPacket);

            _gameEventLogManager.LogMission(CurrentMapSubId, PlayerId.Value,
                $"RNG 부품 회수: {outcome.CollectedPart.PartNameKr} (체력+{outcome.StaminaReward})", isBot: false);
        }

        // 소모품 인벤토리 업데이트 패킷 (RngCollectCore가 AddItem만 호출, 패킷은 여기서)
        if (outcome.AddedInventoryItem != null) SendInGameInventoryUpdate(outcome.AddedInventoryItem);

        Logger.LogInformation(
            "RNG 채집: PlayerId={PlayerId}, InteractId={InteractId}, ResultType={Type}, Item={Item}, Stamina={Sta}",
            PlayerId, msg.InteractId, outcome.ResultType, outcome.ItemNameKr, outcome.StaminaReward);

        // 회수자 본인에게는 결과 패킷
        SendRngCollectResult(msg.InteractId, outcome.ResultType, outcome.ItemId, outcome.ItemNameKr,
            outcome.StaminaReward, RngCollectCooldownSeconds);

        // 매칭 내 모든 클라(본인 포함)에게 쿨타임 broadcast — 마커 30초 숨김
        BroadcastRngCollectCooldown(msg.InteractId, RngCollectCooldownSeconds);
        return Task.CompletedTask;
    }

    /// <summary>
    ///     같은 매칭 인스턴스의 모든 플레이어 클라에게 InteractId + cooldown broadcast.
    ///     결과 정보는 포함 X — 직책 노출 방지.
    /// </summary>
    private void BroadcastRngCollectCooldown(int interactId, int cooldownSeconds)
    {
        var sessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var msg = new G_TO_C_RNG_COLLECT_COOLDOWN_BROADCAST
        {
            InteractId = interactId,
            CooldownSeconds = cooldownSeconds
        };
        var body = MessagePackSerializer.Serialize(msg);
        foreach (var session in sessions)
        {
            if (!session.PlayerId.HasValue) continue;
            using var packet = Packet.Create((int)Protocol.G_TO_C_RNG_COLLECT_COOLDOWN_BROADCAST, session.PlayerId.Value);
            packet.SetBody(body);
            session.Send(packet);
        }
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
