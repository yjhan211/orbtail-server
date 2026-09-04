
namespace user_server.services;

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
