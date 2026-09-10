using game_server.items;
using game_server.logging;
using game_server.matches;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.players;

internal sealed class PlayerMovementService(
    MovementValidationService validationService,
    GameEventLogManager eventLog,
    ILogger logger)
{
    public GameObjectInfo CreateGameObjectInfo(MatchRuntime match, Player player, PlayerState state)
    {
        if (!Monitor.IsEntered(match.MatchLock))
        {
            throw new InvalidOperationException("Movement snapshots require the match lock.");
        }

        var position = player.Position ?? throw new InvalidOperationException("Cannot publish a player before its spawn is initialized.");
        var cell = player.Cell ??  MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, position);
        return new GameObjectInfo(ObjectType.PLAYER, player.PlayerId, Config.SWARM_MATCH_MAP, match.MatchingId, cell)
        {
            Position = new Vector3f(position.X, position.Y, position.Z),
            Velocity = new Vector3f(player.Velocity.X, player.Velocity.Y, player.Velocity.Z),
            Rotation = player.Rotation,
            State = state
        };
    }

    public MovementResult ProcessMovement(MatchRuntime match, Player player, C_TO_G_MOVE msg, float deltaTime)
    {
        if (!Monitor.IsEntered(match.MatchLock))
        {
            throw new InvalidOperationException("Movement processing requires the match lock.");
        }

        var validation = validationService.ValidatePosition(player.PlayerId, Config.SWARM_MATCH_MAP, player.Position, player.Cell, msg.Position, msg.Velocity, deltaTime);
        var validatedPosition = validation.Position;
        var currentCell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, validatedPosition);
        var oldArea = player.CurrentArea;
        var newArea = GameMapData.GetStableCurrentArea(Config.SWARM_MATCH_MAP, currentCell, oldArea);
        long serverTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var previousCell = player.Position != null ? MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, player.Position) : currentCell;
        var blockedCell = validationService.GetBlockedTransitionCell(player.PlayerId, oldArea, newArea, previousCell, currentCell, match.Doors);
        if (blockedCell != null)
        {
            return new MovementResult(validation, currentCell, serverTimestamp, oldArea, newArea, blockedCell, false);
        }

        bool sleepStopped = player.TryStopSleep();
        player.AdvanceOrbOrbit(validatedPosition);
        var pickupArea = newArea == AreaType.None ? oldArea : newArea;
        GroundItemAutoPickupService.RecordMovement(match, player, player.Position ?? validatedPosition, validatedPosition, pickupArea);
        player.ApplyValidatedMovement(validation, msg.Rotation);
        match.GroundItems.ReleaseSourcePickupBlocks(player.PlayerId, pickupArea, validatedPosition.X, validatedPosition.Y);

        if (newArea != oldArea && newArea != AreaType.None)
        {
            logger.LogInformation("Player {PlayerId} Area change at Cell({CellX},{CellY}): {OldArea} → {NewArea}", player.PlayerId, currentCell.X, currentCell.Y, oldArea, newArea);
            player.CurrentArea = newArea;
            eventLog.LogMove(match.MatchingId, player.PlayerId, oldArea.ToString(), newArea.ToString(), isBot: player.PlayerId < 0);
        }

        return new MovementResult(validation, currentCell, serverTimestamp, oldArea, player.CurrentArea, null, sleepStopped);
    }

    internal readonly record struct MovementResult(
        ValidatedMovement Movement, Cell Cell, long ServerTimestamp,
        AreaType OldArea, AreaType NewArea, Cell? BlockedCell, bool SleepStopped);
}
