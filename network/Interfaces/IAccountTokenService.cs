using network.contracts.authentication;
using network.interfaces;

namespace network.interfaces;

public interface IAccountTokenService
{
    public Task<AccountTokenResolution> ResolveAsync(string? presentedToken);
}
