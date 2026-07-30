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
            // 클라이언트 측 timestamp로 deltaTime 산출 — 서버 도착 클러스터링/지연 영향 제거.
            // 첫 패킷이거나 시계가 뒤로 갔으면 0으로 처리 (ValidatePosition이 deltaTime>0 조건으로 검증 스킵).
            float deltaTime;
            if (_lastClientMoveTimestamp == 0 || msg.ClientTimestamp <= _lastClientMoveTimestamp)
                deltaTime = 0f;
            else
                deltaTime = (msg.ClientTimestamp - _lastClientMoveTimestamp) / 1000f;
            _lastClientMoveTimestamp = msg.ClientTimestamp;
            _lastMoveTime = now;

            // 1. 클라이언트 Position 검증
            if (msg.Position == null! || msg.Velocity == null!)
            {
                Logger.LogWarning("Player {PlayerId} HandleMove: null Position/Velocity", PlayerId);
                return;
            }

            var validatedPosition = ValidatePosition(
                msg.Position,
                msg.Velocity,
                deltaTime,
                out bool requiresClientCorrection);

            // 2. Area 변경 시 퇴장 조건 체크 (치팅 방지)
            var currentCell = WorldPositionToCell(validatedPosition);
            var newArea = GameMapData.GetCurrentArea(CurrentMapId, currentCell);


            // 3. Area 변경 처리 (퇴장 조건 통과한 경우만)
            long serverTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // 셀이 어떤 영역에도 속하지 않으면 (영역 경계 바로 밖 등) CurrentArea를 None으로 덮어쓰지 않음
            // — 일시적 None 상태에서 마커 클릭 시 IsAdjacent(None, X) = false로 INVALID_AREA 거절되는 문제 방지
            if (newArea != CurrentArea && newArea != AreaType.None)
            {
                // 가장 가까운 문 기준으로 잠김 체크 (클라이언트는 이미 막고 있음, 서버는 보정 역할)
                // 1. 진입하려는 영역의 가장 가까운 문이 잠겨있으면 차단
                var entryBlockedDoor =
                    _doorStateManager.GetBlockingDoorForArea(CurrentMapSubId, newArea, currentCell.X, currentCell.Y);
                if (entryBlockedDoor != null)
                {
                    Logger.LogWarning("Player {PlayerId} blocked entering area {NewArea} (locked door: {DoorId})",
                        PlayerId, newArea, entryBlockedDoor.DoorId);

                    // 진입 차단: fallback (밖쪽)으로 보정
                    var fallbackCell = new Cell(entryBlockedDoor.FallbackCellX, entryBlockedDoor.FallbackCellY);
                    SendAreaExitBlocked(newArea, fallbackCell);
                    return;
                }

                // 2. 현재 영역의 가장 가까운 문이 잠겨있으면 퇴장 차단
                var exitBlockedDoor =
                    _doorStateManager.GetBlockingDoorForArea(CurrentMapSubId, CurrentArea, currentCell.X,
                        currentCell.Y);
                if (exitBlockedDoor != null)
                {
                    Logger.LogWarning("Player {PlayerId} blocked exiting area {CurrentArea} (locked door: {DoorId})",
                        PlayerId, CurrentArea, exitBlockedDoor.DoorId);

                    // 퇴장 차단: position (안쪽)으로 보정
                    var positionCell = new Cell((int)exitBlockedDoor.PositionX, (int)exitBlockedDoor.PositionY);
                    SendAreaExitBlocked(newArea, positionCell);
                    return;
                }
            }

            // 잠긴 문 검증이 끝난 뒤 위치를 게시한다. 전투 타이머는 아래 지역 갱신까지의
            // 짧은 불일치 구간을 좌표에서 재계산한 지역과 비교해 제외한다.
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
                await HandleAreaChange(oldArea, newArea);
            }


            TrySendCorridorEncounterEvents(validatedPosition);
            TrySendRoomEncounterEventsFromVision(validatedPosition);

            // 5. 브로드캐스트 (같은 Area의 플레이어에게만 전송)
            using var packet = PacketMaker.G_TO_C_MOVE(
                PlayerId.Value,
                validatedPosition,
                msg.Velocity,
                msg.Rotation,
                currentCell,
                msg.InputSequence,
                serverTimestamp
            );

            var otherSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
            var sameAreaSessions = GetSessionsInArea(otherSessions, CurrentArea);

            foreach (var session in sameAreaSessions)
            {
                if (session.PlayerId != PlayerId)
                    session.Send(packet);
            }

            if (requiresClientCorrection)
                Send(packet);
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
        out bool requiresClientCorrection)
    {
        requiresClientCorrection = false;

        const float maxSpeed = 10f; // 최대 속도 (units/s)
        const float tolerance = 1.5f; // 허용 오차 (50%)

        // clientPos, velocity는 호출 전에 null 체크 완료

        // Z값은 항상 0으로 고정
        clientPos.Z = 0;

        // 1. 속도 제한 체크
        float speed = velocity.Magnitude();
        if (speed > maxSpeed)
        {
            Logger.LogWarning("Player {PlayerId} 속도 초과: {Speed:F2} > {MaxSpeed}", PlayerId, speed, maxSpeed);
            // 클라이언트 위치를 신뢰하지 않고 서버 계산 위치 사용
            if (_lastValidatedPosition != null)
            {
                var correctedVelocity = velocity.Normalized() * maxSpeed;
                clientPos = new Vector3f(
                    _lastValidatedPosition.X + correctedVelocity.X * deltaTime,
                    _lastValidatedPosition.Y + correctedVelocity.Y * deltaTime,
                    0
                );
                requiresClientCorrection = true;
            }
        }

        // 2. 이동 거리 검증 (텔레포트 방지)
        if (_lastValidatedPosition != null && deltaTime > 0)
        {
            var delta = clientPos - _lastValidatedPosition;
            float distance = delta.Magnitude();
            float maxDistance = maxSpeed * deltaTime * tolerance;

            if (distance > maxDistance)
            {
                // The first packet may calibrate the client spawn position, but it must
                // still pass the same wall-path validation below.
                if (!_hasFirstMoveCalibrated)
                {
                    _hasFirstMoveCalibrated = true;
                    Logger.LogInformation(
                        "Player {PlayerId} is calibrating the initial position before path validation: distance={Distance:F2}",
                        PlayerId, distance);
                }
                else
                {
                    Logger.LogWarning(
                        "Player {PlayerId} teleport detected: distance={Distance:F2}, maxAllowed={MaxDistance:F2}",
                        PlayerId, distance, maxDistance);
                    clientPos = new Vector3f(
                        _lastValidatedPosition.X + velocity.X * deltaTime,
                        _lastValidatedPosition.Y + velocity.Y * deltaTime,
                        0
                    );
                    requiresClientCorrection = true;
                }
            }

        }

        // 3. Cell 기반 이동 가능 여부 검증 (맵 밖 이탈 방지)
        var clientCell = WorldPositionToCell(clientPos);
        if (!GameMapData.IsMoveablePosition(CurrentMapId, clientCell))
        {
            // 장애물 셀 접촉: 서버 위치만 유지하고 클라이언트에는 보정 패킷을 보내지 않음
            if (_lastValidatedPosition is not null && _lastValidCell is not null)
            {
                Logger.LogWarning(
                    "Player {PlayerId} 이동 불가 위치 감지: ClientPos=({CX:F2},{CY:F2}), Cell=({CellX},{CellY}), 보정 → ({VX:F2},{VY:F2})",
                    PlayerId, clientPos.X, clientPos.Y, clientCell.X, clientCell.Y,
                    _lastValidatedPosition.X, _lastValidatedPosition.Y);
                return _lastValidatedPosition;
            }

            // 마지막 유효 위치가 없으면 (첫 이동) 클라이언트 위치 그대로 사용 (초기 스폰 위치 신뢰)
            Logger.LogWarning(
                "Player {PlayerId} 이동 불가 위치 감지 (첫 이동): ClientPos=({CX:F2},{CY:F2}), Cell=({CellX},{CellY})",
                PlayerId, clientPos.X, clientPos.Y, clientCell.X, clientCell.Y);
        }

        // 4. 검증 통과: 유효 위치 업데이트
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
            return _lastValidatedPosition;
        }

        _lastValidCell = clientCell;

        // 검증 통과: 클라이언트 Position 사용
        return clientPos;
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

                // 6. 사보타주 이벤트 트리거 (해당 Area 최초 진입 시)
                _sabotageManager.OnPlayerEnterArea(CurrentMapSubId, newArea);
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
