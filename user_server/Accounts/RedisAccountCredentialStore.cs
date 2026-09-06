using System.Globalization;
using System.Text;
using network.common;
using network.common.data.models;
using network.helpers;
using network.infrastructure.redis;
using StackExchange.Redis;

namespace user_server.accounts;

/// <summary>
///     계정 토큰의 해시와 PlayerId의 연결을 Redis에 저장하고 조회한다.
///     신규 등록 시 토큰과 플레이어에 잠금을 걸어 중복 등록을 막고,
///     토큰 해시 → PlayerId와 PlayerId → 토큰 해시 두 매핑을 함께 저장한다.
///     PlayerId 발급도 담당한다.
/// </summary>
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
        var playerIdValue = await redisOperations.HashGetAsync(PlayerByTokenHashKey, tokenHash);
        if (playerIdValue.IsNullOrEmpty ||
            !long.TryParse(Decode(playerIdValue), NumberStyles.None, CultureInfo.InvariantCulture, out long playerId) ||
            playerId <= 0)
        {
            return null;
        }

        var tokenHashValue = await redisOperations.HashGetAsync(TokenHashByPlayerKey, playerId.ToString(CultureInfo.InvariantCulture));
        if (tokenHashValue.IsNullOrEmpty)
        {
            return null;
        }

        string storedCredential = Decode(tokenHashValue);
        bool containsLegacyPlaintext = OpaqueToken.IsValid(storedCredential, "acct_");
        string storedTokenHash = containsLegacyPlaintext ? OpaqueToken.Fingerprint(storedCredential) : storedCredential;
        if (!string.Equals(storedTokenHash, tokenHash, StringComparison.Ordinal))
        {
            return null;
        }

        if (containsLegacyPlaintext)
        {
            await redisOperations.HashSetAsync(TokenHashByPlayerKey, playerId.ToString(CultureInfo.InvariantCulture), Encode(storedTokenHash));
        }

        return new AccountCredential(playerId, storedTokenHash);
    }

    public async Task<AccountRegistrationResult> RegisterAsync(long playerId, string token, string tokenHash)
    {
        await using var tokenLock = await redLockFactory.AcquireLockAsync($"account_token:{tokenHash}", Config.LOCK_TTL);
        await using var playerLock = await redLockFactory.AcquireLockAsync(PlayerInfo.GetLockKey(playerId), Config.LOCK_TTL);

        string playerField = playerId.ToString(CultureInfo.InvariantCulture);
        bool playerExists = await redisOperations.HashExistsAsync(PlayerInfo.HashKey, playerField);
        if (playerExists)
        {
            return new AccountRegistrationResult(AccountRegistrationStatus.PlayerAlreadyExists);
        }

        var existingTokenHashValue = await redisOperations.HashGetAsync(TokenHashByPlayerKey, playerField);
        if (!existingTokenHashValue.IsNullOrEmpty)
        {
            return new AccountRegistrationResult(AccountRegistrationStatus.PlayerAlreadyExists);
        }

        var existingPlayerMapping = await redisOperations.HashGetAsync(PlayerByTokenHashKey, tokenHash);
        if (!existingPlayerMapping.IsNullOrEmpty &&
            (!long.TryParse(Decode(existingPlayerMapping), NumberStyles.None, CultureInfo.InvariantCulture,
                 out long existingPlayerId) || existingPlayerId != playerId))
        {
            return new AccountRegistrationResult(AccountRegistrationStatus.TokenAlreadyRegistered);
        }

        await redisOperations.HashSetPairAtomicAsync(
            TokenHashByPlayerKey,
            playerField,
            Encode(tokenHash),
            PlayerByTokenHashKey,
            tokenHash,
            Encode(playerField));
        return new AccountRegistrationResult(AccountRegistrationStatus.Created, token);
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
