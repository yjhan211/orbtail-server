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
internal sealed class PlayerMovementService(
    ILogger<PlayerMovementService> logger)
{
    private const float MinimumReceiptDeltaSeconds = 0f;
    private const float MaximumReceiptDeltaSeconds = 0.25f;
    public const float InitialReceiptDeltaSeconds = 0.05f;
    public const float MaximumSpeedUnitsPerSecond = 10f;
    public const float MovementAcknowledgementIntervalSeconds = 0.25f;

    public static bool IsFinite(Vector3f value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    public static float ClampMoveDeltaTime(double elapsedSeconds)
    {
        if (double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds))
        {
            return InitialReceiptDeltaSeconds;
        }
        return Math.Clamp((float)elapsedSeconds, MinimumReceiptDeltaSeconds, MaximumReceiptDeltaSeconds);
    }

    private static Vector3f ClampVelocity(Vector3f velocity)
    {
        float speed = velocity.Magnitude();
        return speed > MaximumSpeedUnitsPerSecond ? velocity.Normalized() * MaximumSpeedUnitsPerSecond : velocity;
    }

    public MovementResult ProcessMovement(MatchRuntime match, Player player, C_TO_G_MOVE msg, float deltaTime)
    {
        if (!Monitor.IsEntered(match.MatchLock))
        {
            throw new InvalidOperationException("Movement processing requires the match lock.");
        }

        long playerId = player.PlayerId;
        var mapId = Config.SWARM_MATCH_MAP;
        var lastPosition = player.Position;
        var lastValidCell = player.Cell;
        var clientPos = msg.Position;
        var velocity = msg.Velocity;
        bool requiresClientCorrection = false;
        var validatedVelocity = ClampVelocity(velocity);
        clientPos = new Vector3f(clientPos.X, clientPos.Y, 0f);

        float speed = velocity.Magnitude();
        if (speed > MaximumSpeedUnitsPerSecond)
        {
            if (lastPosition != null)
            {
                clientPos = new Vector3f(lastPosition.X + validatedVelocity.X * deltaTime, lastPosition.Y + validatedVelocity.Y * deltaTime, 0);
                requiresClientCorrection = true;
            }
        }

        if (lastPosition != null)
        {
            var delta = clientPos - lastPosition;
            float distance = delta.Magnitude();
            float maxDistance = MaximumSpeedUnitsPerSecond * deltaTime;

            if (distance > maxDistance)
            {
                var direction = delta.Normalized();
                clientPos = new Vector3f(lastPosition.X + direction.X * maxDistance, lastPosition.Y + direction.Y * maxDistance, 0);
                validatedVelocity = direction * MaximumSpeedUnitsPerSecond;
                requiresClientCorrection = true;
            }
        }

        var clientCell = MapCoordinateConverter.WorldToCell(mapId, clientPos);
        bool destinationBlocked = !GameMapData.IsMoveablePosition(mapId, clientCell);
        bool pathBlocked = !destinationBlocked && lastPosition != null && lastValidCell != null && !GridMovementTraversal.IsTraversable(lastValidCell, clientCell, candidate => GameMapData.IsMoveablePosition(mapId, candidate));
        if (destinationBlocked || pathBlocked)
        {
            logger.LogWarning("Player {PlayerId} blocked movement: DestinationBlocked={DestinationBlocked}, PathBlocked={PathBlocked}", playerId, destinationBlocked, pathBlocked);
            clientPos = lastPosition ?? clientPos;
            validatedVelocity = new Vector3f();
            requiresClientCorrection = true;
        }
        else
        {
            if (lastPosition != null && deltaTime > 0f)
            {
                var acceptedDelta = clientPos - lastPosition;
                validatedVelocity = ClampVelocity(new Vector3f(acceptedDelta.X / deltaTime, acceptedDelta.Y / deltaTime, 0f));
            }
            else if (lastPosition != null)
            {
                validatedVelocity = new Vector3f();
            }
            lastValidCell = clientCell;
        }

        var validation = new ValidatedMovement(clientPos, validatedVelocity, lastValidCell, requiresClientCorrection);
        var validatedPosition = validation.Position;
        var currentCell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, validatedPosition);
        var oldArea = player.CurrentArea;
        var newArea = GameMapData.GetStableCurrentArea(Config.SWARM_MATCH_MAP, currentCell, oldArea);
        long serverTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var previousCell = player.Position != null ? MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, player.Position) : currentCell;

        Cell? blockedCell = null;
        if (newArea != oldArea && newArea != AreaType.None)
        {
            var transitionDoor = match.Doors.GetBlockingDoor(oldArea, newArea, previousCell, currentCell);
            if (transitionDoor != null)
            {
                logger.LogWarning("Player {PlayerId} blocked crossing {CurrentArea}→{NewArea} (locked door: {DoorId})", playerId, oldArea, newArea, transitionDoor.DoorId);
                blockedCell = transitionDoor.AreaType == oldArea
                    ? new Cell((int)transitionDoor.PositionX, (int)transitionDoor.PositionY)
                    : new Cell(transitionDoor.FallbackCellX, transitionDoor.FallbackCellY);
            }
        }
        if (blockedCell != null)
        {
            return new MovementResult(validation, currentCell, serverTimestamp, oldArea, newArea, blockedCell, false);
        }

        bool sleepStopped = player.TryStopSleep();
        player.AdvanceOrbOrbit(validatedPosition);
        var pickupArea = newArea == AreaType.None ? oldArea : newArea;
        PlayerPickupService.AddReachableItemsForMovement(match, player, player.Position ?? validatedPosition, validatedPosition, pickupArea);
        player.ApplyValidatedMovement(validation, msg.Rotation);

        if (newArea != oldArea && newArea != AreaType.None)
        {
            logger.LogInformation("Player {PlayerId} Area change at Cell({CellX},{CellY}): {OldArea} → {NewArea}", player.PlayerId, currentCell.X, currentCell.Y, oldArea, newArea);
            player.CurrentArea = newArea;
        }

        return new MovementResult(validation, currentCell, serverTimestamp, oldArea, player.CurrentArea, null, sleepStopped);
    }

    internal readonly record struct MovementResult(
        ValidatedMovement Movement, Cell Cell, long ServerTimestamp,
        AreaType OldArea, AreaType NewArea, Cell? BlockedCell, bool SleepStopped);

    internal readonly record struct ValidatedMovement(
        Vector3f Position, Vector3f Velocity, Cell? ValidCell, bool RequiresCorrection);
}
