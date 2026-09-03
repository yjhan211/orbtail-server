using System.Globalization;
using System.Text;
using network.common;
using network.common.data.models;
using network.helpers;
using network.interfaces;
using StackExchange.Redis;

namespace user_server.services;

public sealed class RedisAccountCredentialStore(
    ICacheHelper cacheHelper,
    IRedLockFactory redLockFactory) : IAccountCredentialStore
{
    private const string PlayerIdCounterKey = "player_id_counter";
    // Keep the existing Redis key for rolling migration, but store only fingerprints going forward.
    private const string TokenHashByPlayerKey = "account_token_by_player";
    private const string PlayerByTokenHashKey = "account_player_by_token_hash";

    public Task<long> AllocatePlayerIdAsync()
    {
        return cacheHelper.StringIncrementAsync(PlayerIdCounterKey);
    }

    public async Task<AccountCredential?> FindByTokenHashAsync(string tokenHash)
    {
        RedisValue playerIdValue = await cacheHelper.HashGetAsync(PlayerByTokenHashKey, tokenHash);
        if (playerIdValue.IsNullOrEmpty ||
            !long.TryParse(Decode(playerIdValue), NumberStyles.None, CultureInfo.InvariantCulture, out long playerId) ||
            playerId <= 0)
        {
            return null;
        }

        RedisValue tokenHashValue = await cacheHelper.HashGetAsync(
            TokenHashByPlayerKey,
            playerId.ToString(CultureInfo.InvariantCulture));
        if (tokenHashValue.IsNullOrEmpty)
            return null;

        string storedCredential = Decode(tokenHashValue);
        bool containsLegacyPlaintext = OpaqueTokenCodec.IsValid(storedCredential, "acct_");
        string storedTokenHash = containsLegacyPlaintext
            ? OpaqueTokenCodec.Fingerprint(storedCredential)
            : storedCredential;
        if (!string.Equals(storedTokenHash, tokenHash, StringComparison.Ordinal))
            return null;

        if (containsLegacyPlaintext)
        {
            await cacheHelper.HashSetAsync(
                TokenHashByPlayerKey,
                playerId.ToString(CultureInfo.InvariantCulture),
                Encode(storedTokenHash));
        }

        return new AccountCredential(playerId, storedTokenHash);
    }

    public async Task<AccountCredentialProvisionResult> ProvisionAsync(
        long playerId,
        string proposedToken,
        string proposedTokenHash,
        bool requireExistingPlayer)
    {
        // Different UserServer instances can allocate different playerIds for the same first-login
        // token. The token-hash lock must therefore be acquired before the player lock.
        await using var tokenLock = await redLockFactory.AcquireLockAsync(
            $"account_token:{proposedTokenHash}",
            Config.LOCK_TTL);
        await using var playerLock = await redLockFactory.AcquireLockAsync(
            PlayerInfo.GetLockKey(playerId),
            Config.LOCK_TTL);

        string playerField = playerId.ToString(CultureInfo.InvariantCulture);
        bool playerExists = await cacheHelper.HashExistsAsync(PlayerInfo.HashKey, playerField);
        if (requireExistingPlayer && !playerExists)
        {
            return new AccountCredentialProvisionResult(AccountCredentialProvisionStatus.PlayerNotFound);
        }
        if (!requireExistingPlayer && playerExists)
        {
            return new AccountCredentialProvisionResult(AccountCredentialProvisionStatus.PlayerAlreadyExists);
        }

        RedisValue existingTokenHashValue = await cacheHelper.HashGetAsync(TokenHashByPlayerKey, playerField);
        if (!existingTokenHashValue.IsNullOrEmpty)
        {
            if (!requireExistingPlayer)
                return new AccountCredentialProvisionResult(AccountCredentialProvisionStatus.PlayerAlreadyExists);

            string storedCredential = Decode(existingTokenHashValue);
            bool containsLegacyPlaintext = OpaqueTokenCodec.IsValid(storedCredential, "acct_");
            string existingTokenHash = containsLegacyPlaintext
                ? OpaqueTokenCodec.Fingerprint(storedCredential)
                : storedCredential;
            RedisValue mappedPlayerValue = await cacheHelper.HashGetAsync(PlayerByTokenHashKey, existingTokenHash);
            if (!mappedPlayerValue.IsNullOrEmpty &&
                (!long.TryParse(Decode(mappedPlayerValue), NumberStyles.None, CultureInfo.InvariantCulture,
                     out long mappedPlayerId) || mappedPlayerId != playerId))
            {
                throw new InvalidOperationException("The stored account credential mapping is inconsistent.");
            }

            await cacheHelper.HashSetPairAtomicAsync(
                TokenHashByPlayerKey,
                playerField,
                Encode(existingTokenHash),
                PlayerByTokenHashKey,
                existingTokenHash,
                Encode(playerField));
            return new AccountCredentialProvisionResult(
                AccountCredentialProvisionStatus.Existing,
                containsLegacyPlaintext
                    ? storedCredential
                    : string.Equals(existingTokenHash, proposedTokenHash, StringComparison.Ordinal)
                        ? proposedToken
                        : null);
        }

        RedisValue proposedMapping = await cacheHelper.HashGetAsync(PlayerByTokenHashKey, proposedTokenHash);
        if (!proposedMapping.IsNullOrEmpty &&
            (!long.TryParse(Decode(proposedMapping), NumberStyles.None, CultureInfo.InvariantCulture,
                 out long proposedMappedPlayerId) || proposedMappedPlayerId != playerId))
        {
            return new AccountCredentialProvisionResult(AccountCredentialProvisionStatus.TokenCollision);
        }

        await cacheHelper.HashSetPairAtomicAsync(
            TokenHashByPlayerKey,
            playerField,
            Encode(proposedTokenHash),
            PlayerByTokenHashKey,
            proposedTokenHash,
            Encode(playerField));
        return new AccountCredentialProvisionResult(AccountCredentialProvisionStatus.Created, proposedToken);
    }

    private static byte[] Encode(string value)
    {
        return Encoding.UTF8.GetBytes(value);
    }

    private static string Decode(RedisValue value)
    {
        return Encoding.UTF8.GetString((byte[])value!);
    }
}
