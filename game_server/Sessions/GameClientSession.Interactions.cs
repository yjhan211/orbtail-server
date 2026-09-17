using MessagePack;
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
    private Task HandleInteractionStart(C_TO_G_INTERACTION_START msg)
    {
        if (!PlayerId.HasValue)
        {
            return Task.CompletedTask;
        }

        var match = Volatile.Read(ref _match);
        if (match == null)
        {
            using var packet = Packet.Create((int)Protocol.G_TO_C_INTERACTION_ACK, PlayerId.Value);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_INTERACTION_ACK
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
                if (info.ZoneId != (int)Player.CurrentArea)
                {
                    error = ErrorCode.AREA_MISMATCH;
                }
                else
                {
                    error = _interactions.StartDoor(match, Player, msg.InteractId, info.DoorId, Environment.TickCount64);
                }
            }

            using var packet = Packet.Create((int)Protocol.G_TO_C_INTERACTION_ACK, PlayerId.Value);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_INTERACTION_ACK
            {
                InteractId = msg.InteractId,
                ErrorCode = error
            }));
            TrySend(packet);
        }
        return Task.CompletedTask;
    }

    private Task HandleInteractionFinish(C_TO_G_INTERACTION_FINISH msg)
    {
        if (!PlayerId.HasValue)
        {
            return Task.CompletedTask;
        }

        var match = Volatile.Read(ref _match);
        if (match == null)
        {
            using var packet = Packet.Create((int)Protocol.G_TO_C_INTERACTION_ACK, PlayerId.Value);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_INTERACTION_ACK
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
                if (info.ZoneId != (int)Player.CurrentArea)
                {
                    error = ErrorCode.AREA_MISMATCH;
                }
                else
                {
                    error = _interactions.CheckDoorGauge(match, Player, msg.InteractId, doorId);
                }
            }

            bool completed = false;
            if (error != ErrorCode.SUCCESS)
            {
                Player.Interactions.Cancel(msg.InteractId);
            }
            else if (_interactions.TryFinishDoor(match, Player, msg.InteractId, doorId, Environment.TickCount64, out error))
            {
                completed = true;

            }

            using var packet = Packet.Create((int)Protocol.G_TO_C_INTERACTION_ACK, PlayerId.Value);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_INTERACTION_ACK
            {
                InteractId = msg.InteractId,
                ErrorCode = error,
                Completed = completed
            }));
            TrySend(packet);

            if (!completed)
            {
                if (error == ErrorCode.SUCCESS)
                {
                    return Task.CompletedTask;
                }
                if (error == ErrorCode.DOOR_OPEN_TOO_EARLY)
                {
                    return Task.CompletedTask;
                }
                if (Player.Interactions.PendingInteractId.HasValue)
                {
                    return Task.CompletedTask;
                }
                if (Player.State != PlayerState.EXPLORE_1)
                {
                    return Task.CompletedTask;
                }
            }
            Player.State = PlayerState.IDLE;
        }
        return Task.CompletedTask;
    }

    internal void SendDoorOpenInterrupted(int interactId)
    {
        if (!PlayerId.HasValue)
        {
            return;
        }
        using var packet = Packet.Create((int)Protocol.G_TO_C_INTERACTION_ACK, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_INTERACTION_ACK
        {
            InteractId = interactId,
            ErrorCode = ErrorCode.DOOR_OPEN_INTERRUPTED
        }));
        TrySend(packet);
    }

    private void SendInteractionCanceled(int[] canceledIds, string reason)
    {
        foreach (int interactId in canceledIds)
        {
            using var packet = Packet.Create((int)Protocol.G_TO_C_INTERACTION_ACK, PlayerId.GetValueOrDefault());
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_INTERACTION_ACK
            {
                InteractId = interactId,
                ErrorCode = ErrorCode.DOOR_OPEN_INTERRUPTED
            }));
            TrySend(packet);
        }
    }

    internal AreaType PublishedInteractionArea { get; set; } = AreaType.None;
    internal HashSet<int> PublishedOpenDoors { get; } = new();

    internal List<InteractableInfo> GetInteractableInfos()
    {
        return _interactions.GetInteractionInfos(Match);
    }

    internal void SendInteractableInfos(List<InteractableInfo> infos)
    {
        var area = Player.CurrentArea;
        using var packet = PacketMaker.G_TO_C_INTERACTABLE_INFO(area, infos);
        if (!TrySend(packet))
        {
            PublishedInteractionArea = AreaType.None;
            return;
        }
        PublishedInteractionArea = area;
        PublishedOpenDoors.Clear();
        foreach (var info in infos)
        {
            var definition = GameInteractableData.Get(info.InteractId);
            if (info.IsCompleted && definition.DoorId > 0)
            {
                PublishedOpenDoors.Add(definition.DoorId);
            }
        }
    }

    private void SendInteractableList()
    {
        if (Player.CurrentArea == AreaType.None) return;
        using (Match.Enter())
        {
            if (Match.IsEnded) return;
            SendInteractableInfos(GetInteractableInfos());
        }
    }
}
