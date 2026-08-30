using System;
using System.Linq;
using game_server.services;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.network;

/// <summary>
///     구역 이동 처리 (GDD v0.0.8: 문/계단 마커 방식)
///     클라이언트가 마커 클릭 → 오토무브 도착 시 C_TO_G_AREA_MOVE 전송 →
///     서버가 인접/스태미나 검증 후 목적지 스폰 셀과 함께 G_TO_C_AREA_MOVE_RESULT 응답.
/// </summary>
public partial class GameClientSession
{

    private async Task HandleAreaMove(C_TO_G_AREA_MOVE msg)
    {
        if (PlayerId == null) return;
        if (IsEliminated)
        {
            LogAreaMoveError(ErrorCode.PLAYER_DEAD, msg.TargetArea);
            SendAreaMoveError(ErrorCode.PLAYER_DEAD, msg.TargetArea);
            return;
        }

        if (IsRoundActionLocked(out _))
        {
            LogAreaMoveError(ErrorCode.INVALID_GAME_STATE, msg.TargetArea);
            SendAreaMoveError(ErrorCode.INVALID_GAME_STATE, msg.TargetArea);
            return;
        }

        if (CurrentState == PlayerState.Exploring)
        {
            LogAreaMoveError(ErrorCode.INVALID_GAME_STATE, msg.TargetArea);
            SendAreaMoveError(ErrorCode.INVALID_GAME_STATE, msg.TargetArea);
            return;
        }

        if (_isSleeping)
        {
            LogAreaMoveError(ErrorCode.INVALID_GAME_STATE, msg.TargetArea);
            SendAreaMoveError(ErrorCode.INVALID_GAME_STATE, msg.TargetArea);
            return;
        }

        // 1. 인접 그래프 검증
        if (!GameAreaConnectionData.IsAdjacent(CurrentMapId, CurrentArea, msg.TargetArea))
        {
            Logger.LogWarning(
                "Player {PlayerId} requested non-adjacent area move: {From} → {To}",
                PlayerId, CurrentArea, msg.TargetArea);
            LogAreaMoveError(ErrorCode.INVALID_AREA, msg.TargetArea);
            SendAreaMoveError(ErrorCode.INVALID_AREA, msg.TargetArea);
            return;
        }

        // 2. 연결 타입 검증 (요청 메타와 실제 그래프 일치 확인 — 변조 방지)
        var actualType = GameAreaConnectionData.GetConnectionType(CurrentMapId, CurrentArea, msg.TargetArea);
        if (actualType == ConnectionType.None || actualType != msg.ConnectionType)
        {
            Logger.LogWarning(
                "Player {PlayerId} ConnectionType mismatch: requested {Req}, actual {Act}",
                PlayerId, msg.ConnectionType, actualType);
            LogAreaMoveError(ErrorCode.INVALID_AREA, msg.TargetArea);
            SendAreaMoveError(ErrorCode.INVALID_AREA, msg.TargetArea);
            return;
        }

        // 3. 구역 이동 자체에는 스태미나 비용이 없다.
        const int staminaCost = 0;

        // 3. 연결별 서버 스폰 셀만 허용한다. 영역 중심 fallback은 변조 요청이 문을
        // 건너뛰어 이동하는 통로가 되므로 사용하지 않는다.
        var spawnCell = GameAreaConnectionData.GetSpawnCell(CurrentMapId, CurrentArea, msg.TargetArea,
            msg.StairSide);
        if (ReferenceEquals(spawnCell, null))
        {
            Logger.LogWarning(
                "Player {PlayerId} AreaMove failed: spawn cell not found, CurrentArea={CurrentArea}, RequestedArea={RequestedArea}, StairSide={StairSide}",
                PlayerId, CurrentArea, msg.TargetArea, msg.StairSide);
            LogAreaMoveError(ErrorCode.SERVER_INTERNAL_ERROR, msg.TargetArea);
            SendAreaMoveError(ErrorCode.SERVER_INTERNAL_ERROR, msg.TargetArea);
            return;
        }

        if (!TryValidateMarkerAreaTransition(msg.TargetArea, spawnCell, out var transitionError))
        {
            LogAreaMoveError(transitionError, msg.TargetArea);
            SendAreaMoveError(transitionError, msg.TargetArea);
            return;
        }

        // 6. 매치 내부 위치/구역은 세션이 권위다. Redis의 영구 PlayerInfo에는 저장하지 않는다.
        var spawnPos = CellToWorldPosition(spawnCell);
        float rotation = _lastValidatedRotation;

        // 7. 세션 상태 갱신 + Area 변경 후처리
        var oldArea = CurrentArea;
        CurrentArea = msg.TargetArea;
        _lastValidatedPosition = spawnPos;
        _lastValidCell = spawnCell;

        Logger.LogInformation(
            "Player {PlayerId} AreaMove: {From} → {To} (cost {Cost}, type {Type})",
            PlayerId, oldArea, msg.TargetArea, staminaCost, actualType);

        // HandleAreaChange는 Movement에 정의됨 — 폐쇄 알림, 동선 추적 등 공통 처리
        _gameEventLogManager.LogMove(CurrentMapSubId, PlayerId.Value,
            oldArea.ToString(), msg.TargetArea.ToString(), isBot: false);

        await HandleAreaChange(oldArea, msg.TargetArea);

        // 9. 응답 (요청자에게만 — 다른 플레이어는 G_TO_C_MOVE 브로드캐스트로 위치 동기화)
        using var resultPacket = PacketMaker.G_TO_C_AREA_MOVE_RESULT(
            ErrorCode.SUCCESS,
            msg.TargetArea,
            spawnCell.X,
            spawnCell.Y,
            staminaCost,
            Stamina);
        Send(resultPacket);

        // 10. 위치 텔레포트 브로드캐스트 (같은 새 Area의 플레이어들에게)
        long serverTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var movePacket = PacketMaker.G_TO_C_MOVE(
            PlayerId.Value,
            spawnPos,
            new Vector3f(0f, 0f, 0f),
            rotation,
            spawnCell,
            lastProcessedInput: 0u,
            serverTimestamp,
            OrbOrbitPhaseDegrees);

        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var sameAreaSessions = GetSessionsInArea(allSessions, msg.TargetArea);
        foreach (var session in sameAreaSessions) session.Send(movePacket);
    }

