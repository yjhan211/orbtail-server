using game_server.services;

namespace demo_regression_tests;

public sealed class SwarmGrowthOfferCoordinatorTests
{
    private const long MatchingId = 43001;
    private const long PlayerId = 701;

    [Fact]
    public void EvaluateStanding_FirstObservationAndExactTwoSecondBoundary_Resend()
    {
        var state = new SwarmGrowthOfferStore();
        var coordinator = CreateCoordinator(state);
        var offer = CreateOffer(coordinator.AllocateOfferId());
        coordinator.RegisterOffer(PlayerId, offer);
        var nowUtc = new DateTime(2026, 8, 31, 1, 2, 3, DateTimeKind.Utc);

        SwarmStandingOfferDecision first = coordinator.EvaluateStanding(PlayerId, nowUtc);
        SwarmStandingOfferDecision early = coordinator.EvaluateStanding(
            PlayerId,
            nowUtc.AddMilliseconds(1999));
        SwarmStandingOfferDecision boundary = coordinator.EvaluateStanding(
            PlayerId,
            nowUtc.AddSeconds(2));

        Assert.Equal(SwarmStandingOfferAction.Resend, first.Action);
        Assert.Equal(offer, first.Offer);
        Assert.Equal(SwarmStandingOfferAction.Hold, early.Action);
        Assert.Equal(SwarmStandingOfferAction.Resend, boundary.Action);
        Assert.Equal(nowUtc.AddSeconds(2), state.OfferResentAtUtc[(MatchingId, PlayerId)]);
    }

    [Fact]
    public void EvaluateFunding_PreviewDeduplicatesUntilCostChangesOrTwoSecondsPass()
    {
        var state = new SwarmGrowthOfferStore();
        var coordinator = CreateCoordinator(state);
        var nowUtc = new DateTime(2026, 8, 31, 2, 0, 0, DateTimeKind.Utc);

        SwarmGrowthFundingAction first = coordinator.EvaluateFunding(
            PlayerId, nowUtc, stoneCount: 2, finalCost: 3);
        SwarmGrowthFundingAction unchanged = coordinator.EvaluateFunding(
            PlayerId, nowUtc.AddMilliseconds(1999), stoneCount: 2, finalCost: 3);
        SwarmGrowthFundingAction changed = coordinator.EvaluateFunding(
            PlayerId, nowUtc.AddMilliseconds(1999), stoneCount: 2, finalCost: 4);
        SwarmGrowthFundingAction boundary = coordinator.EvaluateFunding(
            PlayerId, nowUtc.AddMilliseconds(3999), stoneCount: 2, finalCost: 4);

        Assert.Equal(SwarmGrowthFundingAction.SendPreview, first);
        Assert.Equal(SwarmGrowthFundingAction.HoldPreview, unchanged);
        Assert.Equal(SwarmGrowthFundingAction.SendPreview, changed);
        Assert.Equal(SwarmGrowthFundingAction.SendPreview, boundary);
        Assert.Equal(4, state.PreviewCost[(MatchingId, PlayerId)]);
        Assert.Equal(nowUtc.AddMilliseconds(3999), state.OfferResentAtUtc[(MatchingId, PlayerId)]);
    }

    [Fact]
    public void EvaluateFunding_WhenStonesEqualCost_ReadiesOfferAndClearsPreview()
    {
        var state = new SwarmGrowthOfferStore();
        var coordinator = CreateCoordinator(state);
        var nowUtc = new DateTime(2026, 8, 31, 3, 0, 0, DateTimeKind.Utc);
        state.PreviewCost[(MatchingId, PlayerId)] = 5;
        state.OfferResentAtUtc[(MatchingId, PlayerId)] = nowUtc.AddSeconds(-1);

        SwarmGrowthFundingAction action = coordinator.EvaluateFunding(
            PlayerId, nowUtc, stoneCount: 5, finalCost: 5);

        Assert.Equal(SwarmGrowthFundingAction.ReadyToCreate, action);
        Assert.DoesNotContain((MatchingId, PlayerId), state.PreviewCost.Keys);
        Assert.DoesNotContain((MatchingId, PlayerId), state.OfferResentAtUtc.Keys);
    }

    [Fact]
    public void AllocateOfferId_IsProcessWideAndUnregisteredAllocationStillConsumesId()
    {
        var offerIds = new SwarmGrowthOfferIdSequence();
        var first = CreateCoordinator(new SwarmGrowthOfferStore(), offerIds);
        var second = CreateCoordinator(
            new SwarmGrowthOfferStore(),
            offerIds,
            MatchingId + 1);

        int botOfferId = first.AllocateOfferId();
        int nextHumanOfferId = first.AllocateOfferId();
        int otherMatchOfferId = second.AllocateOfferId();

        Assert.Equal(1, botOfferId);
        Assert.Equal(2, nextHumanOfferId);
        Assert.Equal(3, otherMatchOfferId);
    }

