using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using network.contracts.authentication;
using network.core.security;
using network.interfaces;

namespace network.application.authentication;

public sealed class AccountTokenOptions
{
    public bool AllowLegacyNumericMigration { get; set; }
    public string? LegacyMigrationSecret { get; set; }
}

public sealed class AccountAuthenticationException(string message) : InvalidOperationException(message);

public sealed class AccountTokenService(
    IAccountCredentialStore credentialStore,
    AccountTokenOptions options) : IAccountTokenService
{
    private const string TokenPrefix = "acct_";
    private const int MaxTokenGenerationAttempts = 5;

    public async Task<AccountTokenResolution> ResolveAsync(string? presentedToken)
    {
        if (string.IsNullOrWhiteSpace(presentedToken))
            return await CreateAccountAsync();

        string token = presentedToken.Trim();
        if (OpaqueTokenCodec.IsValid(token, TokenPrefix))
            return await ResolveOrProvisionOpaqueTokenAsync(token);

        if (options.AllowLegacyNumericMigration &&
            long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out long legacyPlayerId) &&
            legacyPlayerId > 0)
        {
            return await MigrateLegacyTokenAsync(legacyPlayerId);
        }

        throw new AccountAuthenticationException("The supplied account credential is invalid.");
    }

    private async Task<AccountTokenResolution> CreateAccountAsync()
    {
        for (int attempt = 0; attempt < MaxTokenGenerationAttempts; attempt++)
        {
            long playerId = await credentialStore.AllocatePlayerIdAsync();
            string token = OpaqueTokenCodec.Create(TokenPrefix);
            var result = await credentialStore.ProvisionAsync(
                playerId,
                token,
                OpaqueTokenCodec.Fingerprint(token),
                requireExistingPlayer: false);

            if (result.Status == AccountCredentialProvisionStatus.Created)
            {
                return new AccountTokenResolution(
                    playerId,
                    result.AccountToken ?? token,
                    IsNewAccount: true,
                    WasLegacyMigration: false);
            }

            if (result.Status is not AccountCredentialProvisionStatus.TokenCollision and
                not AccountCredentialProvisionStatus.PlayerAlreadyExists)
                break;
        }

        throw new InvalidOperationException("An account credential could not be provisioned.");
    }

    private async Task<AccountTokenResolution> ResolveOrProvisionOpaqueTokenAsync(string token)
    {
        string tokenHash = OpaqueTokenCodec.Fingerprint(token);
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
                tokenHash,
                requireExistingPlayer: false);

            if (result.Status == AccountCredentialProvisionStatus.Created)
            {
                return new AccountTokenResolution(
                    playerId,
                    result.AccountToken ?? token,
                    IsNewAccount: true,
                    WasLegacyMigration: false);
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
            IsNewAccount: isNewAccount,
            WasLegacyMigration: false);
    }

    private async Task<AccountTokenResolution> MigrateLegacyTokenAsync(long playerId)
    {
        string token = CreateLegacyMigrationToken(playerId);
        for (int attempt = 0; attempt < MaxTokenGenerationAttempts; attempt++)
        {
            var result = await credentialStore.ProvisionAsync(
                playerId,
                token,
                OpaqueTokenCodec.Fingerprint(token),
                requireExistingPlayer: true);

            if (result.Status == AccountCredentialProvisionStatus.Created)
            {
                return new AccountTokenResolution(
                    playerId,
                    result.AccountToken ?? token,
                    IsNewAccount: false,
                    WasLegacyMigration: true);
            }

            if (result.Status == AccountCredentialProvisionStatus.Existing)
            {
                if (!string.IsNullOrWhiteSpace(result.AccountToken))
                {
                    return new AccountTokenResolution(
                        playerId,
                        result.AccountToken,
                        IsNewAccount: false,
                        WasLegacyMigration: true);
                }

                throw new AccountAuthenticationException(
                    "The legacy account has already migrated; use its opaque credential.");
            }

            if (result.Status == AccountCredentialProvisionStatus.PlayerNotFound)
                throw new AccountAuthenticationException("The legacy account does not exist.");

            if (result.Status != AccountCredentialProvisionStatus.TokenCollision)
                break;
        }

        throw new InvalidOperationException("The legacy account credential could not be migrated.");
    }

    private string CreateLegacyMigrationToken(long playerId)
    {
        if (string.IsNullOrWhiteSpace(options.LegacyMigrationSecret))
            throw new InvalidOperationException("Legacy account migration requires a server secret.");

        byte[] key = Encoding.UTF8.GetBytes(options.LegacyMigrationSecret);
        byte[] input = Encoding.UTF8.GetBytes(
            $"manitto-account-migration:v1:{playerId.ToString(CultureInfo.InvariantCulture)}");
        byte[] digest = HMACSHA256.HashData(key, input);
        return OpaqueTokenCodec.CreateFromBytes(TokenPrefix, digest);
    }
}
