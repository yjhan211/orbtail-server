using StackExchange.Redis;

namespace network.interfaces;

public interface ICacheHelper
{
    
    IRedLockFactory GetRedLockFactory();
    
    Task<bool> HashSetAsync(string key, long field, byte[] value, int db = -1);
    Task<bool> HashSetAsync(string key, string field, byte[] value, int db = -1);
    Task<RedisValue> HashGetAsync(string key, string field, int db = -1);
    Task<RedisValue> HashGetAsync(string key, long field, int db = -1);
    Task<RedisValue[]> HashGetAsync(string key, RedisValue[] fields, int db = -1);
    Task<HashEntry[]> HashGetAllAsync(string key, int db = -1);
    Task<bool> HashDeleteAsync(string key, string field, int db = -1);
    Task<bool> HashDeleteAsync(string key, long field, int db = -1);
    Task<bool> HashExistsAsync(string key, string field, int db = -1);
    Task<RedisValue[]> ListRangeAsync(string key, int start = 0, int end = -1, int db = -1);
    Task<RedisValue[]> ListRangeAsync(List<string> keys, int start = 0, int end = -1, int db = -1);
    Task<List<(string key, RedisValue value)>> ListRangeWithKeyAsync(List<string> keys, int start = 0, int end = -1, int db = -1);
    Task<long> ListPushAsync(string key, string value, int db = -1);
    Task<long> ListRemoveAsync(string key, string value, int db = -1);
    Task<long> EnqueueAsync(string key, byte[] value, int db = -1);
    Task<byte[]?> DequeueAsync(string key, int db = -1);
    Task<long> ListLengthAsync(string key, int db = -1);
    Task<long> StringIncrementAsync(string key, int db = -1);
    Task<bool> KeyDeleteAsync(string key, int db = -1);
    Task<bool> SortedSetAddAsync(string key, byte[] value, double score, int db = -1);
    Task<byte[][]> SortedSetRangeByScoreAsync(string key, double start = double.NegativeInfinity, double stop = double.PositiveInfinity, int db = -1);
    Task<bool> SortedSetRemoveAsync(string key, byte[] value, int db = -1);
}
