
namespace user_server.accounts;

public interface IAccountTokenService
{
    public Task<AccountTokenResolution> ResolveAsync(string? presentedToken);
}
