using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.network;

public partial class GameClientSession
{
    /// <summary>
    ///     문 열기 요청 처리
    /// </summary>
    private Task HandleDoorOpenRequest(C_TO_G_DOOR_OPEN_REQUEST msg)
    {
        if (!PlayerId.HasValue)
        {
            Logger.LogWarning("HandleDoorOpenRequest: PlayerId not set");
            return Task.CompletedTask;
        }
        if (IsRoundActionLocked(out _))
        {
            using var lockedPacket =
                PacketMaker.G_TO_C_DOOR_STATE_UPDATE(msg.DoorId, false, ErrorCode.INVALID_GAME_STATE);
            Send(lockedPacket);
            return Task.CompletedTask;
        }

        try
        {
            int doorId = msg.DoorId;
            var doorInfo = GameDoorData.Get(doorId);
            if (doorInfo == null)
            {
                using var missingDoorPacket =
                    PacketMaker.G_TO_C_DOOR_STATE_UPDATE(doorId, false, ErrorCode.INVALID_GAME_STATE);
                Send(missingDoorPacket);
                return Task.CompletedTask;
            }

            // 폐쇄 구역 문은 밖에서 다시 열 수 없다 (#229): 모든 문이 required_item_id=0이라, 폐쇄로
            // 잠근 문을 상호작용 한 번으로 되열 수 있었다 — 잠금이 사실상 없는 것과 같았다.
            // 양쪽 중 한쪽이라도 닫혔으면 거절한다(간선이므로 한쪽만 닫혀도 통행이 막혀야 한다).
            // 단 내가 그 폐쇄 구역 안에 있으면 예외 (2026-08-18): 갇힌 사람은 문을 따고 나갈 수 있다.
            if (_areaClosureManager != null &&
                (_areaClosureManager.IsAreaClosed(CurrentMapSubId, doorInfo.AreaType) ||
                 _areaClosureManager.IsAreaClosed(CurrentMapSubId, doorInfo.AreaTypeB)) &&
                !_areaClosureManager.IsAreaClosed(CurrentMapSubId, CurrentArea))
            {
                using var closedAreaPacket =
                    PacketMaker.G_TO_C_DOOR_STATE_UPDATE(doorId, false, ErrorCode.INVALID_GAME_STATE);
                Send(closedAreaPacket);
                return Task.CompletedTask;
            }

            // 탐색 게이지가 붙은 문은 근접만으로 열리지 않는다 (#229). Door.CheckProximityAndRequestOpen이
            // 사거리 안에 들면 자동으로 요청을 쏘기 때문에, 여기서 막지 않으면 게이지가 무의미해진다.
            if (GameInteractableData.IsGaugeGatedDoor(doorId) &&
                !_doorStateManager.IsDoorOpen(CurrentMapSubId, doorId))
            {
                using var gatedPacket =
                    PacketMaker.G_TO_C_DOOR_STATE_UPDATE(doorId, false, ErrorCode.DOOR_KEY_MISSING);
                Send(gatedPacket);
                return Task.CompletedTask;
            }

            // 이미 열려있는지 확인
            if (_doorStateManager.IsDoorOpen(CurrentMapSubId, doorId))
            {
                Logger.LogDebug("Player {PlayerId} tried to open already open door: DoorId={DoorId}", PlayerId, doorId);
                using var alreadyOpenPacket =
                    PacketMaker.G_TO_C_DOOR_STATE_UPDATE(doorId, true, ErrorCode.DOOR_ALREADY_OPEN);
                Send(alreadyOpenPacket);
                return Task.CompletedTask;
            }

            // 열쇠 보유 확인 (required_item_id가 0이면 열쇠 불필요)
            if (doorInfo.RequiredItemId > 0)
            {
                var playerInventory = _inGameInventoryManager.GetPlayerInventory(CurrentMapSubId, PlayerId.Value);
                bool hasKey = playerInventory.GetItemCount(doorInfo.RequiredItemId) > 0;

                if (!hasKey)
                {
                    Logger.LogWarning(
                        "Player {PlayerId} missing key for door: DoorId={DoorId}, RequiredItemId={ItemId}",
                        PlayerId, doorId, doorInfo.RequiredItemId);
                    using var noKeyPacket =
                        PacketMaker.G_TO_C_DOOR_STATE_UPDATE(doorId, false, ErrorCode.DOOR_KEY_MISSING);
                    Send(noKeyPacket);
                    return Task.CompletedTask;
                }
            }

            // 문 열기
            _doorStateManager.OpenDoor(CurrentMapSubId, doorId);
            Logger.LogInformation("Player {PlayerId} opened door: DoorId={DoorId}", PlayerId, doorId);

            // 같은 매칭의 모든 플레이어에게 브로드캐스트
            using var updatePacket =
                PacketMaker.G_TO_C_DOOR_STATE_UPDATE(doorId, true, ErrorCode.SUCCESS, PlayerId.Value);
            var matchingSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
            foreach (var session in matchingSessions) session.Send(updatePacket);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "HandleDoorOpenRequest error for player {PlayerId}", PlayerId);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///     열린 문 목록 전송 (입장 시)
    /// </summary>
    private void SendDoorStateList()
    {
        if (!PlayerId.HasValue) return;

        var openDoors = _doorStateManager.GetOpenDoors(CurrentMapSubId);
        using var packet = PacketMaker.G_TO_C_DOOR_STATE_LIST(openDoors);
        Send(packet);
        Logger.LogDebug("Sent DOOR_STATE_LIST to Player {PlayerId}: {Count} open doors", PlayerId, openDoors.Count);
    }
}
