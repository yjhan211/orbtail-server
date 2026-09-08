using System.Diagnostics;
using game_server.services;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
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
                if (msg.Position == null! || msg.Velocity == null!)
                {
                    Logger.LogWarning("Player {PlayerId} HandleMove: null Position/Velocity", PlayerId);
                    return Task.CompletedTask;
                }

                if (!MovementValidationPolicy.IsFinite(msg.Position) || !MovementValidationPolicy.IsFinite(msg.Velocity) || !float.IsFinite(msg.Rotation))
                {
                    Logger.LogWarning("Player {PlayerId} sent a non-finite movement packet", PlayerId);
                    if (!PlayerId.HasValue || LastValidatedPosition == null)
                    {
                        return Task.CompletedTask;
                    }
                    var cell = _playerMovement.LastValidatedCell ?? MapCoordinateConverter.WorldToCell(CurrentMapId, LastValidatedPosition);
                    using var correctionPacket = PacketMaker.G_TO_C_MOVE(PlayerId.Value, LastValidatedPosition, new Vector3f(), _playerMovement.LastValidatedRotation, cell, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _playerMovement.OrbOrbitPhaseDegrees);
                    TrySend(correctionPacket);
                    _lastMoveAcknowledgementTimestamp = Stopwatch.GetTimestamp();
                    return Task.CompletedTask;
                }

                long receiptTimestamp = Stopwatch.GetTimestamp();
                float deltaTime = GetServerReceiptDeltaSeconds(receiptTimestamp);

                var result = _playerMovement.Apply(msg, deltaTime);
                if (result == null)
                    return Task.CompletedTask;

                var (validation, currentCell, serverTimestamp) = result.Value;
                var validatedPosition = validation.Position;
                var validatedVelocity = validation.Velocity;
                bool requiresClientCorrection = validation.RequiresCorrection;

                // 5. 브로드캐스트 (같은 Area의 플레이어에게만 전송)
                using var packet = PacketMaker.G_TO_C_MOVE(
                    PlayerId.Value,
                    validatedPosition,
                    validatedVelocity,
                    msg.Rotation,
                    currentCell,
                    serverTimestamp,
                    _playerMovement.OrbOrbitPhaseDegrees
                );

                _playerMovement.Broadcast(packet);

                if (requiresClientCorrection || ShouldSendMovementAcknowledgement(receiptTimestamp))
                {
                    TrySend(packet);
                    _lastMoveAcknowledgementTimestamp = receiptTimestamp;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"HandleMove error for player {PlayerId}");
                SendErrorResponse(ErrorCode.SERVER_INTERNAL_ERROR, "이동 처리 오류");
            }
        }

        return Task.CompletedTask;
    }

    protected override Task ScheduleMessageAsync(Protocol protocolId, byte[] body, Func<Task> dispatch)
    {
        bool isMovement = protocolId == Protocol.C_TO_G_MOVE;
        if (isMovement)
        {
            var move = DeserializeClientMessage<C_TO_G_MOVE>(body);
            // 잘못된 값이 보류 중인 정상 이동을 덮어쓰지 않도록 합치기 전에 확인한다.
            if (move?.Position == null || move.Velocity == null ||
                !MovementValidationPolicy.IsFinite(move.Position) ||
                !MovementValidationPolicy.IsFinite(move.Velocity) || !float.IsFinite(move.Rotation))
                return Task.CompletedTask;
        }
        return _movementPacketQueue.EnqueueAsync(dispatch, isMovement);
    }




    private float GetServerReceiptDeltaSeconds(long receiptTimestamp)
    {
        if (_lastMoveReceiptTimestamp == 0)
        {
            _lastMoveReceiptTimestamp = receiptTimestamp;
            return MovementValidationPolicy.InitialReceiptDeltaSeconds;
        }

        double elapsedSeconds = (receiptTimestamp - _lastMoveReceiptTimestamp) / (double)Stopwatch.Frequency;
        _lastMoveReceiptTimestamp = receiptTimestamp;
        return MovementValidationPolicy.ClampReceiptDeltaSeconds(elapsedSeconds);
    }

    private bool ShouldSendMovementAcknowledgement(long receiptTimestamp)
    {
        if (_lastMoveAcknowledgementTimestamp == 0)
            return true;

        double elapsedSeconds = (receiptTimestamp - _lastMoveAcknowledgementTimestamp) / (double)Stopwatch.Frequency;
        return elapsedSeconds >= MovementValidationPolicy.MovementAcknowledgementIntervalSeconds;
    }
}
