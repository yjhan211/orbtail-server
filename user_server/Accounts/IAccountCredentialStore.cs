
namespace user_server.accounts;

public sealed record AccountCredential(long PlayerId, string TokenHash);

public enum AccountRegistrationStatus
{
    Created,
    PlayerAlreadyExists,
    TokenCollision
}

public sealed record AccountRegistrationResult(AccountRegistrationStatus Status, string? AccountToken = null);

public interface IAccountCredentialStore
{
    public Task<long> AllocatePlayerIdAsync();
    public Task<AccountCredential?> FindByTokenHashAsync(string tokenHash);
    public Task<AccountRegistrationResult> RegisterAsync(long playerId, string token, string tokenHash);
}
