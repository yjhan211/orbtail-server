using network.helpers;

namespace user_server.accounts;

public sealed record AccountTokenResolution(long PlayerId, string AccountToken, bool IsNewAccount);
public sealed class AccountAuthenticationException(string message) : InvalidOperationException(message);

/// <summary>
///     로그인에 사용한 계정 토큰을 확인하고, 연결된 PlayerId와 사용할 토큰을 반환한다.
///     토큰이 없으면 새 토큰과 계정을 발급하고, 토큰이 아직 등록되지 않았다면 새 PlayerId에 연결한다.
///     등록된 토큰은 해시로 조회하며, 잘못된 형식이나 해시 불일치는 인증 실패로 처리한다.
/// </summary>
public sealed class AccountTokenService(IAccountCredentialStore credentialStore)
{
    private const string TokenPrefix = "acct_";

    public async Task<AccountTokenResolution> ResolveAsync(string? presentedToken)
    {
        if (string.IsNullOrWhiteSpace(presentedToken))
        {
            return await CreateAccountAsync();
        }

        string token = presentedToken.Trim();
        if (OpaqueToken.IsValid(token, TokenPrefix))
        {
            return await GetOrCreateAccountAsync(token);
        }

        throw new AccountAuthenticationException("The supplied account credential is invalid.");
    }

    private async Task<AccountTokenResolution> CreateAccountAsync()
    {
        long playerId = await credentialStore.AllocatePlayerIdAsync();
        string token = OpaqueToken.Create(TokenPrefix);
        var result = await credentialStore.RegisterAsync(playerId, token, OpaqueToken.Fingerprint(token));
        if (result.Status == AccountRegistrationStatus.Created)
        {
            return new AccountTokenResolution(
                playerId,
                result.AccountToken ?? token,
                IsNewAccount: true);
        }

        throw new InvalidOperationException("An account credential could not be registered.");
    }

    private async Task<AccountTokenResolution> GetOrCreateAccountAsync(string token)
    {
        string tokenHash = OpaqueToken.Fingerprint(token);
        var credential = await credentialStore.FindByTokenHashAsync(tokenHash);
        if (credential != null)
        {
            return ResolveCredential(credential, token, tokenHash, isNewAccount: false);
        }

        long playerId = await credentialStore.AllocatePlayerIdAsync();
        var result = await credentialStore.RegisterAsync(playerId, token, tokenHash);
        if (result.Status == AccountRegistrationStatus.Created)
        {
            return new AccountTokenResolution(playerId, result.AccountToken ?? token, IsNewAccount: true);
        }
        if (result.Status == AccountRegistrationStatus.TokenAlreadyRegistered)
        {
            credential = await credentialStore.FindByTokenHashAsync(tokenHash);
            if (credential != null)
            {
                return ResolveCredential(credential, token, tokenHash, isNewAccount: false);
            }
        }

        throw new InvalidOperationException("The supplied account credential could not be registered.");
    }

    private static AccountTokenResolution ResolveCredential(AccountCredential credential, string presentedToken, string expectedTokenHash, bool isNewAccount)
    {
        if (!string.Equals(credential.TokenHash, expectedTokenHash, StringComparison.Ordinal))
        {
            throw new AccountAuthenticationException("The supplied account credential is invalid.");
        }
        return new AccountTokenResolution(credential.PlayerId, presentedToken, IsNewAccount: isNewAccount);
    }
}
