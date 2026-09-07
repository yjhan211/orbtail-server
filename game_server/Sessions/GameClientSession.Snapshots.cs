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

        var sessions = _getSessionsByMatch(MatchingId)
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

    /// <summary>서버가 승인한 현재 공간 정보를 복사한다. PlayerInfo·Redis 데이터는 건드리지 않는다.</summary>
    internal GameObjectInfo CaptureGameObjectInfo()
    {
        var position = _lastValidatedPosition
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

    /// <summary>
    /// 새 접속과 재접속 시 현재 방송 대상·남은 시간·누적 폐쇄 지역을 복원한다.
    /// 미래 웨이브는 아직 보내지 않아 다음 방송 전까지 대상이 드러나지 않는다.
    /// </summary>
    private void SendAreaClosureStateSnapshot()
    {
        if (MatchingId <= 0) return;

        var snapshot = Match.Closures.GetClientStateSnapshot();
        foreach (var closedArea in snapshot.ClosedAreas)
        {
            using var packet = Packet.Create((int)Protocol.G_TO_C_AREA_CLOSED);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_AREA_CLOSED
            {
                AreaType = closedArea,
                SuppressAlert = true
            }));
            TrySend(packet);
        }

        foreach (var warningArea in snapshot.WarningAreas)
        {
            using var packet = Packet.Create((int)Protocol.G_TO_C_AREA_CLOSURE_WARNING);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_AREA_CLOSURE_WARNING
            {
                AreaType = warningArea,
                SecondsRemaining = snapshot.WarningSeconds,
                ClosureAtUnixMs = snapshot.ClosureAtUnixMs
            }));
            TrySend(packet);
        }
        var globalClosure = Match.Closures.GetGlobalClosureClientState();
        if (globalClosure.IsKnown)
        {
            using var packet = Packet.Create((int)Protocol.G_TO_C_AREA_CLOSURE_WARNING);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_AREA_CLOSURE_WARNING
            {
                AreaType = AreaType.None,
                SecondsRemaining = globalClosure.SecondsRemaining,
                ClosureAtUnixMs = globalClosure.ClosureAtUnixMs,
                IsGlobalClosure = true,
                IsGlobalClosureActive = globalClosure.IsActive
            }));
            TrySend(packet);
        }

        // AreaType.None is the generic next-warning clock.
        using var countdownPacket = Packet.Create((int)Protocol.G_TO_C_AREA_CLOSURE_WARNING);
        countdownPacket.SetBody(MessagePackSerializer.Serialize(new G_TO_C_AREA_CLOSURE_WARNING
        {
            AreaType = AreaType.None,
            SecondsRemaining = snapshot.NextWarningSeconds,
            ClosureAtUnixMs = snapshot.NextWarningAtUnixMs
        }));
        TrySend(countdownPacket);

        // #272 자기장: 수축 시계를 복원한다 — 클라 경계 렌더의 유일한 입력. 폐쇄 시계와
        // 같은 앵커(GameStartTime)라 별도 상태가 없다.
        var closureState = Match.Closures.GetMatchingState();
        if (Config.SWARM_PRESSURE_FIELD_ENABLED && closureState != null)
        {
            using var fieldPacket = Packet.Create((int)Protocol.G_TO_C_SWARM_FIELD_STATE);
            fieldPacket.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SWARM_FIELD_STATE
            {
                StartedAtUnixMs = new DateTimeOffset(closureState.GameStartTime).ToUnixTimeMilliseconds()
            }));
            TrySend(fieldPacket);
        }
    }
}
