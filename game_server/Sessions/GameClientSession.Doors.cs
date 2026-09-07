using game_server.services;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.sessions;

public partial class GameClientSession
{
    /// <summary>
    ///     문 열기 요청 처리
    /// </summary>
    private Task HandleDoorOpenRequest(C_TO_G_DOOR_OPEN_REQUEST msg)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;
        return RunWithMatchLock(() => ProcessDoorOpenRequest(msg), () =>
        {
            using var packet = PacketMaker.G_TO_C_DOOR_STATE_UPDATE(msg.DoorId, false, ErrorCode.INVALID_GAME_STATE);
            TrySend(packet);
        });
    }

    private Task ProcessDoorOpenRequest(C_TO_G_DOOR_OPEN_REQUEST msg)
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
            TrySend(lockedPacket);
            return Task.CompletedTask;
        }

        try
        {
            int doorId = msg.DoorId;
            var error = MatchInteractionService.OpenDoor(
                Match, PlayerId.Value, CurrentArea, doorId);
            if (error != ErrorCode.SUCCESS)
            {
                using var rejected = PacketMaker.G_TO_C_DOOR_STATE_UPDATE(
                    doorId, error == ErrorCode.DOOR_ALREADY_OPEN, error);
                TrySend(rejected);
                return Task.CompletedTask;
            }
            Logger.LogInformation("Player {PlayerId} opened door: DoorId={DoorId}", PlayerId, doorId);

            // 같은 매칭의 모든 플레이어에게 브로드캐스트
            using var updatePacket =
                PacketMaker.G_TO_C_DOOR_STATE_UPDATE(doorId, true, ErrorCode.SUCCESS, PlayerId.Value);
            var matchingSessions = _getSessionsByMatch(MatchingId);
            foreach (var session in matchingSessions) session.TrySend(updatePacket);
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

        var openDoors = Doors?.GetOpenDoors() ?? [];
        using var packet = PacketMaker.G_TO_C_DOOR_STATE_LIST(openDoors);
        TrySend(packet);
        Logger.LogDebug("Sent DOOR_STATE_LIST to Player {PlayerId}: {Count} open doors", PlayerId, openDoors.Count);
    }
}
