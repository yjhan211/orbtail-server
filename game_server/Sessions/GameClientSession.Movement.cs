using game_server.players;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.sessions;

/// <summary>
///     클라이언트의 이동 요청을 처리한다.
///     매치 잠금 안에서 이동 값과 플레이 가능 상태를 확인하고,
///     이동 검증과 상태 반영은 PlayerMovementService에 맡긴다.
///     처리 결과는 주변 플레이어에게 전송하며,
///     본인에게는 보정이 필요하거나 응답 간격이 지났을 때 전송한다.
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
                if (!MovementValidationPolicy.IsFinite(msg.Position) || !MovementValidationPolicy.IsFinite(msg.Velocity) || !float.IsFinite(msg.Rotation))
                {
                    Logger.LogWarning("Player {PlayerId} sent invalid movement values", PlayerId);
                    return Task.CompletedTask;
                }

                long timestamp = Stopwatch.GetTimestamp();
                float deltaTime = PlayerMovement.CalculateMoveDeltaTime(Player, timestamp);

                var result = PlayerMovement.Apply(match, Player, msg, deltaTime);
                if (result == null)
                {
                    return Task.CompletedTask;
                }

                (var validation, var currentCell, long serverTimestamp) = result.Value;
                var validatedPosition = validation.Position;
                var validatedVelocity = validation.Velocity;
                bool requiresClientCorrection = validation.RequiresCorrection;

                using var packet = PacketMaker.G_TO_C_MOVE(PlayerId.Value, validatedPosition, validatedVelocity, msg.Rotation, currentCell, serverTimestamp, Player.OrbOrbitPhaseDegrees);
                PlayerMovement.Broadcast(this, packet);

                if (requiresClientCorrection || PlayerMovement.ShouldSendMoveResponse(Player, timestamp))
                {
                    TrySend(packet);
                    PlayerMovement.RecordMoveResponse(Player, timestamp);
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
}
