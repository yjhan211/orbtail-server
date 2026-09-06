using network.helpers;

namespace user_server.accounts;

public sealed record AccountTokenResolution(
    long PlayerId,
    string AccountToken,
    bool IsNewAccount);

public sealed class AccountAuthenticationException(string message) : InvalidOperationException(message);

public sealed class AccountTokenService(
    IAccountCredentialStore credentialStore) : IAccountTokenService
{
    private const string TokenPrefix = "acct_";
    private const int MaxTokenGenerationAttempts = 5;

    public async Task<AccountTokenResolution> ResolveAsync(string? presentedToken)
    {
        if (string.IsNullOrWhiteSpace(presentedToken))
            return await CreateAccountAsync();

        string token = presentedToken.Trim();
        if (OpaqueToken.IsValid(token, TokenPrefix))
            return await ResolveOrProvisionOpaqueTokenAsync(token);

        throw new AccountAuthenticationException("The supplied account credential is invalid.");
    }

    private async Task<AccountTokenResolution> CreateAccountAsync()
    {
        for (int attempt = 0; attempt < MaxTokenGenerationAttempts; attempt++)
        {
            long playerId = await credentialStore.AllocatePlayerIdAsync();
            string token = OpaqueToken.Create(TokenPrefix);
            var result = await credentialStore.ProvisionAsync(
                playerId,
                token,
                OpaqueToken.Fingerprint(token));

            if (result.Status == AccountCredentialProvisionStatus.Created)
            {
                return new AccountTokenResolution(
                    playerId,
                    result.AccountToken ?? token,
                    IsNewAccount: true);
            }

            if (result.Status is not AccountCredentialProvisionStatus.TokenCollision and
                not AccountCredentialProvisionStatus.PlayerAlreadyExists)
                break;
        }

        throw new InvalidOperationException("An account credential could not be provisioned.");
    }

    private async Task<AccountTokenResolution> ResolveOrProvisionOpaqueTokenAsync(string token)
    {
        string tokenHash = OpaqueToken.Fingerprint(token);
        var credential = await credentialStore.FindByTokenHashAsync(tokenHash);
        if (credential != null)
            return ResolveCredential(credential, token, tokenHash, isNewAccount: false);

        // Unity persists a CSPRNG token before its first network request. Binding that durable
        // credential here lets an interrupted PlayerInfo setup retry the same account.
        for (int attempt = 0; attempt < MaxTokenGenerationAttempts; attempt++)
        {
            long playerId = await credentialStore.AllocatePlayerIdAsync();
            var result = await credentialStore.ProvisionAsync(
                playerId,
                token,
                tokenHash);

            if (result.Status == AccountCredentialProvisionStatus.Created)
            {
                return new AccountTokenResolution(
                    playerId,
                    result.AccountToken ?? token,
                    IsNewAccount: true);
            }

            if (result.Status == AccountCredentialProvisionStatus.TokenCollision)
            {
                // Concurrent first logins with the same durable token converge on the winner.
                credential = await credentialStore.FindByTokenHashAsync(tokenHash);
                if (credential != null)
                    return ResolveCredential(credential, token, tokenHash, isNewAccount: false);
            }
            else if (result.Status != AccountCredentialProvisionStatus.PlayerAlreadyExists)
            {
                break;
            }
        }

        throw new InvalidOperationException("The supplied account credential could not be provisioned.");
    }

    private static AccountTokenResolution ResolveCredential(
        AccountCredential credential,
        string presentedToken,
        string expectedTokenHash,
        bool isNewAccount)
    {
        if (!string.Equals(credential.TokenHash, expectedTokenHash, StringComparison.Ordinal))
        {
            throw new AccountAuthenticationException("The supplied account credential is invalid.");
        }

        return new AccountTokenResolution(
            credential.PlayerId,
            presentedToken,
            IsNewAccount: isNewAccount);
    }
}
