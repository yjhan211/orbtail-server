using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data.models;
namespace demo_regression_tests;

public sealed class GroundItemPickupServiceTests
{
    [Fact]
    public void PickupRequiresOwningMatchLock()
    {
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(984201);
        Assert.Throws<InvalidOperationException>(() => GroundItemPickupService.TryPickup(
            runtime, 1, AreaType.None, new Vector3f(0, 0, 0), 0, 1));
        using (store.Enter(runtime)) runtime.TryMarkTerminal();
    }

    [Fact]
    public void MissingItemDoesNotCreateInventoryOrRecovery()
    {
        var store = new MatchRuntimeStore(NullLogger.Instance);
        var runtime = store.GetOrCreate(984202);
        using (store.Enter(runtime))
        {
            var result = GroundItemPickupService.TryPickup(
                runtime, 1, AreaType.None, new Vector3f(0, 0, 0), 0, long.MaxValue);
            Assert.NotEqual(GroundItemClaimStatus.Success, result.Status);
            Assert.Null(result.ClaimedItem);
            Assert.Null(result.AddedItem);
            Assert.False(result.AutoUsed);
            Assert.Equal(0, result.HealthRecovery);
            runtime.TryMarkTerminal();
        }
    }
}
