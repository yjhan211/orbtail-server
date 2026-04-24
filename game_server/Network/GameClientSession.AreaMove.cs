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
            SendAreaMoveError(ErrorCode.PLAYER_DEAD, msg.TargetArea);
            return;
        }

        if (CurrentState == PlayerState.Exploring)
        {
            SendAreaMoveError(ErrorCode.INVALID_GAME_STATE, msg.TargetArea);
            return;
        }

        if (_isSleeping)
        {
            SendAreaMoveError(ErrorCode.INVALID_GAME_STATE, msg.TargetArea);
            return;
        }

        // 1. 인접 그래프 검증
        if (!GameAreaConnectionData.IsAdjacent(CurrentMapId, CurrentArea, msg.TargetArea))
        {
            Logger.LogWarning(
                "Player {PlayerId} requested non-adjacent area move: {From} → {To}",
                PlayerId, CurrentArea, msg.TargetArea);
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
            SendAreaMoveError(ErrorCode.INVALID_AREA, msg.TargetArea);
            return;
        }

        // 3. 이동 비용 산정 (Door=-3 인접, Stair=-1 엘리베이터)
        int staminaCost = actualType == ConnectionType.Stair
            ? GameServer.StairStaminaCost
            : GameServer.MoveStaminaCost;

        // 4. 스태미나 검증
        await using var playerLock = await PlayerInfo.Lock(RedLock, PlayerId.Value);
        var playerInfo = await PlayerInfo.Load(CacheHelper, PlayerId.Value);
        if (playerInfo == null)
        {
            SendAreaMoveError(ErrorCode.SERVER_INTERNAL_ERROR, msg.TargetArea);
            return;
        }

        if (playerInfo.Stamina < staminaCost)
        {
            Logger.LogInformation(
                "Player {PlayerId} 스태미나 부족 (요청 {Cost}, 보유 {Have})",
                PlayerId, staminaCost, playerInfo.Stamina);
            SendAreaMoveError(ErrorCode.INSUFFICIENT_STAMINA, msg.TargetArea);
            return;
        }

        // 5. 목적지 스폰 셀 조회
        var spawnCell = GameMapData.GetAreaSpawnCell(CurrentMapId, msg.TargetArea);

        // 6. PlayerInfo 갱신 (스태미나 차감 + 위치/구역 이동)
        playerInfo.Stamina -= staminaCost;
        playerInfo.ObjectInfo.Cell = spawnCell;
        var spawnPos = CellToWorldPosition(spawnCell);
        playerInfo.ObjectInfo.Position = spawnPos;
        playerInfo.ObjectInfo.Velocity = new Vector3f(0f, 0f, 0f);
        playerInfo.ObjectInfo.MoveTimestamp = DateTime.UtcNow;
        await playerInfo.Save(CacheHelper);

        // 7. 세션 상태 갱신 + Area 변경 후처리
        var oldArea = CurrentArea;
        _previousArea = oldArea;
        CurrentArea = msg.TargetArea;
        _lastValidatedPosition = spawnPos;

        Logger.LogInformation(
            "Player {PlayerId} AreaMove: {From} → {To} (cost {Cost}, type {Type})",
            PlayerId, oldArea, msg.TargetArea, staminaCost, actualType);

        // HandleAreaChange는 Movement에 정의됨 — 폐쇄 알림, 동선 추적 등 공통 처리
        await HandleAreaChange(oldArea, msg.TargetArea);

        // 8. 응답 (요청자에게만 — 다른 플레이어는 G_TO_C_MOVE 브로드캐스트로 위치 동기화)
        using var resultPacket = PacketMaker.G_TO_C_AREA_MOVE_RESULT(
            ErrorCode.SUCCESS,
            msg.TargetArea,
            spawnCell.X,
            spawnCell.Y,
            staminaCost,
            playerInfo.Stamina);
        Send(resultPacket);

        // 9. 위치 텔레포트 브로드캐스트 (같은 새 Area의 플레이어들에게)
        long serverTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var movePacket = PacketMaker.G_TO_C_MOVE(
            PlayerId.Value,
            spawnPos,
            new Vector3f(0f, 0f, 0f),
            playerInfo.ObjectInfo.Rotation,
            spawnCell,
            lastProcessedInput: 0u,
            serverTimestamp);

        var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
        var sameAreaSessions = GetSessionsInArea(allSessions, msg.TargetArea);
        foreach (var session in sameAreaSessions) session.Send(movePacket);
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
}
