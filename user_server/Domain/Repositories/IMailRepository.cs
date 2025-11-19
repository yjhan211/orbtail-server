using network.common.data.models;

namespace user_server.domain.repositories;

/// <summary>
/// Repository interface for Mail data access
/// </summary>
public interface IMailRepository
{
    /// <summary>
    /// Loads mailbox for a player
    /// </summary>
    /// <param name="playerId">Player ID</param>
    /// <returns>Mailbox or null if not found</returns>
    Task<MailBox?> LoadAsync(long playerId);

    /// <summary>
    /// Saves mailbox to cache
    /// </summary>
    /// <param name="mailBox">Mailbox to save</param>
    Task SaveAsync(MailBox mailBox);

    /// <summary>
    /// Gets a specific mail by UID
    /// </summary>
    /// <param name="playerId">Player ID</param>
    /// <param name="mailUid">Mail UID</param>
    /// <returns>Mail info or null if not found</returns>
    Task<MailInfo?> GetMailAsync(long playerId, long mailUid);
}
