using game_server.services;
using MessagePack;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.sessions;

// 초기 입장 시 플레이어·봇·구역 폐쇄 상태를 패킷으로 전송한다.
public partial class GameClientSession
{
    private Task BroadcastPlayerJoin()
    {
        if (!PlayerId.HasValue || IsEliminated) return Task.CompletedTask;

        var sessions = Match.Sessions.Snapshot()
            .Where(s => s.PlayerId.HasValue && s.PlayerId != PlayerId &&
                        !s.IsEliminated && s.CurrentArea == CurrentArea)
            .ToList();

        if (sessions.Count > 0)
        {
            using var others = PacketMaker.G_TO_C_OBJECT_INFO(sessions.Select(s => s.CaptureGameObjectInfo()).ToList());
            TrySend(others);
        }

        using (var mine = PacketMaker.G_TO_C_OBJECT_INFO([CaptureGameObjectInfo()]))
            foreach (var session in sessions) session.TrySend(mine);

        var bots = Match.Bots.GetBots(MatchingId)
            .Where(b => !b.IsEliminated && b.CurrentArea == CurrentArea).ToList();
        var objects = bots.Select(b => Match.Bots.SynthesizeGameObjectInfo(MatchingId, b.PlayerId))
            .OfType<GameObjectInfo>().ToList();
        if (objects.Count > 0)
        {
            using var packet = PacketMaker.G_TO_C_OBJECT_INFO(objects);
            TrySend(packet);
            foreach (var bot in bots)
            {
                using var appearance = PacketMaker.G_TO_C_PLAYER_APPEARANCE(
                    bot.PlayerId, BotPlayerManager.BuildBotWearItems(bot));
                TrySend(appearance);
            }
        }
        return Task.CompletedTask;
    }

    internal Cell? LastValidatedCell => _lastValidCell;

    /// <summary>검증된 위치·셀·속도·회전을 함께 반영한다. 호출자는 매치 잠금을 잡아야 한다.</summary>
    internal void ApplyValidatedMovement(ValidatedMovement movement, float rotation)
    {
        _lastValidCell = movement.ValidCell;
        LastValidatedPosition = movement.Position;
        _lastValidatedVelocity = movement.Velocity;
        _lastValidatedRotation = rotation;
    }

    internal void ChangeMovementArea(AreaType area) => CurrentArea = area;

    /// <summary>서버가 승인한 현재 공간 정보를 복사한다. PlayerInfo·Redis 데이터는 건드리지 않는다.</summary>
    internal GameObjectInfo CaptureGameObjectInfo()
    {
        var position = LastValidatedPosition
            ?? throw new InvalidOperationException("Cannot publish a player before its spawn is initialized.");
        var cell = _lastValidCell ?? WorldPositionToCell(position);
        return new GameObjectInfo(ObjectType.PLAYER, PlayerId!.Value, CurrentMapId, MatchingId, cell)
        {
            Position = new Vector3f(position.X, position.Y, position.Z),
            Velocity = new Vector3f(_lastValidatedVelocity.X, _lastValidatedVelocity.Y, _lastValidatedVelocity.Z),
            Rotation = _lastValidatedRotation,
            State = _condition.IsSleeping ? PlayerState.SLEEP : CurrentState
        };
    }

    /// <summary>입장한 클라이언트에 자기장 수축 시작 시각을 보낸다. 경계는 공용 규칙으로 계산한다.</summary>
    private void SendPressureFieldState()
    {
        if (MatchingId <= 0 || !Config.SWARM_PRESSURE_FIELD_ENABLED) return;
        var state = Match.Closures.GetMatchingState();
        if (state == null) return;

        using var packet = Packet.Create((int)Protocol.G_TO_C_SWARM_FIELD_STATE);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SWARM_FIELD_STATE
        {
            StartedAtUnixMs = new DateTimeOffset(state.GameStartTime).ToUnixTimeMilliseconds()
        }));
        TrySend(packet);
    }
}
