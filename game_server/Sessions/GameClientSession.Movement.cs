using System.Diagnostics;
using game_server.services;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.sessions;

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
            if (match.IsTerminal || PlayerId == null || IsEliminated || IsGameplayActionBlocked(out _))
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
                float deltaTime = _playerMovement.CalculateMoveDeltaTime(timestamp);

                var result = _playerMovement.Apply(msg, deltaTime);
                if (result == null)
                {
                    return Task.CompletedTask;
                }

                (var validation, var currentCell, long serverTimestamp) = result.Value;
                var validatedPosition = validation.Position;
                var validatedVelocity = validation.Velocity;
                bool requiresClientCorrection = validation.RequiresCorrection;

                using var packet = PacketMaker.G_TO_C_MOVE(PlayerId.Value, validatedPosition, validatedVelocity, msg.Rotation, currentCell, serverTimestamp, _playerMovement.OrbOrbitPhaseDegrees);
                _playerMovement.Broadcast(packet);

                if (requiresClientCorrection || _playerMovement.ShouldSendMoveResponse(timestamp))
                {
                    TrySend(packet);
                    _playerMovement.RecordMoveResponse(timestamp);
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
