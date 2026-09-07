using System.Diagnostics;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;
using network.infrastructure.redis;
using network.packets;

namespace game_server.sessions;

public partial class GameClientSession
{
    private static readonly MessagePackSerializerOptions MovementMessagePackOptions =
        MessagePackSerializer.DefaultOptions.WithSecurity(MessagePackSecurity.UntrustedData);

    protected override Task ScheduleMessageAsync(Protocol protocolId, byte[] body, Func<Task> dispatch)
    {
        uint? sequence = null;
        if (protocolId == Protocol.C_TO_G_MOVE)
        {
            var move = MessagePackSerializer.Deserialize<C_TO_G_MOVE>(body, MovementMessagePackOptions);
            // 잘못된 값이 보류 중인 정상 이동을 덮어쓰지 않도록 합치기 전에 확인한다.
            if (move?.Position == null || move.Velocity == null ||
                !MovementValidationPolicy.IsFinite(move.Position) ||
                !MovementValidationPolicy.IsFinite(move.Velocity) || !float.IsFinite(move.Rotation))
                return Task.CompletedTask;
            sequence = move.InputSequence;
        }
        return _movementPacketQueue.EnqueueAsync(dispatch, sequence);
    }

    private async Task HandleMove(C_TO_G_MOVE msg)
    {
        if (PlayerId == null) return;
        if (IsEliminated) return;
        if (IsRoundActionLocked(out _)) return;

        // SLEEP 중에는 이동 불가
        if (_condition.IsSleeping)
        {
            Logger.LogDebug("Player {PlayerId} tried to move while sleeping, ignoring", PlayerId);
            SendErrorResponse(ErrorCode.INVALID_GAME_STATE, "수면 중에는 이동할 수 없습니다");
            return;
        }

        try
        {
            // Client timestamps are diagnostic data only. Movement authority is bounded by
            // the server's monotonic receipt time so a forged future timestamp cannot expand
            // the distance budget.
            if (msg.Position == null! || msg.Velocity == null!)
            {
                Logger.LogWarning("Player {PlayerId} HandleMove: null Position/Velocity", PlayerId);
                return;
            }

            if (!MovementValidationPolicy.IsFinite(msg.Position) ||
                !MovementValidationPolicy.IsFinite(msg.Velocity) ||
                !float.IsFinite(msg.Rotation))
            {
                Logger.LogWarning("Player {PlayerId} sent a non-finite movement packet", PlayerId);
                SendMovementCorrection(msg.InputSequence);
                return;
            }

            if (IsStaleMoveInputSequence(msg.InputSequence))
            {
                Logger.LogWarning("Player {PlayerId} sent stale movement sequence {Sequence}",
                    PlayerId, msg.InputSequence);
                SendMovementCorrection(_lastProcessedMoveInputSequence);
                return;
            }

            // 대기 중인 이동은 MovementPacketQueue가 최신 값으로 합치고 최소 간격 뒤에 넘긴다.
            // 여기서는 실제로 처리하는 이동만 순번·시각을 기록한다.
            long receiptTimestamp = Stopwatch.GetTimestamp();
            RecordMoveInputSequence(msg.InputSequence);
            float deltaTime = GetServerReceiptDeltaSeconds(receiptTimestamp);

            var validation = _movementValidation.ValidatePosition(
                PlayerId.Value, CurrentMapId, _lastValidatedPosition, _lastValidCell,
                msg.Position, msg.Velocity, deltaTime);
            var validatedPosition = validation.Position;
            var validatedVelocity = validation.Velocity;
            bool requiresClientCorrection = validation.RequiresCorrection;
            // 2. Area 변경 시 퇴장 조건 체크 (치팅 방지)
            var currentCell = WorldPositionToCell(validatedPosition);
            var newArea = GameMapData.GetStableCurrentArea(CurrentMapId, currentCell, CurrentArea);


            // 3. Area 변경 처리 (퇴장 조건 통과한 경우만)
            long serverTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var previousCell = _lastValidatedPosition != null
                ? WorldPositionToCell(_lastValidatedPosition)
                : currentCell;
            var blockedCell = _movementValidation.GetBlockedTransitionCell(
                PlayerId.Value, CurrentArea, newArea, previousCell, currentCell, Doors);
            if (blockedCell != null)
            {
                SendAreaExitBlocked(newArea, blockedCell);
                return;
            }
            // 잠긴 문 검증이 끝난 뒤 위치를 게시한다. 폐쇄 구역도 문이 열려 있으면
            // 진입할 수 있으며, 체류 페널티는 ResourceTick에서 서버 권위로 적용한다.
            // #229 6단계: 이동 입력이 곧 수면 해제다 — 누워서 도망칠 수 없다.
            // 2026-08-17 재조정: 수면을 깨우는 건 이 이동뿐이다 (피격·폐쇄는 깨우지 않는다).
            if (_condition.IsSleeping)
                BreakSwarmSleep();
            // 오브 궤도 (#232): 검증된 이동 거리만큼 돈다 — 멈추면 이동 패킷이 없으니 저절로 선다.
            if (_lastValidatedPosition != null)
                AdvanceOrbOrbit(_lastValidatedPosition, validatedPosition);
            _lastValidCell = validation.ValidCell;
            _lastValidatedPosition = validatedPosition;
            _lastValidatedVelocity = validatedVelocity;
            _matchRuntimes.GetRequired(MatchingId).GroundItems.ReleaseSourcePickupBlocks(PlayerId.Value,
                newArea == AreaType.None ? CurrentArea : newArea, validatedPosition.X, validatedPosition.Y);
            _lastValidatedRotation = msg.Rotation;

            if (newArea != CurrentArea && newArea != AreaType.None)
            {
                // 폐쇄 구역 진입 경고 (지속 페널티는 ResourceTick에서 처리)
                if (_matchRuntimes.GetRequired(MatchingId).Closures.IsAreaClosed(newArea))
                {
                    Logger.LogInformation("폐쇄 구역 진입: PlayerId={PlayerId}, Area={Area} (체류 시 오염도 지속 증가)",
                        PlayerId, newArea);
                }

                Logger.LogInformation("Player {PlayerId} Area change at Cell({CellX},{CellY}): {OldArea} → {NewArea}",
                    PlayerId, currentCell.X, currentCell.Y, CurrentArea, newArea);
                var oldArea = CurrentArea;
                CurrentArea = newArea; // 먼저 Area 업데이트 (다른 플레이어의 MOVE 수신 가능하도록)
                _gameEventLogManager.LogMove(MatchingId, PlayerId.Value,
                    oldArea.ToString(), newArea.ToString(), isBot: false);
                HandleAreaChange(oldArea, newArea);
            }



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

            var otherSessions = _getSessionsByMatch(MatchingId);
            var sameAreaSessions = GetSessionsInArea(otherSessions, CurrentArea);

            foreach (var session in sameAreaSessions)
            {
                if (session.PlayerId != PlayerId)
                    session.TrySend(packet);
            }

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

    private void SendMovementCorrection(uint inputSequence)
    {
        if (!PlayerId.HasValue || _lastValidatedPosition is not { } position)
            return;

        var cell = _lastValidCell ?? WorldPositionToCell(position);
        using var packet = PacketMaker.G_TO_C_MOVE(
            PlayerId.Value,
            position,
            new Vector3f(0f, 0f, 0f),
            _lastValidatedRotation,
            cell,
            inputSequence,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            OrbOrbitPhaseDegrees);
        TrySend(packet);
        _lastMoveAcknowledgementTimestamp = Stopwatch.GetTimestamp();
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

    private void HandleAreaChange(AreaType oldArea, AreaType newArea)
    {
        try
        {
            if (!PlayerId.HasValue) return;

            Logger.LogInformation("Player {PlayerId} moved from Area {OldArea} to {NewArea}", PlayerId, oldArea,
                newArea);

            var allSessions = _getSessionsByMatch(MatchingId);

            // 1. 이전 Area의 플레이어들에게 퇴장 알림 + 나에게 기존 플레이어 삭제 알림
            if (oldArea != AreaType.None)
            {
                var oldAreaSessions = GetSessionsInArea(allSessions, oldArea);
                using var leavePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(PlayerId.Value);

                foreach (var session in oldAreaSessions)
                {
                    // 이전 Area 플레이어들에게 내 퇴장 알림
                    session.TrySend(leavePacket);

                    // 나에게 이전 Area 플레이어들 삭제 알림
                    if (session.PlayerId.HasValue)
                    {
                        using var removePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(session.PlayerId.Value);
                        TrySend(removePacket);
                    }
                }

                // #79: 나에게 이전 Area의 봇들 삭제 알림 (봇은 TCP 세션이 없어 별도 처리)
                var oldAreaBots = _matchRuntimes.GetRequired(MatchingId).Bots.GetBots(MatchingId)
                    .Where(b => !b.IsEliminated && b.CurrentArea == oldArea)
                    .ToList();
                foreach (var bot in oldAreaBots)
                {
                    using var botLeavePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(bot.PlayerId);
                    TrySend(botLeavePacket);
                }

                Logger.LogDebug("Sent LEAVE to {Count} players + {BotCount} bots in old Area {OldArea}",
                    oldAreaSessions.Count, oldAreaBots.Count, oldArea);
            }

            // 2. 새 Area의 플레이어들에게 진입 알림 (내 최신 Cell 포함)
            if (newArea != AreaType.None)
            {
                var newAreaSessions = GetSessionsInArea(allSessions, newArea);
                using var enterPacket = PacketMaker.G_TO_C_AREA_PLAYER_ENTER(CaptureGameObjectInfo());

                foreach (var session in newAreaSessions) session.TrySend(enterPacket);

                Logger.LogDebug("Sent ENTER to {Count} players in new Area {NewArea}", newAreaSessions.Count, newArea);

                // 3. 나에게 새 Area의 다른 플레이어 정보 전송 (세션의 최신 Cell 사용)
                foreach (var session in newAreaSessions)
                {
                    if (!session.PlayerId.HasValue) continue;

                    using var otherEnterPacket = PacketMaker.G_TO_C_AREA_PLAYER_ENTER(session.CaptureGameObjectInfo());
                    TrySend(otherEnterPacket);
                }

                Logger.LogDebug("Sent {Count} existing players to Player {PlayerId}", newAreaSessions.Count, PlayerId);

                // 4. #125: 새 Area의 봇들 ENTER도 나에게 전송 (실제 플레이어 동등)
                var newAreaBots = _matchRuntimes.GetRequired(MatchingId).Bots.GetBots(MatchingId)
                    .Where(b => !b.IsEliminated && b.CurrentArea == newArea)
                    .ToList();
                foreach (var bot in newAreaBots)
                {
                    var objectInfo = _matchRuntimes.GetRequired(MatchingId).Bots.SynthesizeGameObjectInfo(MatchingId, bot.PlayerId);
                    if (objectInfo == null) continue;
                    using var botEnterPacket = PacketMaker.G_TO_C_AREA_PLAYER_ENTER(objectInfo);
                    TrySend(botEnterPacket);
                    using var appearance = PacketMaker.G_TO_C_PLAYER_APPEARANCE(
                        bot.PlayerId, BotPlayerManager.BuildBotWearItems(bot));
                    TrySend(appearance);
                }
                if (newAreaBots.Count > 0)
                    Logger.LogDebug("Sent {Count} bots in new Area {NewArea} to Player {PlayerId}",
                        newAreaBots.Count, newArea, PlayerId);

                // 5. 나에게 새 Area의 Interactable 목록 전송
                SendInteractableList(newArea);
                SendGroundItemSnapshot(newArea);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "HandleAreaChange error for player {PlayerId}", PlayerId);
        }
    }

    private void SendInteractableList(AreaType areaType)
    {
        var objects = InteractableStateManager.GetAreaObjectStates(areaType);

        // #229 5단계: 스웜은 상자 탐색을 보내지 않는다 — 마커도 빈 상호작용 UI도 뜰 일이 없다.
        // 단 문 잠금해제(door_id > 0)는 예외다. 방을 여는 유일한 수단이라 스웜의 핵심 조작이다.
        if (Config.IsSwarmExploreDisabled())
            objects = objects.Where(state => IsDoorUnlockInteractable(state.InteractId)).ToList();

        // 이미 열린 문의 마커는 보내지 않는다 — 열린 문 앞에서 게이지가 도는 그림은 거짓말이다.
        objects = objects
            .Where(state => GameInteractableData.Get(state.InteractId) is not { DoorId: > 0 } info ||
                            Doors?.IsDoorOpen(info.DoorId) != true)
            .ToList();
        if (objects.Count == 0)
        {
            Logger.LogDebug("No interactable objects in area {AreaType}", areaType);
            return;
        }

        // 각 오브젝트의 액션 개수 로그
        foreach (var obj in objects)
            Logger.LogDebug("InteractableObject Id={InteractId}: {ActionCount} actions",
                obj.InteractId, obj.Actions.Count);

        using var packet = PacketMaker.G_TO_C_INTERACTABLE_LIST(areaType, objects);
        TrySend(packet);

        Logger.LogDebug(
            "Sent {Count} interactable objects for area {AreaType} to Player {PlayerId} (MatchingId={MatchingId})",
            objects.Count, areaType, PlayerId, MatchingId);
    }

}
