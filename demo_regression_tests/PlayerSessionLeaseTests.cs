using Microsoft.Extensions.Logging.Abstractions;
using user_server.sessions;

namespace demo_regression_tests;

public sealed class PlayerSessionLeaseTests
{
    [Fact]
    public async Task LeaseRenewal_RejectsReplacedAndRemovedOwner()
    {
        var redis = new InMemoryRedisOperations();
        var store = new RedisPlayerSessionLeaseStore(redis, NullLogger<RedisPlayerSessionLeaseStore>.Instance);
        var first = (await store.TryAcquireAsync(7, "node-a", "a"))!;
        Assert.True(await store.TryRenewAsync(first));
        var second = (await store.TryAcquireAsync(7, "node-b", "b"))!;
        Assert.False(await store.TryRenewAsync(first));
        Assert.True(await store.TryRenewAsync(second));
        await store.TryReleaseAsync(second);
        Assert.False(await store.TryRenewAsync(second));
    }
}
