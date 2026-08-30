using StackExchange.Redis;

namespace network.interfaces;

public interface ICacheHelper
{

    public IRedLockFactory GetRedLockFactory();

    public Task<bool> HashSetAsync(string key, long field, byte[] value, int db = -1);
    public Task<bool> HashSetAsync(string key, string field, byte[] value, int db = -1);
    public Task HashSetWithExpiryAsync(
        string key,
        string field,
        byte[] value,
        TimeSpan expiry,
        int db = -1);
    public Task HashSetPairAtomicAsync(
        string firstKey,
        string firstField,
        RedisValue firstValue,
        string secondKey,
        string secondField,
        RedisValue secondValue,
        int db = -1
    );
    public Task<RedisValue> HashGetAsync(string key, string field, int db = -1);
    public Task<RedisValue> HashGetAsync(string key, long field, int db = -1);
    public Task<RedisValue> HashGetDeleteFirstAsync(
        string firstKey,
        RedisValue firstField,
        string secondKey,
        RedisValue secondField,
        int db = -1
    );
    public Task<RedisValue[]> HashGetAsync(string key, RedisValue[] fields, int db = -1);
    public Task<HashEntry[]> HashGetAllAsync(string key, int db = -1);
    public Task<RedisValue> HashGetFromReplicaAsync(string key, string field, int db = -1);
    public Task<RedisValue> HashGetFromReplicaAsync(string key, long field, int db = -1);
    public Task<RedisValue[]> HashGetFromReplicaAsync(string key, RedisValue[] fields, int db = -1);
    public Task<HashEntry[]> HashGetAllFromReplicaAsync(string key, int db = -1);
    public Task<bool> HashDeleteAsync(string key, string field, int db = -1);
    public Task<bool> HashDeleteAsync(string key, long field, int db = -1);
    public Task<bool> HashExistsAsync(string key, string field, int db = -1);
    public Task<bool> HashExistsOnReplicaAsync(string key, string field, int db = -1);
    public Task<RedisValue[]> ListRangeAsync(string key, int start = 0, int end = -1, int db = -1);
    public Task<RedisValue[]> ListRangeFromReplicaAsync(string key, int start = 0, int end = -1, int db = -1);
    public Task<RedisValue[]> ListRangeAsync(List<string> keys, int start = 0, int end = -1, int db = -1);
    public Task<List<(string key, RedisValue value)>> ListRangeWithKeyAsync(List<string> keys, int start = 0, int end = -1, int db = -1);
    public Task<long> ListPushAsync(string key, string value, int db = -1);
    public Task<long> ListRemoveAsync(string key, string value, int db = -1);
    public Task<long> EnqueueAsync(string key, byte[] value, int db = -1);
    public Task<byte[]?> DequeueAsync(string key, int db = -1);
    public Task<long> ListLengthAsync(string key, int db = -1);
    public Task<long> StringIncrementAsync(string key, int db = -1);
    public Task<long> StringIncrementByAsync(string key, long increment, int db = -1);
    public Task<RedisValue> StringGetAsync(string key, int db = -1);
    public Task<RedisValue> StringGetDeleteAsync(string key, int db = -1);
    public Task<bool> StringSetAsync(string key, RedisValue value, TimeSpan? expiry = null, int db = -1);
    public Task<bool> StringSetIfNotExistsAsync(
        string key,
        RedisValue value,
        TimeSpan? expiry = null,
        int db = -1
    );
    public Task<bool> StringSetIfEqualsAsync(
        string key,
        string expectedValue,
        string newValue,
        TimeSpan expiry,
        int db = -1
    );
    public Task<bool> StringDeleteIfEqualsAsync(string key, string expectedValue, int db = -1);
    public Task<bool> StringSetWithExpiryIfGuardEqualsAsync(
        string key,
        RedisValue value,
        TimeSpan expiry,
        string guardKey,
        string expectedGuardValue,
        int db = -1
    );
    public Task<RedisValue> StringGetDeleteIfGuardsEqualAsync(
        string valueKey,
        string firstGuardKey,
        string expectedFirstGuardValue,
        string secondGuardKey,
        string expectedSecondGuardValue,
        TimeSpan secondGuardExpiry,
        string ownerSlotsKey,
        long matchingId,
        int db = -1
    );
    public Task<RedisValue> StringGetDeleteIfGuardsEqualWithReceiptAsync(
        string valueKey,
        string firstGuardKey,
        string expectedFirstGuardValue,
        string secondGuardKey,
        string expectedSecondGuardValue,
        TimeSpan secondGuardExpiry,
        string ownerSlotsKey,
        long matchingId,
        string receiptKey,
        string consumeNonce,
        TimeSpan receiptExpiry,
        int db = -1
    );
    public Task<RedisValue> StringReconcileConsumeReceiptAsync(
        string valueKey,
        string receiptKey,
        string consumeNonce,
        TimeSpan receiptExpiry,
        int db = -1
    );
    public Task<long> TryReserveGameServerMatchOwnerAsync(
        string nodeLeaseKey,
        string nodeAcceptingKey,
        string expectedGeneration,
        string nodeSlotsKey,
        string matchOwnerKey,
        string fenceKey,
        string nodeId,
        long matchingId,
        int capacity,
        TimeSpan ownerLifetime,
        int db = -1
    );
    public Task<bool> RenewGameServerMatchOwnerAsync(
        string matchOwnerKey,
        string expectedOwnerToken,
        string nodeSlotsKey,
        long matchingId,
        TimeSpan ownerLifetime,
        int db = -1
    );
    public Task<bool> ReleaseGameServerMatchOwnerAsync(
        string matchOwnerKey,
        string expectedOwnerToken,
        string nodeSlotsKey,
        long matchingId,
        int db = -1
    );
    public Task<bool> ReleaseGameServerNodeLeaseAsync(
        string nodeLeaseKey,
        string nodeAcceptingKey,
        string nodeDescriptorKey,
        string nodeHeartbeatIndexKey,
        string nodeId,
        string expectedGeneration,
        int db = -1
    );
    public Task<bool> TryClaimSortedSetEntryAsync(
        string sortedSetKey,
        RedisValue entry,
        string claimKey,
        string claimValue,
        TimeSpan expiry,
        int db = -1
    );
    public Task<bool> KeyDeleteAsync(string key, int db = -1);
    public Task<bool> KeyExpireAsync(string key, TimeSpan? expiry, int db = -1);
    public Task<bool> SortedSetAddAsync(string key, byte[] value, double score, int db = -1);
    public Task<byte[][]> SortedSetRangeByScoreAsync(string key, double start = double.NegativeInfinity, double stop = double.PositiveInfinity, int db = -1);
    public Task<bool> SortedSetRemoveAsync(string key, byte[] value, int db = -1);
}
