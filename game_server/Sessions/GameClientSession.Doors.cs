using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.sessions;

public partial class GameClientSession
{
    private Task HandleDoorOpenStart(C_TO_G_DOOR_OPEN_START msg)
    {
        if (!PlayerId.HasValue)
        {
            return Task.CompletedTask;
        }

        var match = Volatile.Read(ref _match);
        if (match == null)
        {
            using var packet = Packet.Create((int)Protocol.G_TO_C_DOOR_OPEN_ACK, PlayerId.Value);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_DOOR_OPEN_ACK
            {
                InteractId = msg.InteractId,
                ErrorCode = ErrorCode.INVALID_GAME_STATE
            }));
            TrySend(packet);
            return Task.CompletedTask;
        }

        using (match.Enter())
        {
            var error = ErrorCode.INVALID_GAME_STATE;
            if (!match.IsTerminal && !IsGameplayActionBlocked(out _) && GameInteractableData.Get(msg.InteractId) is { DoorId: > 0 } info)
            {
                error = info.ZoneId != (int)CurrentArea
                    ? ErrorCode.AREA_MISMATCH
                    : MatchInteractionService.CheckDoorGauge(match, CurrentArea, info.DoorId);
            }

            if (error == ErrorCode.SUCCESS)
            {
                _interactions.BeginDoor(msg.InteractId, Environment.TickCount64);
                _gameEventLogManager.LogExploreStart(MatchingId, PlayerId.Value, msg.InteractId, CurrentArea.ToString(), isBot: false);
            }

            using var packet = Packet.Create((int)Protocol.G_TO_C_DOOR_OPEN_ACK, PlayerId.Value);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_DOOR_OPEN_ACK
            {
                InteractId = msg.InteractId,
                ErrorCode = error
            }));
            TrySend(packet);
        }
        return Task.CompletedTask;
    }

    private Task HandleDoorOpenFinish(C_TO_G_DOOR_OPEN_FINISH msg)
    {
        if (!PlayerId.HasValue)
        {
            return Task.CompletedTask;
        }

        var match = Volatile.Read(ref _match);
        if (match == null)
        {
            using var packet = Packet.Create((int)Protocol.G_TO_C_DOOR_OPEN_ACK, PlayerId.Value);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_DOOR_OPEN_ACK
            {
                InteractId = msg.InteractId,
                ErrorCode = ErrorCode.INVALID_GAME_STATE
            }));
            TrySend(packet);
            return Task.CompletedTask;
        }

        using (match.Enter())
        {
            var error = ErrorCode.INVALID_GAME_STATE;
            int doorId = 0;
            if (!match.IsTerminal && !IsGameplayActionBlocked(out _) && GameInteractableData.Get(msg.InteractId) is { DoorId: > 0 } info)
            {
                doorId = info.DoorId;
                error = info.ZoneId != (int)CurrentArea
                    ? ErrorCode.AREA_MISMATCH
                    : MatchInteractionService.CheckDoorGauge(match, CurrentArea, doorId);
            }

            bool completed = false;
            if (error != ErrorCode.SUCCESS)
            {
                _interactions.TryFinish(msg.InteractId);
            }
            else if (_interactions.TryFinishDoor(msg.InteractId, Environment.TickCount64,
                         TimeSpan.FromSeconds(Config.GetSwarmDoorGaugeSeconds(doorId)), out error))
            {
                MatchInteractionService.FinishDoor(match, _interactions, doorId);
                completed = true;
                using var updatePacket = PacketMaker.G_TO_C_DOOR_STATE_UPDATE(doorId, true, ErrorCode.SUCCESS, PlayerId.Value);
                foreach (var session in match.Sessions.Snapshot())
                {
                    session.TrySend(updatePacket);
                }
            }

            using var packet = Packet.Create((int)Protocol.G_TO_C_DOOR_OPEN_ACK, PlayerId.Value);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_DOOR_OPEN_ACK
            {
                InteractId = msg.InteractId,
                ErrorCode = error,
                Completed = completed
            }));
            TrySend(packet);

            if (completed)
            {
                BroadcastPlayerState(PlayerState.IDLE);
            }
        }
        return Task.CompletedTask;
    }

    internal void BreakDoorUnlockGauge()
    {
        if (_interactions.InterruptDoor() is not { } interactId)
        {
            return;
        }
        _gameEventLogManager.LogExploreCancelled(MatchingId, PlayerId ?? 0, interactId, CurrentArea.ToString(), "door_unlock_hit", isBot: false);
        if (!PlayerId.HasValue)
        {
            return;
        }
        using var packet = Packet.Create((int)Protocol.G_TO_C_DOOR_OPEN_ACK, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_DOOR_OPEN_ACK
        {
            InteractId = interactId,
            ErrorCode = ErrorCode.DOOR_OPEN_INTERRUPTED
        }));
        TrySend(packet);
    }

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