    private bool TryValidateMarkerAreaTransition(AreaType targetArea, Cell targetSpawnCell,
        out ErrorCode errorCode)
    {
        errorCode = ErrorCode.INVALID_AREA;
        if (_lastValidCell is not { } currentCell)
        {
            Logger.LogWarning("Player {PlayerId} requested area move without a validated source cell", PlayerId);
            return false;
        }


        var door = GameDoorData.GetDoorForTransition(CurrentArea, targetArea, currentCell, targetSpawnCell);
        if (door == null || GameDoorData.IsOutsidePassageRadius(door, currentCell))
        {
            Logger.LogWarning(
                "Player {PlayerId} requested area move away from the transition: {From} → {To}, Cell=({X},{Y})",
                PlayerId, CurrentArea, targetArea, currentCell.X, currentCell.Y);
            return false;
        }

        if (!_doorStateManager.IsDoorOpen(CurrentMapSubId, door.DoorId))
        {
            Logger.LogWarning("Player {PlayerId} requested locked door transition: Door={DoorId}, {From} → {To}",
                PlayerId, door.DoorId, CurrentArea, targetArea);
            return false;
        }

        return true;
    }
    private void SendAreaMoveError(ErrorCode errorCode, AreaType requestedArea)
    {
        if (PlayerId == null) return;
        using var packet = PacketMaker.G_TO_C_AREA_MOVE_RESULT(
            errorCode,
            requestedArea,
            spawnCellX: 0,
            spawnCellY: 0,
            staminaCost: 0,
            remainingStamina: 0);
        Send(packet);
    }

    private void LogAreaMoveError(ErrorCode errorCode, AreaType requestedArea)
    {
        Logger.LogWarning(
            "Player {PlayerId} AreaMove failed: {ErrorCode}, CurrentArea={CurrentArea}, RequestedArea={RequestedArea}, State={State}, Sleeping={Sleeping}",
            PlayerId,
            errorCode,
            CurrentArea,
            requestedArea,
            CurrentState,
            _isSleeping);
    }

}
