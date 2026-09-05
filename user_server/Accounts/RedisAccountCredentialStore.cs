using System.Globalization;
using System.Text;
using network.common;
using network.common.data.models;
using network.helpers;
using network.infrastructure.redis;
using StackExchange.Redis;

namespace user_server.accounts;

public sealed class RedisAccountCredentialStore(
    IRedisOperations redisOperations,
    IRedLockFactory redLockFactory) : IAccountCredentialStore
{
    private const string PlayerIdCounterKey = "player_id_counter";
    private const string AccountHashTag = "{account}";
    private const string TokenHashByPlayerKey = AccountHashTag + ":token_hash_by_player";
    private const string PlayerByTokenHashKey = AccountHashTag + ":player_by_token_hash";

    public Task<long> AllocatePlayerIdAsync()
    {
        return redisOperations.StringIncrementAsync(PlayerIdCounterKey);
    }

    public async Task<AccountCredential?> FindByTokenHashAsync(string tokenHash)
    {
        RedisValue playerIdValue = await redisOperations.HashGetAsync(PlayerByTokenHashKey, tokenHash);
        if (playerIdValue.IsNullOrEmpty ||
            !long.TryParse(Decode(playerIdValue), NumberStyles.None, CultureInfo.InvariantCulture, out long playerId) ||
            playerId <= 0)
        {
            return null;
        }

        RedisValue tokenHashValue = await redisOperations.HashGetAsync(
            TokenHashByPlayerKey,
            playerId.ToString(CultureInfo.InvariantCulture));
        if (tokenHashValue.IsNullOrEmpty)
            return null;

        string storedCredential = Decode(tokenHashValue);
        bool containsLegacyPlaintext = OpaqueToken.IsValid(storedCredential, "acct_");
        string storedTokenHash = containsLegacyPlaintext
            ? OpaqueToken.Fingerprint(storedCredential)
            : storedCredential;
        if (!string.Equals(storedTokenHash, tokenHash, StringComparison.Ordinal))
            return null;

        if (containsLegacyPlaintext)
        {
            await redisOperations.HashSetAsync(
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
        bool playerExists = await redisOperations.HashExistsAsync(PlayerInfo.HashKey, playerField);
        if (requireExistingPlayer && !playerExists)
        {
            return new AccountCredentialProvisionResult(AccountCredentialProvisionStatus.PlayerNotFound);
        }
        if (!requireExistingPlayer && playerExists)
        {
            return new AccountCredentialProvisionResult(AccountCredentialProvisionStatus.PlayerAlreadyExists);
        }

        RedisValue existingTokenHashValue = await redisOperations.HashGetAsync(TokenHashByPlayerKey, playerField);
        if (!existingTokenHashValue.IsNullOrEmpty)
        {
            if (!requireExistingPlayer)
                return new AccountCredentialProvisionResult(AccountCredentialProvisionStatus.PlayerAlreadyExists);

            string storedCredential = Decode(existingTokenHashValue);
            bool containsLegacyPlaintext = OpaqueToken.IsValid(storedCredential, "acct_");
            string existingTokenHash = containsLegacyPlaintext
                ? OpaqueToken.Fingerprint(storedCredential)
                : storedCredential;
            RedisValue mappedPlayerValue = await redisOperations.HashGetAsync(PlayerByTokenHashKey, existingTokenHash);
            if (!mappedPlayerValue.IsNullOrEmpty &&
                (!long.TryParse(Decode(mappedPlayerValue), NumberStyles.None, CultureInfo.InvariantCulture,
                     out long mappedPlayerId) || mappedPlayerId != playerId))
            {
                throw new InvalidOperationException("The stored account credential mapping is inconsistent.");
            }

            await redisOperations.HashSetPairAtomicAsync(
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

        RedisValue proposedMapping = await redisOperations.HashGetAsync(PlayerByTokenHashKey, proposedTokenHash);
        if (!proposedMapping.IsNullOrEmpty &&
            (!long.TryParse(Decode(proposedMapping), NumberStyles.None, CultureInfo.InvariantCulture,
                 out long proposedMappedPlayerId) || proposedMappedPlayerId != playerId))
        {
            return new AccountCredentialProvisionResult(AccountCredentialProvisionStatus.TokenCollision);
        }

        await redisOperations.HashSetPairAtomicAsync(
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
