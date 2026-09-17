using game_server.matches;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;

namespace game_server.players;

/// <summary>
///     수신 간격에 따른 이동 한계와 지형·문 통과를 검증하고, 승인된 이동을 플레이어와 매치에 반영한다.
///     전송할 공간 정보를 생성하며 패킷 전송은 세션이 담당한다.
/// </summary>
internal sealed class PlayerMovementService(ILogger<PlayerMovementService> logger)
{
    private const float MaximumReceiptDeltaSeconds = 0.25f;
    public const float InitialReceiptDeltaSeconds = 0.05f;
    public const float MaximumSpeedUnitsPerSecond = 10f;

    public static bool IsFinite(Vector3f value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    public static float ClampMoveDeltaTime(double elapsedSeconds)
    {
        if (double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds))
        {
            return InitialReceiptDeltaSeconds;
        }
        return Math.Clamp((float)elapsedSeconds, 0f, MaximumReceiptDeltaSeconds);
    }

    /// <summary>플레이어별 수신 시각을 갱신하고 이동 검증에 사용할 경과 시간을 제한한다.</summary>
    public float CalculateMoveDeltaTime(Player player, long timestamp)
    {
        long previous = player.LastMoveProcessedTimestamp;
        player.LastMoveProcessedTimestamp = timestamp;
        if (previous == 0)
        {
            return InitialReceiptDeltaSeconds;
        }
        double elapsedSeconds = (timestamp - previous) / (double)System.Diagnostics.Stopwatch.Frequency;
        return ClampMoveDeltaTime(elapsedSeconds);
    }

    private static Vector3f ClampVelocity(Vector3f velocity)
    {
        float speed = velocity.Magnitude();
        return speed > MaximumSpeedUnitsPerSecond ? velocity.Normalized() * MaximumSpeedUnitsPerSecond : velocity;
    }

    public bool ProcessMovement(MatchRuntime match, Player player, C_TO_G_MOVE msg, float deltaTime)
    {
        if (!Monitor.IsEntered(match.MatchLock))
        {
            throw new InvalidOperationException("Movement processing requires the match lock.");
        }

        var mapId = Config.SWARM_MATCH_MAP;
        var lastPosition = player.Position;
        var lastCell = player.Cell;
        var position = new Vector3f(msg.Position.X, msg.Position.Y, 0f);
        bool requiresClientCorrection = false;

        if (lastPosition != null)
        {
            var delta = position - lastPosition;
            float maxDistance = MaximumSpeedUnitsPerSecond * deltaTime;
            if (delta.Magnitude() > maxDistance)
            {
                var direction = delta.Normalized();
                position = new Vector3f(lastPosition.X + direction.X * maxDistance, lastPosition.Y + direction.Y * maxDistance, 0);
                requiresClientCorrection = true;
            }
        }

        var cell = MapCoordinateConverter.WorldToCell(mapId, position);
        bool destinationBlocked = !GameMapData.IsMoveablePosition(mapId, cell);
        bool pathBlocked = !destinationBlocked && lastPosition != null && lastCell != null && !MapTraversal.IsTraversable(lastCell, cell, candidate => GameMapData.IsMoveablePosition(mapId, candidate));
        bool blocked = destinationBlocked || pathBlocked;
        if (blocked)
        {
            logger.LogWarning("Player {PlayerId} blocked movement: DestinationBlocked={DestinationBlocked}, PathBlocked={PathBlocked}", player.PlayerId, destinationBlocked, pathBlocked);
            position = lastPosition ?? position;
            cell = MapCoordinateConverter.WorldToCell(mapId, position);
            requiresClientCorrection = true;
        }

        var velocity = ClampVelocity(msg.Velocity);
        if (blocked)
        {
            velocity = new Vector3f();
        }
        else if (lastPosition != null)
        {
            var acceptedDelta = position - lastPosition;
            velocity = deltaTime > 0f
                ? ClampVelocity(new Vector3f(acceptedDelta.X / deltaTime, acceptedDelta.Y / deltaTime, 0f))
                : new Vector3f();
        }

        var oldArea = player.CurrentArea;
        var newArea = GameMapData.GetCurrentArea(mapId, cell);
        bool areaChanged = newArea != oldArea && newArea != AreaType.None;
        if (areaChanged && match.Doors.GetBlockingDoor(oldArea, newArea, lastCell ?? cell, cell) is { } lockedDoor)
        {
            logger.LogWarning("Player {PlayerId} blocked crossing {CurrentArea}→{NewArea} (locked door: {DoorId})", player.PlayerId, oldArea, newArea, lockedDoor.DoorId);
            player.GameInfo.ObjectInfo.Velocity = new Vector3f();
            return true;
        }

        player.TryStopSleep();
        PlayerPickupService.AddReachableItemsForMovement(match, player, lastPosition ?? position, position, newArea);
        player.ApplyValidatedMovement(position, velocity, msg.Rotation);
        if (areaChanged)
        {
            logger.LogDebug("Player {PlayerId} Area change at Cell({CellX},{CellY}): {OldArea} → {NewArea}", player.PlayerId, cell.X, cell.Y, oldArea, newArea);
        }

        CancelDoorOpeningIfMoved(match, player);
        return requiresClientCorrection;
    }

    public static void CancelDoorOpeningIfMoved(MatchRuntime runtime, Player player)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Player movement completion requires the match lock.");
        }
        var velocity = player.GameInfo.ObjectInfo.Velocity;
        if (player.State == PlayerState.EXPLORE_1 && (velocity.X != 0f || velocity.Y != 0f))
        {
            player.Interactions.Cancel();
            player.State = PlayerState.IDLE;
        }
    }
}
