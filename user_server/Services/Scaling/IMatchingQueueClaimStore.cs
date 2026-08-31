namespace user_server.services.scaling;

/// <summary>
///     Owns the atomic Redis transition that reserves one matching-queue entry
///     for a matching worker claim.
/// </summary>
public interface IMatchingQueueClaimStore
{
    /// <summary>
    ///     Creates the player's expiring claim only while the exact serialized queue entry
    ///     still exists. Returns <see langword="false"/> when the entry disappeared or
    ///     another worker already owns the claim.
    /// </summary>
    public Task<bool> TryClaimQueueEntryAsync(
        byte[] queueEntry,
        long playerId,
        string claimId,
        TimeSpan expiry);
}
