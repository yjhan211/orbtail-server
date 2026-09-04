
namespace user_server.services;

public interface IAccountTokenService
{
    public Task<AccountTokenResolution> ResolveAsync(string? presentedToken);
}
