namespace network.infrastructure.redis;

public sealed class RedisLockNotAcquiredException(string resource)
    : InvalidOperationException($"Redis lock could not be acquired for resource '{resource}'.")
{
    public string Resource { get; } = resource;
}
