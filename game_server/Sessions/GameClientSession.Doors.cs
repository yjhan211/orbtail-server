using game_server.field;
using game_server.matches;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.sessions;

/// <summary>
///     문 열기 요청과 문 상태 전송을 담당한다.
///     게이지 문은 START에서 시작 시각을 기록하고, FINISH에서 대기 시간이 지났는지 확인한 뒤 연다.
///     피격으로 게이지가 중단되면 요청자에게 알리고, 문이 열리면 같은 매치의 모든 플레이어에게 알린다.
///     문 상태 조회와 변경은 매치 잠금 안에서 처리한다.
/// </summary>
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
            if (!match.IsEnded && !IsGameplayActionBlocked(out _) && GameInteractableData.Get(msg.InteractId) is { DoorId: > 0 } info)
            {
                error = info.ZoneId != (int)Player.CurrentArea
                    ? ErrorCode.AREA_MISMATCH
                    : MatchInteractionService.StartDoor(match, Player, msg.InteractId, info.DoorId, Environment.TickCount64);
            }

            if (error == ErrorCode.SUCCESS)
            {
                _gameEventLogManager.LogExploreStart(MatchingId, PlayerId.Value, msg.InteractId, Player.CurrentArea.ToString(), isBot: false);
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
            if (!match.IsEnded && !IsGameplayActionBlocked(out _) && GameInteractableData.Get(msg.InteractId) is { DoorId: > 0 } info)
            {
                doorId = info.DoorId;
                error = info.ZoneId != (int)Player.CurrentArea
                    ? ErrorCode.AREA_MISMATCH
                    : MatchInteractionService.CheckDoorGauge(match, Player.CurrentArea, doorId);
            }

            bool completed = false;
            if (error != ErrorCode.SUCCESS)
            {
                Player.TryFinishInteraction(msg.InteractId);
            }
            else if (MatchInteractionService.TryFinishDoor(match, Player, msg.InteractId, doorId, Environment.TickCount64, out error))
            {
                completed = true;
                using var updatePacket = PacketMaker.G_TO_C_DOOR_STATE_UPDATE(doorId, true, ErrorCode.SUCCESS, PlayerId.Value);
                foreach (var session in match.GetSessions())
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
                Player.State = PlayerState.IDLE;
                SendPlayerState();
            }
        }
        return Task.CompletedTask;
    }

    internal void SendDoorOpenInterrupted(int interactId)
    {
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
            if (match.IsEnded)
            {
                return;
            }

            var openDoors = match.Doors.GetOpenDoors();
            using var packet = PacketMaker.G_TO_C_DOOR_STATE_LIST(openDoors);
            TrySend(packet);
            Logger.LogDebug("Sent DOOR_STATE_LIST to Player {PlayerId}: {Count} open doors", PlayerId, openDoors.Count);
        }
    }

    private void SendInteractionCanceled(int[] canceledIds, string reason)
    {
        foreach (int interactId in canceledIds)
        {
            _gameEventLogManager.LogExploreCancelled(MatchingId, PlayerId.GetValueOrDefault(), interactId, Player.CurrentArea.ToString(), reason, isBot: false);
            using var packet = Packet.Create((int)Protocol.G_TO_C_DOOR_OPEN_ACK, PlayerId.GetValueOrDefault());
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_DOOR_OPEN_ACK
            {
                InteractId = interactId,
                ErrorCode = ErrorCode.DOOR_OPEN_INTERRUPTED
            }));
            TrySend(packet);
        }
    }

    private void SendInteractableList( AreaType areaType)
    {
        if (areaType == AreaType.None)
        {
            return;
        }

        var match = Match;
        using (match.Enter())
        {
            if (match.IsEnded) return;

            var objects = InteractableStateManager.GetAreaObjectStates(areaType);
            if (Config.IsSwarmExploreDisabled())
            {
                objects = objects.Where(state => GameInteractableData.Get(state.InteractId) is { DoorId: > 0 }).ToList();
            }

            objects = objects.Where(state => GameInteractableData.Get(state.InteractId) is not { DoorId: > 0 } info || !match.Doors.IsDoorOpen(info.DoorId)).ToList();
            if (objects.Count == 0)
            {
                return;
            }

            using var packet = PacketMaker.G_TO_C_INTERACTABLE_LIST(areaType, objects);
            TrySend(packet);
        }
    }

}
