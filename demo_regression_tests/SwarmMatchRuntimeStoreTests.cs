using game_server.services;

namespace demo_regression_tests;

public sealed class SwarmMatchRuntimeStoreTests
{
    [Fact]
    public void GetOrCreate_ReturnsSameRuntimeForSameIdAndIsolatesDifferentIds()
    {
        var store = new SwarmMatchRuntimeStore();
        const long firstMatchingId = 42001;
        const long secondMatchingId = 42002;

        SwarmMatchRuntime first = store.GetOrCreate(firstMatchingId);
        SwarmMatchRuntime firstAgain = store.GetOrCreate(firstMatchingId);
        SwarmMatchRuntime second = store.GetOrCreate(secondMatchingId);

        Assert.Same(first, firstAgain);
        Assert.NotSame(first, second);
        Assert.Equal(firstMatchingId, first.MatchingId);
        Assert.Equal(secondMatchingId, second.MatchingId);
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void Remove_PreservesSiblingAndNextGetOrCreateBuildsNewAggregate()
    {
        var store = new SwarmMatchRuntimeStore();
        const long removedMatchingId = 42003;
        const long siblingMatchingId = 42004;

        SwarmMatchRuntime removed = store.GetOrCreate(removedMatchingId);
        SwarmMatchRuntime sibling = store.GetOrCreate(siblingMatchingId);

        Assert.True(store.Remove(removedMatchingId));
        Assert.False(store.TryGet(removedMatchingId, out SwarmMatchRuntime? missing));
        Assert.Null(missing);
        Assert.True(store.TryGet(siblingMatchingId, out SwarmMatchRuntime? preservedSibling));
        Assert.Same(sibling, preservedSibling);
        Assert.Equal(1, store.Count);

        SwarmMatchRuntime recreated = store.GetOrCreate(removedMatchingId);

        Assert.NotSame(removed, recreated);
        Assert.Equal(removedMatchingId, recreated.MatchingId);
        Assert.Same(sibling, store.GetOrCreate(siblingMatchingId));
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void Remove_DropsWholeAggregateWithoutTouchingSiblingHolderState()
    {
        var store = new SwarmMatchRuntimeStore();
        const long removedMatchingId = 42005;
        const long siblingMatchingId = 42006;
        const long removedPlayerId = 501;
        const long siblingPlayerId = 601;

        SwarmMatchRuntime removed = store.GetOrCreate(removedMatchingId);
        SwarmMatchRuntime sibling = store.GetOrCreate(siblingMatchingId);

        removed.TrailCombat.OrbTrails[(removedMatchingId, removedPlayerId)] = [];
        removed.GrowthOffers.PreviewCost[(removedMatchingId, removedPlayerId)] = 3;
        removed.BotTactics.FleeDirective.Add((removedMatchingId, removedPlayerId));
        removed.Pacing.StartingOrbGrantedPlayers.Add((removedMatchingId, removedPlayerId));

        sibling.TrailCombat.OrbTrails[(siblingMatchingId, siblingPlayerId)] = [];
        sibling.GrowthOffers.PreviewCost[(siblingMatchingId, siblingPlayerId)] = 7;
        sibling.BotTactics.FleeDirective.Add((siblingMatchingId, siblingPlayerId));
        sibling.Pacing.StartingOrbGrantedPlayers.Add((siblingMatchingId, siblingPlayerId));

        Assert.True(store.Remove(removedMatchingId));

        Assert.False(store.TryGet(removedMatchingId, out SwarmMatchRuntime? missing));
        Assert.Null(missing);
        Assert.True(store.TryGet(siblingMatchingId, out SwarmMatchRuntime? preservedSibling));
        Assert.Same(sibling, preservedSibling);
        Assert.Contains((siblingMatchingId, siblingPlayerId), sibling.TrailCombat.OrbTrails.Keys);
        Assert.Equal(7, sibling.GrowthOffers.PreviewCost[(siblingMatchingId, siblingPlayerId)]);
        Assert.Contains((siblingMatchingId, siblingPlayerId), sibling.BotTactics.FleeDirective);
        Assert.Contains((siblingMatchingId, siblingPlayerId), sibling.Pacing.StartingOrbGrantedPlayers);
        Assert.Equal(1, store.Count);

        SwarmMatchRuntime replacement = store.GetOrCreate(removedMatchingId);
        Assert.NotSame(removed, replacement);
        Assert.Empty(replacement.TrailCombat.OrbTrails);
        Assert.Empty(replacement.GrowthOffers.PreviewCost);
        Assert.Empty(replacement.BotTactics.FleeDirective);
        Assert.Empty(replacement.Pacing.StartingOrbGrantedPlayers);
    }

    [Fact]
    public void OfferIds_RemainProcessWideAcrossMatchesAndRuntimeRecreation()
    {
        var store = new SwarmMatchRuntimeStore();
        const long firstMatchingId = 42007;
        const long secondMatchingId = 42008;

        SwarmMatchRuntime first = store.GetOrCreate(firstMatchingId);
        SwarmMatchRuntime second = store.GetOrCreate(secondMatchingId);

        Assert.Equal(1, first.GrowthOfferCoordinator.AllocateOfferId());
        Assert.Equal(2, second.GrowthOfferCoordinator.AllocateOfferId());

        Assert.True(store.Remove(firstMatchingId));
        SwarmMatchRuntime recreated = store.GetOrCreate(firstMatchingId);

        Assert.Equal(3, recreated.GrowthOfferCoordinator.AllocateOfferId());
    }
}
