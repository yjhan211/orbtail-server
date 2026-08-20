using System.Diagnostics;
using game_server.services;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;
using network.packets;

namespace game_server.network;

public partial class GameClientSession
{
    private async Task HandleMove(C_TO_G_MOVE msg)
    {
        if (PlayerId == null) return;
        if (IsEliminated) return;
        if (IsRoundActionLocked(out _)) return;

        var now = DateTime.UtcNow;

        // 탐색 진입 직후에는 도착 정산용 in-flight 이동 패킷이 늦게 도착할 수 있다.
        if (CurrentState == PlayerState.Exploring && now > _exploreMoveGraceUntil)
        {
            Logger.LogDebug("Player {PlayerId} tried to move while exploring, ignoring", PlayerId);
            SendErrorResponse(ErrorCode.INVALID_GAME_STATE, "탐색 중에는 이동할 수 없습니다");
            return;
        }

        // SLEEP 중에는 이동 불가
        if (_isSleeping)
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

            RecordMoveInputSequence(msg.InputSequence);
            long receiptTimestamp = Stopwatch.GetTimestamp();
            float deltaTime = GetServerReceiptDeltaSeconds(receiptTimestamp);
            _lastMoveTime = now;

            var validatedPosition = ValidatePosition(
                msg.Position,
                msg.Velocity,
                deltaTime,
                out bool requiresClientCorrection,
                out var validatedVelocity);

            // 2. Area 변경 시 퇴장 조건 체크 (치팅 방지)
            var currentCell = WorldPositionToCell(validatedPosition);
            var newArea = GameMapData.GetStableCurrentArea(CurrentMapId, currentCell, CurrentArea);


            // 3. Area 변경 처리 (퇴장 조건 통과한 경우만)
            long serverTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // 셀이 어떤 영역에도 속하지 않으면 (영역 경계 바로 밖 등) CurrentArea를 None으로 덮어쓰지 않음
            // — 일시적 None 상태에서 마커 클릭 시 IsAdjacent(None, X) = false로 INVALID_AREA 거절되는 문제 방지
            if (newArea != CurrentArea && newArea != AreaType.None)
            {
                var survivorPhase = _survivorPhaseManager?.GetSnapshot(CurrentMapSubId)
                                    ?? SurvivorPhaseSnapshot.Empty;
                if (survivorPhase.Phase == SurvivorMatchPhase.ROOM_COMBAT &&
                    survivorPhase.CurrentRooms.Contains(CurrentArea) &&
                    !survivorPhase.ClearedRooms.Contains(CurrentArea))
                {
                    var fallbackCell = _lastValidatedPosition != null
                        ? WorldPositionToCell(_lastValidatedPosition)
                        : currentCell;
                    Logger.LogDebug(
                        "Player {PlayerId} cannot leave uncleared survivor room {Area}",
                        PlayerId,
                        CurrentArea);
                    SendAreaExitBlocked(newArea, fallbackCell);
                    return;
                }

                // 이 전이를 관장하는 문 기준으로 잠김 체크 (클라이언트 IsAreaExitBlocked와 동일 판정).
                // "영역의 가장 가까운 문" 휴리스틱은 열린 문과 잠긴 문이 공존하는 방에서
                // 열린 문 통과까지 오차단한다 (예: 창고1의 열린 111 옆 잠긴 113).
                var previousCell = _lastValidatedPosition != null
                    ? WorldPositionToCell(_lastValidatedPosition)
                    : currentCell;
                var transitionDoor = GameDoorData.GetDoorForTransition(
                    CurrentArea, newArea, previousCell, currentCell);
                if (transitionDoor != null &&
                    !_doorStateManager.IsDoorOpen(CurrentMapSubId, transitionDoor.DoorId))
                {
                    Logger.LogWarning(
                        "Player {PlayerId} blocked crossing {CurrentArea}→{NewArea} (locked door: {DoorId})",
                        PlayerId, CurrentArea, newArea, transitionDoor.DoorId);

                    // 문 소속 방에서 나가려던 경우 안쪽(door cell), 들어가려던 경우 바깥(fallback)으로 보정
                    var blockedCell = transitionDoor.AreaType == CurrentArea
                        ? new Cell((int)transitionDoor.PositionX, (int)transitionDoor.PositionY)
                        : new Cell(transitionDoor.FallbackCellX, transitionDoor.FallbackCellY);
                    SendAreaExitBlocked(newArea, blockedCell);
                    return;
                }
            }

            // 잠긴 문 검증이 끝난 뒤 위치를 게시한다. 폐쇄 구역도 문이 열려 있으면
            // 진입할 수 있으며, 체류 페널티는 ResourceTick에서 서버 권위로 적용한다.
            // #229 6단계: 이동 입력이 곧 수면 해제다 — 누워서 도망칠 수 없다.
            // 2026-08-17 재조정: 수면을 깨우는 건 이 이동뿐이다 (피격·폐쇄는 깨우지 않는다).
            if (_isSleeping)
                BreakSwarmSleep();
            // 오브 궤도 (#232): 검증된 이동 거리만큼 돈다 — 멈추면 이동 패킷이 없으니 저절로 선다.
            if (_lastValidatedPosition != null)
                AdvanceOrbOrbit(_lastValidatedPosition, validatedPosition);
            _lastValidatedPosition = validatedPosition;
            _groundItemManager.ReleaseSourcePickupBlocks(CurrentMapSubId, PlayerId.Value,
                newArea == AreaType.None ? CurrentArea : newArea, validatedPosition.X, validatedPosition.Y);
            _lastValidatedRotation = msg.Rotation;

            if (newArea != CurrentArea && newArea != AreaType.None)
            {
                // 폐쇄 구역 진입 경고 (지속 페널티는 ResourceTick에서 처리)
                if (_areaClosureManager.IsAreaClosed(CurrentMapSubId, newArea))
                {
                    Logger.LogInformation("폐쇄 구역 진입: PlayerId={PlayerId}, Area={Area} (체류 시 오염도 지속 증가)",
                        PlayerId, newArea);
                }

                Logger.LogInformation("Player {PlayerId} Area change at Cell({CellX},{CellY}): {OldArea} → {NewArea}",
                    PlayerId, currentCell.X, currentCell.Y, CurrentArea, newArea);
                var oldArea = CurrentArea;
                _previousArea = oldArea; // 이전 구역 기록 (상호작용 동선추궁용)
                CurrentArea = newArea; // 먼저 Area 업데이트 (다른 플레이어의 MOVE 수신 가능하도록)
                _presenceTracker?.SetPlayerArea(CurrentMapSubId, PlayerId.Value, newArea,
                    countAsEntry: true);
                _gameEventLogManager.LogMove(CurrentMapSubId, PlayerId.Value,
                    oldArea.ToString(), newArea.ToString(), isBot: false);
                LogContestedCoreEntry(newArea);
                await HandleAreaChange(oldArea, newArea);
            }


            TrySendCorridorEncounterEvents(validatedPosition);

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

            var otherSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
            var sameAreaSessions = GetSessionsInArea(otherSessions, CurrentArea);

            foreach (var session in sameAreaSessions)
            {
                if (session.PlayerId != PlayerId)
                    session.Send(packet);
            }

            if (requiresClientCorrection || ShouldSendMovementAcknowledgement(receiptTimestamp))
            {
                Send(packet);
                _lastMoveAcknowledgementTimestamp = receiptTimestamp;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, $"HandleMove error for player {PlayerId}");
            SendErrorResponse(ErrorCode.SERVER_INTERNAL_ERROR, "이동 처리 오류");
        }
    }

    /// <summary>
    ///     클라이언트 Position 검증 (치트 방지)
    ///     정상이면 클라이언트 Position 사용, 비정상이면 서버 계산 Position 사용
    /// </summary>
    private Vector3f ValidatePosition(
        Vector3f clientPos,
        Vector3f velocity,
        float deltaTime,
        out bool requiresClientCorrection,
        out Vector3f validatedVelocity)
    {
        requiresClientCorrection = false;
        validatedVelocity = MovementValidationPolicy.ClampVelocity(velocity);

        const float maxSpeed = MovementValidationPolicy.MaximumSpeedUnitsPerSecond;

        // clientPos, velocity는 호출 전에 null 체크 완료

        // Z값은 항상 0으로 고정
        clientPos.Z = 0;

        // 1. 속도 제한 체크
        float speed = velocity.Magnitude();
        if (speed > maxSpeed)
        {
            Logger.LogWarning("Player {PlayerId} 속도 초과: {Speed:F2} > {MaxSpeed}", PlayerId, speed, maxSpeed);
            if (_lastValidatedPosition != null)
            {
                clientPos = new Vector3f(
                    _lastValidatedPosition.X + validatedVelocity.X * deltaTime,
                    _lastValidatedPosition.Y + validatedVelocity.Y * deltaTime,
                    0
                );
                requiresClientCorrection = true;
            }
        }

        // 2. 서버가 관측한 시간 안에 가능한 거리만 허용한다. 클라이언트 timestamp는
        // 이 예산을 늘릴 수 없으며, 첫 이동도 서버가 세션에 저장한 스폰 위치에서 시작한다.
        if (_lastValidatedPosition != null)
        {
            var delta = clientPos - _lastValidatedPosition;
            float distance = delta.Magnitude();
            float maxDistance = maxSpeed * deltaTime;

            if (distance > maxDistance)
            {
                Logger.LogWarning(
                    "Player {PlayerId} teleport detected: distance={Distance:F2}, maxAllowed={MaxDistance:F2}",
                    PlayerId, distance, maxDistance);
                var direction = delta.Normalized();
                clientPos = new Vector3f(
                    _lastValidatedPosition.X + direction.X * maxDistance,
                    _lastValidatedPosition.Y + direction.Y * maxDistance,
                    0
                );
                validatedVelocity = direction * maxSpeed;
                requiresClientCorrection = true;
            }
        }

        // 3. Cell 기반 이동 가능 여부 검증 (맵 밖 이탈 방지)
        var clientCell = WorldPositionToCell(clientPos);
        if (!GameMapData.IsMoveablePosition(CurrentMapId, clientCell))
        {
            if (_lastValidatedPosition is not null && _lastValidCell is not null)
            {
                Logger.LogWarning(
                    "Player {PlayerId} 이동 불가 위치 감지: ClientPos=({CX:F2},{CY:F2}), Cell=({CellX},{CellY}), 보정 → ({VX:F2},{VY:F2})",
                    PlayerId, clientPos.X, clientPos.Y, clientCell.X, clientCell.Y,
                    _lastValidatedPosition.X, _lastValidatedPosition.Y);
                requiresClientCorrection = true;
                validatedVelocity = new Vector3f(0f, 0f, 0f);
                return _lastValidatedPosition;
            }

            Logger.LogWarning(
                "Player {PlayerId} 이동 불가 위치 감지 (초기 위치): ClientPos=({CX:F2},{CY:F2}), Cell=({CellX},{CellY})",
                PlayerId, clientPos.X, clientPos.Y, clientCell.X, clientCell.Y);
            requiresClientCorrection = true;
            validatedVelocity = new Vector3f(0f, 0f, 0f);
            return _lastValidatedPosition ?? clientPos;
        }

        // 4. 중간 벽 셀을 건너뛰는 이동 차단
        if (_lastValidatedPosition is not null && _lastValidCell is not null &&
            !GridMovementTraversal.IsTraversable(
                _lastValidCell,
                clientCell,
                candidate => GameMapData.IsMoveablePosition(CurrentMapId, candidate)))
        {
            Logger.LogWarning(
                "Player {PlayerId} attempted to cross an impassable cell: From=({FromX},{FromY}), To=({ToX},{ToY})",
                PlayerId, _lastValidCell.X, _lastValidCell.Y, clientCell.X, clientCell.Y);
            requiresClientCorrection = true;
            validatedVelocity = new Vector3f(0f, 0f, 0f);
            return _lastValidatedPosition;
        }

        // 원본 속도가 아니라 서버가 승인한 위치 변화에서 원격 표시용 속도를 산출한다.
        if (_lastValidatedPosition is not null && deltaTime > 0f)
        {
            var acceptedDelta = clientPos - _lastValidatedPosition;
            validatedVelocity = MovementValidationPolicy.ClampVelocity(new Vector3f(
                acceptedDelta.X / deltaTime,
                acceptedDelta.Y / deltaTime,
                0f));
        }
        _lastValidCell = clientCell;

        // 검증 통과: 클라이언트 Position 사용
        return clientPos;
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
        Send(packet);
        _lastMoveAcknowledgementTimestamp = Stopwatch.GetTimestamp();
    }

    #region Isometric 좌표 변환 (Unity Isometric Z as Y 타일맵)

    /// <summary>
    ///     World Position을 Cell 좌표로 변환
    ///     Unity Isometric Z as Y 타일맵의 WorldToCell과 동일한 로직
    ///     클라이언트 MapManager.WorldToCell:
    ///     var unityCell = tileMap.WorldToCell(position);
    ///     return new Vector3Int(unityCell.x + CellOffsetX, unityCell.y + CellOffsetY + 1, 0);
    ///     Unity Isometric Z as Y 역변환:
    ///     unityCellX = floor(WorldX + 2 * WorldY)
    ///     unityCellY = floor (2 * WorldY - WorldX)
    ///     최종 Cell = unityCell + CellOffset (Y는 +1 추가)
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

    private async Task HandleAreaChange(AreaType oldArea, AreaType newArea)
    {
        try
        {
            if (!PlayerId.HasValue) return;

            Logger.LogInformation("Player {PlayerId} moved from Area {OldArea} to {NewArea}", PlayerId, oldArea,
                newArea);

            var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
            var playerInfo = await PlayerInfo.Load(CacheHelper, PlayerId.Value);

            if (playerInfo == null) return;

            ApplyLivePlayerInfoSnapshot(this, playerInfo);

            // 내 최신 위치로 playerInfo 업데이트
            if (_lastValidatedPosition != null)
            {
                var latestCell = WorldPositionToCell(_lastValidatedPosition);
                playerInfo.ObjectInfo.Position = _lastValidatedPosition;
                playerInfo.ObjectInfo.Cell = latestCell;
                playerInfo.LastCell = latestCell;
            }

            // 1. 이전 Area의 플레이어들에게 퇴장 알림 + 나에게 기존 플레이어 삭제 알림
            if (oldArea != AreaType.None)
            {
                var oldAreaSessions = GetSessionsInArea(allSessions, oldArea);
                using var leavePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(PlayerId.Value);

                foreach (var session in oldAreaSessions)
                {
                    // 이전 Area 플레이어들에게 내 퇴장 알림
                    session.Send(leavePacket);

                    // 나에게 이전 Area 플레이어들 삭제 알림
                    if (session.PlayerId.HasValue)
                    {
                        using var removePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(session.PlayerId.Value);
                        Send(removePacket);
                    }
                }

                // #79: 나에게 이전 Area의 봇들 삭제 알림 (봇은 TCP 세션이 없어 별도 처리)
                var oldAreaBots = _botPlayerManager.GetBots(CurrentMapSubId)
                    .Where(b => !b.IsEliminated && b.CurrentArea == oldArea)
                    .ToList();
                foreach (var bot in oldAreaBots)
                {
                    using var botLeavePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(bot.PlayerId);
                    Send(botLeavePacket);
                }

                Logger.LogDebug("Sent LEAVE to {Count} players + {BotCount} bots in old Area {OldArea}",
                    oldAreaSessions.Count, oldAreaBots.Count, oldArea);
            }

            // 2. 새 Area의 플레이어들에게 진입 알림 (내 최신 Cell 포함)
            if (newArea != AreaType.None)
            {
                var newAreaSessions = GetSessionsInArea(allSessions, newArea);
                var myCell = _lastValidatedPosition != null
                    ? WorldPositionToCell(_lastValidatedPosition)
                    : playerInfo.ObjectInfo.Cell;
                using var enterPacket = PacketMaker.G_TO_C_AREA_PLAYER_ENTER(playerInfo, myCell);

                foreach (var session in newAreaSessions) session.Send(enterPacket);

                Logger.LogDebug("Sent ENTER to {Count} players in new Area {NewArea}", newAreaSessions.Count, newArea);

                // 3. 나에게 새 Area의 다른 플레이어 정보 전송 (세션의 최신 Cell 사용)
                foreach (var session in newAreaSessions)
                {
                    if (!session.PlayerId.HasValue) continue;

                    var otherPlayerInfo = await PlayerInfo.Load(CacheHelper, session.PlayerId.Value);
                    if (otherPlayerInfo != null)
                    {
                        ApplyLivePlayerInfoSnapshot(session, otherPlayerInfo);

                        // 세션의 최신 위치에서 Cell 계산 (없으면 캐시된 Cell 사용)
                        var otherCell = session._lastValidatedPosition != null
                            ? WorldPositionToCell(session._lastValidatedPosition)
                            : otherPlayerInfo.ObjectInfo.Cell;

                        Logger.LogInformation("Sending Player {OtherId} to Player {MyId}: Cell=({CellX},{CellY})",
                            session.PlayerId, PlayerId, otherCell.X, otherCell.Y);

                        using var otherEnterPacket = PacketMaker.G_TO_C_AREA_PLAYER_ENTER(otherPlayerInfo, otherCell);
                        Send(otherEnterPacket);
                    }
                }

                Logger.LogDebug("Sent {Count} existing players to Player {PlayerId}", newAreaSessions.Count, PlayerId);

                // 4. #125: 새 Area의 봇들 ENTER도 나에게 전송 (실제 플레이어 동등)
                var newAreaBots = _botPlayerManager.GetBots(CurrentMapSubId)
                    .Where(b => !b.IsEliminated && b.CurrentArea == newArea)
                    .ToList();
                foreach (var bot in newAreaBots)
                {
                    var botInfo = _botPlayerManager.SynthesizePlayerInfo(CurrentMapSubId, bot.PlayerId);
                    if (botInfo == null) continue;
                    using var botEnterPacket = PacketMaker.G_TO_C_AREA_PLAYER_ENTER(botInfo, bot.Cell);
                    Send(botEnterPacket);
                }
                if (newAreaBots.Count > 0)
                    Logger.LogDebug("Sent {Count} bots in new Area {NewArea} to Player {PlayerId}",
                        newAreaBots.Count, newArea, PlayerId);

                // 5. 나에게 새 Area의 Interactable 목록 전송
                SendInteractableList(newArea);
                SendGroundItemSnapshot(newArea);
                SendMonsterSnapshot(newArea);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "HandleAreaChange error for player {PlayerId}", PlayerId);
        }
    }

    private void SendInteractableList(AreaType areaType)
    {
        var objects = _interactableStateManager.GetAreaObjectStates(CurrentMapSubId, areaType);

        // #229 5단계: 스웜은 상자 탐색을 보내지 않는다 — 마커도 빈 상호작용 UI도 뜰 일이 없다.
        // 단 문 잠금해제(door_id > 0)는 예외다. 방을 여는 유일한 수단이라 스웜의 핵심 조작이다.
        if (Config.IsSwarmExploreDisabled())
            objects = objects.Where(state => IsDoorUnlockInteractable(state.InteractId)).ToList();

        // 이미 열린 문의 마커는 보내지 않는다 — 열린 문 앞에서 게이지가 도는 그림은 거짓말이다.
        objects = objects
            .Where(state => GameInteractableData.Get(state.InteractId) is not { DoorId: > 0 } info ||
                            !_doorStateManager.IsDoorOpen(CurrentMapSubId, info.DoorId))
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

        // 청크로 분할하여 전송 (패킷 크기 제한)
        const int chunkSize = 3;
        for (int i = 0; i < objects.Count; i += chunkSize)
        {
            var chunk = objects.Skip(i).Take(chunkSize).ToList();
            bool isEnd = i + chunkSize >= objects.Count;
            using var packet = PacketMaker.G_TO_C_INTERACTABLE_LIST(areaType, chunk, isEnd);
            Send(packet);
        }

        Logger.LogDebug(
            "Sent {Count} interactable objects for area {AreaType} to Player {PlayerId} (MatchingId={MatchingId})",
            objects.Count, areaType, PlayerId, CurrentMapSubId);
    }

}
