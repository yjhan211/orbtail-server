
namespace user_server.accounts;

public sealed record AccountCredential(long PlayerId, string TokenHash);

public enum AccountCredentialProvisionStatus
{
    Created,
    Existing,
    PlayerNotFound,
    PlayerAlreadyExists,
    TokenCollision
}

public sealed record AccountCredentialProvisionResult(
    AccountCredentialProvisionStatus Status,
    string? AccountToken = null);

public interface IAccountCredentialStore
{
    public Task<long> AllocatePlayerIdAsync();
    public Task<AccountCredential?> FindByTokenHashAsync(string tokenHash);
    public Task<AccountCredentialProvisionResult> ProvisionAsync(
        long playerId,
        string proposedToken,
        string proposedTokenHash,
        bool requireExistingPlayer);
}
