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
                    if (PlayerId.HasValue && LastValidatedPosition is { } position)
                    {
                        var cell = _lastValidCell ?? WorldPositionToCell(position);
                        using var correctionPacket = PacketMaker.G_TO_C_MOVE(PlayerId.Value, position, new Vector3f(), _lastValidatedRotation, cell, msg.InputSequence, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), OrbOrbitPhaseDegrees);
                        TrySend(correctionPacket);
                        _lastMoveAcknowledgementTimestamp = Stopwatch.GetTimestamp();
                    }
                    return Task.CompletedTask;
                }

                if (IsStaleMoveInputSequence(msg.InputSequence))
                {
                    Logger.LogWarning("Player {PlayerId} sent stale movement sequence {Sequence}",
                        PlayerId, msg.InputSequence);
                    if (PlayerId.HasValue && LastValidatedPosition is { } position)
                    {
                        var cell = _lastValidCell ?? WorldPositionToCell(position);
                        using var correctionPacket = PacketMaker.G_TO_C_MOVE(
                            PlayerId.Value, position, new Vector3f(0f, 0f, 0f),
                            _lastValidatedRotation, cell, _lastProcessedMoveInputSequence,
                            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), OrbOrbitPhaseDegrees);
                        TrySend(correctionPacket);
                        _lastMoveAcknowledgementTimestamp = Stopwatch.GetTimestamp();
                    }
                    return Task.CompletedTask;
                }

                long receiptTimestamp = Stopwatch.GetTimestamp();
                RecordMoveInputSequence(msg.InputSequence);
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
                    msg.InputSequence,
                    serverTimestamp,
                    OrbOrbitPhaseDegrees
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
        uint? sequence = null;
        if (protocolId == Protocol.C_TO_G_MOVE)
        {
            var move = DeserializeClientMessage<C_TO_G_MOVE>(body);
            // 잘못된 값이 보류 중인 정상 이동을 덮어쓰지 않도록 합치기 전에 확인한다.
            if (move?.Position == null || move.Velocity == null ||
                !MovementValidationPolicy.IsFinite(move.Position) ||
                !MovementValidationPolicy.IsFinite(move.Velocity) || !float.IsFinite(move.Rotation))
                return Task.CompletedTask;
            sequence = move.InputSequence;
        }
        return _movementPacketQueue.EnqueueAsync(dispatch, sequence);
    }

    private bool IsStaleMoveInputSequence(uint sequence)
    {
        if (!_hasProcessedMoveInputSequence || sequence == _lastProcessedMoveInputSequence)
            return false;

        // Unsigned subtraction keeps the ordering valid across uint wrap-around.
        return (uint)(sequence - _lastProcessedMoveInputSequence) > uint.MaxValue / 2;
    }

    private void RecordMoveInputSequence(uint sequence)
    {
        if (!_hasProcessedMoveInputSequence || sequence != _lastProcessedMoveInputSequence)
        {
            _lastProcessedMoveInputSequence = sequence;
            _hasProcessedMoveInputSequence = true;
        }
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

    #region Isometric 좌표 변환 (Unity Isometric Z as Y 타일맵)

    /// <summary>
    ///     World Position → Cell. 공식·원점 보정은 MapCoordinateConverter(Common) 한 곳이 소유하며
    ///     클라 MapManager.WorldToCell과 같은 결과를 낸다 — 셀 오프셋 같은 별도 보정은 없다.
    /// </summary>
    private Cell WorldPositionToCell(Vector3f worldPos) =>
        MapCoordinateConverter.WorldToCell(CurrentMapId, worldPos);

    /// <summary>
    ///     Cell → World position 역변환 (구역 이동 시 스폰용).
    ///     WorldPositionToCell의 역함수.
    /// </summary>
    internal Vector3f CellToWorldPosition(Cell cell) =>
        MapCoordinateConverter.CellToWorld(CurrentMapId, cell);

    #endregion






    /// <summary>
    ///     오브 궤도 위상 (#232): 이동할 때 돌고 멈추면 선다 — 검증 이동 거리를 적산한다.
    ///     서버 전투가 오브별 자리(SwarmOrbOrbit)를 계산하는 근거이자, G_TO_C_MOVE로 클라에 보내는 보정값.
    /// </summary>
    private float OrbOrbitPhaseDegrees =>
        _orbOrbitPhaseDegrees ?? SwarmOrbOrbit.InitialPhaseDegrees(PlayerId ?? 0L);

    /// <summary>검증된 이동만큼 궤도를 돌린다 (텔레포트급 점프는 SwarmOrbOrbit이 무시한다).</summary>
    internal void AdvanceOrbOrbit(Vector3f from, Vector3f to)
    {
        float dx = to.X - from.X;
        float dy = to.Y - from.Y;
        _orbOrbitPhaseDegrees = SwarmOrbOrbit.AdvancePhase(
            OrbOrbitPhaseDegrees, MathF.Sqrt(dx * dx + dy * dy));
    }
}
