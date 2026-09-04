using StackExchange.Redis;

namespace network.interfaces;

/// <summary>
///     서버 서비스와 Redis 저장소가 공통으로 사용하는 Redis 자료구조 및 원자 연산을 제공한다.
///     연결 수명과 재시도는 IRedisConnection이, 키 구성·직렬화·도메인 상태 전이는 각 저장소가 담당한다.
/// </summary>
public interface IRedisOperations
{
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
    public Task<bool> HashDeleteAsync(string key, string field, int db = -1);
    public Task<bool> HashDeleteAsync(string key, long field, int db = -1);
    public Task<bool> HashExistsAsync(string key, string field, int db = -1);
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
    public Task<bool> KeyDeleteAsync(string key, int db = -1);
    public Task<bool> KeyExpireAsync(string key, TimeSpan? expiry, int db = -1);
    public Task<bool> SortedSetAddAsync(string key, byte[] value, double score, int db = -1);
    public Task<byte[][]> SortedSetRangeByScoreAsync(string key, double start = double.NegativeInfinity, double stop = double.PositiveInfinity, int db = -1);
    public Task<bool> SortedSetRemoveAsync(string key, byte[] value, int db = -1);
}
