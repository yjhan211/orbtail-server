using game_server.matches;
using game_server.players.bots;
using network.common;
using network.common.data;
using network.packets;

namespace game_server.sessions;

/// <summary>봇 이동 결과를 플레이어 프로토콜로 해당 구역의 세션에 전송한다.</summary>
internal static class BotMovementPublisher
{
    public static void SendMovements(MatchRuntime runtime, IReadOnlyList<BotMovementResult> movements)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Bot movement publication requires the match lock.");
        }
        if (runtime.IsEnded)
        {
            throw new InvalidOperationException("Cannot publish bot movement after the match has ended.");
        }
        if (movements.Count == 0) return;
        var sessions = runtime.GetSessions();
        foreach (var movement in movements)
        {
            var player = runtime.Bots.GetBot(movement.BotPlayerId)?.Player;
            if (player != null && player.State == PlayerState.EXPLORE_1 && (movement.Velocity.X != 0f || movement.Velocity.Y != 0f))
            {
                player.ClearPendingInteractions();
                player.State = PlayerState.IDLE;
                using var statePacket = PacketMaker.G_TO_C_PLAYER_STATE(player.PlayerId, player.State);
                foreach (var session in sessions)
                {
                    if (session.Player.CurrentArea == movement.FromArea || session.Player.CurrentArea == movement.ToArea)
                    {
                        session.TrySend(statePacket);
                    }
                }
            }

            if (movement.IsAreaTransition)
            {
                using var leavePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(movement.BotPlayerId);
                foreach (var session in sessions)
                {
                    if (session.Player.CurrentArea == movement.FromArea)
                    {
                        session.TrySend(leavePacket);
                    }
                }

                var enteringBot = runtime.Bots.GetPlayerObjectInfo(movement.BotPlayerId);
                if (enteringBot != null)
                {
                    using var enterPacket = PacketMaker.G_TO_C_AREA_PLAYER_ENTER(enteringBot);
                    foreach (var session in sessions)
                    {
                        if (session.Player.CurrentArea == movement.ToArea)
                        {
                            session.TrySend(enterPacket);
                        }
                    }
                }
            }

            float orbOrbitPhase = runtime.Bots.GetBot(movement.BotPlayerId)?.Player.OrbOrbitPhaseDegrees ?? SwarmOrbOrbit.InitialPhaseDegrees(movement.BotPlayerId);
            using var movePacket = PacketMaker.G_TO_C_MOVE(movement.BotPlayerId, movement.Position, movement.Velocity, movement.Rotation, movement.ToCell, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), orbOrbitPhase);
            foreach (var session in sessions)
            {
                if (session.Player.CurrentArea == movement.ToArea)
                {
                    session.TrySend(movePacket);
                }
            }
        }
    }

}
