using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;

namespace game_server.services;

/// <summary>
///     서버 수신 간격으로 이동 거리·속도를 제한하고 목적 셀·이동 경로·구역 사이의 잠긴 문을 검사한다.
///     세션 상태를 변경하지 않고 검증 결과만 반환한다. 입력 순번·수신 시각·최종 위치 반영은 세션이 맡는다.
/// </summary>
internal sealed class MovementValidationService(ILogger<MovementValidationService> logger)
{
    public ValidatedMovement ValidatePosition(
        long playerId,
        MapId mapId,
        Vector3f? lastPosition,
        Cell? lastValidCell,
        Vector3f clientPos,
        Vector3f velocity,
        float deltaTime)
    {
        bool requiresClientCorrection = false;
        Vector3f validatedVelocity = MovementValidationPolicy.ClampVelocity(velocity);

        const float maxSpeed = MovementValidationPolicy.MaximumSpeedUnitsPerSecond;

        // clientPos, velocity는 호출 전에 null 체크 완료

        // Z값은 항상 0으로 고정
        clientPos = new Vector3f(clientPos.X, clientPos.Y, 0f);

        // 1. 속도 제한 체크
        float speed = velocity.Magnitude();
        if (speed > maxSpeed)
        {
            logger.LogWarning("Player {PlayerId} 속도 초과: {Speed:F2} > {MaxSpeed}", playerId, speed, maxSpeed);
            if (lastPosition != null)
            {
                clientPos = new Vector3f(
                    lastPosition.X + validatedVelocity.X * deltaTime,
                    lastPosition.Y + validatedVelocity.Y * deltaTime,
                    0
                );
                requiresClientCorrection = true;
            }
        }

        // 2. 서버가 관측한 시간 안에 가능한 거리만 허용한다. 클라이언트 timestamp는
        // 이 예산을 늘릴 수 없으며, 첫 이동도 서버가 세션에 저장한 스폰 위치에서 시작한다.
        if (lastPosition != null)
        {
            var delta = clientPos - lastPosition;
            float distance = delta.Magnitude();
            float maxDistance = maxSpeed * deltaTime;

            if (distance > maxDistance)
            {
                logger.LogWarning(
                    "Player {PlayerId} teleport detected: distance={Distance:F2}, maxAllowed={MaxDistance:F2}",
                    playerId, distance, maxDistance);
                var direction = delta.Normalized();
                clientPos = new Vector3f(
                    lastPosition.X + direction.X * maxDistance,
                    lastPosition.Y + direction.Y * maxDistance,
                    0
                );
                validatedVelocity = direction * maxSpeed;
                requiresClientCorrection = true;
            }
        }

        // 3. Cell 기반 이동 가능 여부 검증 (맵 밖 이탈 방지)
        var clientCell = MapCoordinateConverter.WorldToCell(mapId, clientPos);
        if (!GameMapData.IsMoveablePosition(mapId, clientCell))
        {
            if (lastPosition is not null && lastValidCell is not null)
            {
                logger.LogWarning(
                    "Player {PlayerId} 이동 불가 위치 감지: ClientPos=({CX:F2},{CY:F2}), Cell=({CellX},{CellY}), 보정 → ({VX:F2},{VY:F2})",
                    playerId, clientPos.X, clientPos.Y, clientCell.X, clientCell.Y,
                    lastPosition.X, lastPosition.Y);
                requiresClientCorrection = true;
                validatedVelocity = new Vector3f(0f, 0f, 0f);
                return new(lastPosition, validatedVelocity, lastValidCell, requiresClientCorrection);
            }

            logger.LogWarning(
                "Player {PlayerId} 이동 불가 위치 감지 (초기 위치): ClientPos=({CX:F2},{CY:F2}), Cell=({CellX},{CellY})",
                playerId, clientPos.X, clientPos.Y, clientCell.X, clientCell.Y);
            requiresClientCorrection = true;
            validatedVelocity = new Vector3f(0f, 0f, 0f);
            return new(lastPosition ?? clientPos, validatedVelocity, lastValidCell, requiresClientCorrection);
        }

        // 4. 중간 벽 셀을 건너뛰는 이동 차단
        if (lastPosition is not null && lastValidCell is not null &&
            !GridMovementTraversal.IsTraversable(
                lastValidCell,
                clientCell,
                candidate => GameMapData.IsMoveablePosition(mapId, candidate)))
        {
            logger.LogWarning(
                "Player {PlayerId} attempted to cross an impassable cell: From=({FromX},{FromY}), To=({ToX},{ToY})",
                playerId, lastValidCell.X, lastValidCell.Y, clientCell.X, clientCell.Y);
            requiresClientCorrection = true;
            validatedVelocity = new Vector3f(0f, 0f, 0f);
            return new(lastPosition, validatedVelocity, lastValidCell, requiresClientCorrection);
        }

        // 원본 속도가 아니라 서버가 승인한 위치 변화에서 원격 표시용 속도를 산출한다.
        if (lastPosition is not null && deltaTime > 0f)
        {
            var acceptedDelta = clientPos - lastPosition;
            validatedVelocity = MovementValidationPolicy.ClampVelocity(new Vector3f(
                acceptedDelta.X / deltaTime,
                acceptedDelta.Y / deltaTime,
                0f));
        }
        lastValidCell = clientCell;

        // 검증 통과: 클라이언트 Position 사용
        return new(clientPos, validatedVelocity, lastValidCell, requiresClientCorrection);
    }

    public Cell? GetBlockedTransitionCell(
        long playerId,
        AreaType currentArea,
        AreaType newArea,
        Cell previousCell,
        Cell currentCell,
        MatchDoorState? doors)
    {
        if (newArea == currentArea || newArea == AreaType.None)
            return null;

        var transitionDoor = GameDoorData.GetDoorForTransition(currentArea, newArea, previousCell, currentCell);
        if (transitionDoor == null || doors?.IsDoorOpen(transitionDoor.DoorId) == true)
            return null;

        logger.LogWarning(
            "Player {PlayerId} blocked crossing {CurrentArea}→{NewArea} (locked door: {DoorId})",
            playerId, currentArea, newArea, transitionDoor.DoorId);
        return transitionDoor.AreaType == currentArea
            ? new Cell((int)transitionDoor.PositionX, (int)transitionDoor.PositionY)
            : new Cell(transitionDoor.FallbackCellX, transitionDoor.FallbackCellY);
    }
}

/// <summary>세션이 문 통과까지 확인한 뒤 반영할 위치·속도·셀과 보정 필요 여부.</summary>
internal readonly record struct ValidatedMovement(
    Vector3f Position, Vector3f Velocity, Cell? ValidCell, bool RequiresCorrection);
