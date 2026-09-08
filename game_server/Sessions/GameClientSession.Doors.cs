using game_server.services;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.sessions;

public partial class GameClientSession
{
    private Task HandleDoorOpenRequest(C_TO_G_DOOR_OPEN_REQUEST msg)
    {
        if (!PlayerId.HasValue)
        {
            return Task.CompletedTask;
        }
        var match = Volatile.Read(ref _match);
        if (match == null)
        {
            using var packet = PacketMaker.G_TO_C_DOOR_STATE_UPDATE(msg.DoorId, false, ErrorCode.INVALID_GAME_STATE);
            TrySend(packet);
            return Task.CompletedTask;
        }

        using (match.Enter())
        {
            if (match.IsTerminal || IsGameplayActionBlocked(out _))
            {
                using var packet = PacketMaker.G_TO_C_DOOR_STATE_UPDATE(msg.DoorId, false, ErrorCode.INVALID_GAME_STATE);
                TrySend(packet);
                return Task.CompletedTask;
            }

            try
            {
                int doorId = msg.DoorId;
                var error = MatchInteractionService.OpenDoor(match, PlayerId.Value, CurrentArea, doorId);
                if (error != ErrorCode.SUCCESS)
                {
                    using var rejected = PacketMaker.G_TO_C_DOOR_STATE_UPDATE(doorId, error == ErrorCode.DOOR_ALREADY_OPEN, error);
                    TrySend(rejected);
                    return Task.CompletedTask;
                }
                Logger.LogInformation("Player {PlayerId} opened door: DoorId={DoorId}", PlayerId, doorId);

                using var updatePacket = PacketMaker.G_TO_C_DOOR_STATE_UPDATE(doorId, true, ErrorCode.SUCCESS, PlayerId.Value);
                var matchingSessions = match.Sessions.Snapshot();
                foreach (var session in matchingSessions)
                {
                    session.TrySend(updatePacket);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "HandleDoorOpenRequest error for player {PlayerId}", PlayerId);
            }
        }

        return Task.CompletedTask;
    }

    private void SendDoorStateList()
    {
        if (!PlayerId.HasValue)
        {
            return;
        }

        var match = Volatile.Read(ref _match);
        if (match == null)
        {
            return;
        }

        using (match.Enter())
        {
            if (match.IsTerminal)
            {
                return;
            }

            var openDoors = match.Doors.GetOpenDoors();
            using var packet = PacketMaker.G_TO_C_DOOR_STATE_LIST(openDoors);
            TrySend(packet);
            Logger.LogDebug("Sent DOOR_STATE_LIST to Player {PlayerId}: {Count} open doors", PlayerId, openDoors.Count);
        }
    }
}
