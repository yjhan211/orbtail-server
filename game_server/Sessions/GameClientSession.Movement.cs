using System.Diagnostics;
using game_server.players;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.sessions;

/// <summary>
///     클라이언트의 이동 요청을 처리한다.
///     매치 잠금 안에서 이동 값과 플레이 가능 상태를 확인하고,
///     이동 검증과 상태 반영은 PlayerMovementService에 맡긴다.
///     처리 결과는 주변 플레이어에게 전송하며, 본인에게는 보정이 필요하거나 응답 간격이 지났을 때 전송한다.
/// </summary>
public partial class GameClientSession
{
    private Task HandleMove(C_TO_G_MOVE msg)
    {
        if (!PlayerId.HasValue)
        {
            return Task.CompletedTask;
        }

        var match = Volatile.Read(ref _match);
        if (match == null)
        {
            return Task.CompletedTask;
        }

        using (match.Enter())
        {
            if (match.IsEnded || PlayerId == null || IsGameplayActionBlocked(out _))
            {
                return Task.CompletedTask;
            }

            try
            {
                if (!PlayerMovementService.IsFinite(msg.Position) || !PlayerMovementService.IsFinite(msg.Velocity) || !float.IsFinite(msg.Rotation))
                {
                    Logger.LogWarning("Player {PlayerId} sent invalid movement values", PlayerId);
                    return Task.CompletedTask;
                }

                long timestamp = Stopwatch.GetTimestamp();
                float deltaTime = Player.CalculateMoveDeltaTime(timestamp);

                var result = _movement.ProcessMovement(match, Player, msg, deltaTime);
                if (result.BlockedCell is { } blockedCell)
                {
                    using var rejected = PacketMaker.G_TO_C_AREA_EXIT_BLOCKED(result.NewArea, blockedCell);
                    TrySend(rejected);
                    Logger.LogDebug("Sent AREA_EXIT_BLOCKED to Player {PlayerId}: Area={Area}, CorrectedCell=({X},{Y})", PlayerId, result.NewArea, blockedCell.X, blockedCell.Y);
                    return Task.CompletedTask;
                }

                if (result.SleepStopped)
                    SendPlayerState();
                if (result.OldArea != result.NewArea)
                    SendAreaChange(result.OldArea, result.NewArea);

                var validation = result.Movement;
                var currentCell = result.Cell;
                long serverTimestamp = result.ServerTimestamp;
                var validatedPosition = validation.Position;
                var validatedVelocity = validation.Velocity;
                bool requiresClientCorrection = validation.RequiresCorrection;

                using var packet = PacketMaker.G_TO_C_MOVE(PlayerId.Value, validatedPosition, validatedVelocity, msg.Rotation, currentCell, serverTimestamp, Player.OrbOrbitPhaseDegrees);
                BroadcastMovement(packet);

                if (requiresClientCorrection || Player.ShouldSendMoveResponse(timestamp))
                {
                    TrySend(packet);
                    Player.RecordMoveResponse(timestamp);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"HandleMove error for player {PlayerId}");
                using var errorPacket = PacketMaker.G_TO_C_ERROR(ErrorCode.SERVER_INTERNAL_ERROR);
                TrySend(errorPacket);
            }
        }

        return Task.CompletedTask;
    }

    internal void SendGroundItemSnapshot(AreaType area)
    {
        if (MatchingId <= 0 || area == AreaType.None)
        {
            return;
        }

        var match = Match;
        if (!Monitor.IsEntered(match.MatchLock))
        {
            throw new InvalidOperationException("Ground item snapshots require the match lock.");
        }

        var items = match.GroundItems.GetItemsInArea(area);
        using var packet = PacketMaker.G_TO_C_GROUND_ITEM_SNAPSHOT((int)area, items);
        TrySend(packet);
    }

    private void SendAreaChange(AreaType oldArea, AreaType newArea)
    {
        try
        {
            if (!PlayerId.HasValue) return;

            Logger.LogInformation("Player {PlayerId} moved from Area {OldArea} to {NewArea}", PlayerId, oldArea,
                newArea);

            var allSessions = Match.GetSessions();
            if (oldArea != AreaType.None)
            {
                var oldAreaSessions = new List<GameClientSession>();
                foreach (var session in allSessions)
                {
                    if (!session.Player.IsEliminated && session.Player.CurrentArea == oldArea && session.PlayerId != PlayerId)
                    {
                        oldAreaSessions.Add(session);
                    }
                }

                using var leavePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(PlayerId.Value);
                foreach (var session in oldAreaSessions)
                {
                    session.TrySend(leavePacket);
                    if (session.PlayerId.HasValue)
                    {
                        using var removePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(session.PlayerId.Value);
                        TrySend(removePacket);
                    }
                }

                // 나에게 이전 Area의 봇들 삭제 알림 (봇은 TCP 세션이 없어 별도 처리)
                var oldAreaBots = Match.Bots.GetBots().Where(b => !b.Player.IsEliminated && b.Player.CurrentArea == oldArea).ToList();
                foreach (var bot in oldAreaBots)
                {
                    using var botLeavePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(bot.PlayerId);
                    TrySend(botLeavePacket);
                }

                Logger.LogDebug("Sent LEAVE to {Count} players + {BotCount} bots in old Area {OldArea}", oldAreaSessions.Count, oldAreaBots.Count, oldArea);
            }

            if (newArea != AreaType.None)
            {
                var newAreaSessions = new List<GameClientSession>();
                foreach (var session in allSessions)
                {
                    if (!session.Player.IsEliminated && session.Player.CurrentArea == newArea && session.PlayerId != PlayerId)
                    {
                        newAreaSessions.Add(session);
                    }

                }
                using var enterPacket = PacketMaker.G_TO_C_AREA_PLAYER_ENTER(_movement.CreateGameObjectInfo(Match, Player, Player.State));
                foreach (var session in newAreaSessions)
                {
                    session.TrySend(enterPacket);
                }

                foreach (var session in newAreaSessions)
                {
                    if (!session.PlayerId.HasValue)
                    {
                        continue;
                    }

                    using var otherEnterPacket = PacketMaker.G_TO_C_AREA_PLAYER_ENTER(_movement.CreateGameObjectInfo(session.Match, session.Player, session.Player.State));
                    TrySend(otherEnterPacket);
                }

                var newAreaBots = Match.Bots.GetBots().Where(b => !b.Player.IsEliminated && b.Player.CurrentArea == newArea).ToList();
                foreach (var bot in newAreaBots)
                {
                    var objectInfo = Match.Bots.SynthesizeGameObjectInfo(bot.PlayerId);
                    if (objectInfo == null)
                    {
                        continue;
                    }
                    using var botEnterPacket = PacketMaker.G_TO_C_AREA_PLAYER_ENTER(objectInfo);
                    TrySend(botEnterPacket);
                }

                if (newAreaBots.Count > 0)
                {
                    Logger.LogDebug("Sent {Count} bots in new Area {NewArea} to Player {PlayerId}", newAreaBots.Count, newArea, PlayerId);
                }

                SendInteractableList();
                SendGroundItemSnapshot(newArea);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "SendAreaChange error for player {PlayerId}", PlayerId);
        }
    }

    private void BroadcastMovement(Packet packet)
    {
        var targetSessions = new List<GameClientSession>();
        foreach (var other in Match.GetSessions())
        {
            if (!other.Player.IsEliminated && other.Player.CurrentArea == Player.CurrentArea && other.PlayerId != PlayerId)
            {
                targetSessions.Add(other);
            }
        }

        foreach (var other in targetSessions)
        {
            other.TrySend(packet);
        }
    }
}
