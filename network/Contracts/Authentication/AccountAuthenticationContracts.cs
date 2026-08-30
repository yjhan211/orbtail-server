namespace network.contracts.authentication;

public sealed record AccountTokenResolution(
    long PlayerId,
    string AccountToken,
    bool IsNewAccount,
    bool WasLegacyMigration);

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

public interface IAccountTokenService
{
    public Task<AccountTokenResolution> ResolveAsync(string? presentedToken);
}

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
