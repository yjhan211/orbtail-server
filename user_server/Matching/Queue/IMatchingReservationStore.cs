namespace user_server.matching.queue;

/// <summary>
///     Owns the atomic Redis transition that reserves one matching-queue entry
///     for a matching worker reservation.
/// </summary>
public interface IMatchingReservationStore
{
    /// <summary>
    ///     Creates the player's expiring reservation only while the exact serialized queue entry
    ///     still exists. Returns <see langword="false"/> when the entry disappeared or
    ///     another worker already owns the reservation.
    /// </summary>
    public Task<bool> TryReserveQueueEntryAsync(
        byte[] queueEntry,
        long playerId,
        string reservationId,
        TimeSpan expiry);
}