    [Fact]
    public void TryApplyPick_WithStaleOfferId_DoesNotInvokeEffectOrRemoveOffer()
    {
        var state = new SwarmGrowthOfferStore();
        var coordinator = CreateCoordinator(state);
        var offer = CreateOffer(coordinator.AllocateOfferId());
        coordinator.RegisterOffer(PlayerId, offer);
        int effectCalls = 0;

        SwarmGrowthPickResolution result = coordinator.TryApplyPick(
            PlayerId,
            offer.OfferId + 1,
            _ =>
            {
                effectCalls++;
                return true;
            });

        Assert.False(result.OfferMatched);
        Assert.False(result.Applied);
        Assert.Equal(0, effectCalls);
        Assert.Equal(offer, state.Offers[(MatchingId, PlayerId)]);
    }

    [Fact]
    public void TryApplyPick_WhenEffectRejects_KeepsOfferAndResendTimestamp()
    {
        var state = new SwarmGrowthOfferStore();
        var coordinator = CreateCoordinator(state);
        var offer = CreateOffer(coordinator.AllocateOfferId());
        coordinator.RegisterOffer(PlayerId, offer);
        var sentAtUtc = new DateTime(2026, 8, 31, 4, 0, 0, DateTimeKind.Utc);
        state.OfferResentAtUtc[(MatchingId, PlayerId)] = sentAtUtc;
        int effectCalls = 0;

        SwarmGrowthPickResolution result = coordinator.TryApplyPick(
            PlayerId,
            offer.OfferId,
            _ =>
            {
                effectCalls++;
                return false;
            });

        Assert.True(result.OfferMatched);
        Assert.False(result.Applied);
        Assert.Equal(offer, result.Offer);
        Assert.Equal(1, effectCalls);
        Assert.Equal(offer, state.Offers[(MatchingId, PlayerId)]);
        Assert.Equal(sentAtUtc, state.OfferResentAtUtc[(MatchingId, PlayerId)]);
    }

    [Fact]
    public void TryApplyPick_WhenEffectSucceeds_RemovesStateAndCannotApplyTwice()
    {
        var state = new SwarmGrowthOfferStore();
        var coordinator = CreateCoordinator(state);
        var offer = CreateOffer(coordinator.AllocateOfferId());
        coordinator.RegisterOffer(PlayerId, offer);
        state.OfferResentAtUtc[(MatchingId, PlayerId)] =
            new DateTime(2026, 8, 31, 5, 0, 0, DateTimeKind.Utc);
        int effectCalls = 0;

        SwarmGrowthPickResolution first = coordinator.TryApplyPick(
            PlayerId,
            offer.OfferId,
            _ =>
            {
                effectCalls++;
                return true;
            });
        SwarmGrowthPickResolution duplicate = coordinator.TryApplyPick(
            PlayerId,
            offer.OfferId,
            _ =>
            {
                effectCalls++;
                return true;
            });

        Assert.True(first.OfferMatched);
        Assert.True(first.Applied);
        Assert.Equal(offer, first.Offer);
        Assert.False(duplicate.OfferMatched);
        Assert.False(duplicate.Applied);
        Assert.Equal(1, effectCalls);
        Assert.DoesNotContain((MatchingId, PlayerId), state.Offers.Keys);
        Assert.DoesNotContain((MatchingId, PlayerId), state.OfferResentAtUtc.Keys);
    }

    [Fact]
    public void RegisterOffer_DoesNotMarkInitialSendSoNextStandingEvaluationResendsImmediately()
    {
        var state = new SwarmGrowthOfferStore();
        var coordinator = CreateCoordinator(state);
        var nowUtc = new DateTime(2026, 8, 31, 6, 0, 0, DateTimeKind.Utc);
        Assert.Equal(
            SwarmGrowthFundingAction.SendPreview,
            coordinator.EvaluateFunding(PlayerId, nowUtc, stoneCount: 2, finalCost: 3));
        Assert.Equal(
            SwarmGrowthFundingAction.ReadyToCreate,
            coordinator.EvaluateFunding(PlayerId, nowUtc, stoneCount: 3, finalCost: 3));
        var offer = CreateOffer(coordinator.AllocateOfferId());

        coordinator.RegisterOffer(PlayerId, offer);

        Assert.DoesNotContain((MatchingId, PlayerId), state.OfferResentAtUtc.Keys);
        SwarmStandingOfferDecision decision = coordinator.EvaluateStanding(PlayerId, nowUtc);
        Assert.Equal(SwarmStandingOfferAction.Resend, decision.Action);
        Assert.Equal(offer, decision.Offer);
        Assert.Equal(nowUtc, state.OfferResentAtUtc[(MatchingId, PlayerId)]);
    }

    private static SwarmGrowthOfferState CreateOffer(int offerId) =>
        new(
            offerId,
            Cost: 3,
            SpawnItemId: 101,
            EnhanceTargetTier: 0,
            ArmorCount: 1,
            CostSummon: 3,
            CostAttack: 5,
            CostDefense: 4);

    private static SwarmGrowthOfferCoordinator CreateCoordinator(
        SwarmGrowthOfferStore state,
        SwarmGrowthOfferIdSequence? offerIds = null,
        long matchingId = MatchingId) =>
        new(matchingId, state, offerIds ?? new SwarmGrowthOfferIdSequence());
}
