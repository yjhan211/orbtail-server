namespace game_server.services;

/// <summary>Describes what the caller should do with an already-standing growth offer.</summary>
public enum SwarmStandingOfferAction
{
    Missing,
    Hold,
    Resend
}

/// <summary>The state-only result of inspecting a player's standing growth offer.</summary>
public readonly record struct SwarmStandingOfferDecision(
    SwarmStandingOfferAction Action,
    SwarmGrowthOfferState Offer);

/// <summary>Describes the next state transition after comparing stones with the cheapest card cost.</summary>
public enum SwarmGrowthFundingAction
{
    HoldPreview,
    SendPreview,
    ReadyToCreate
}

/// <summary>The ownership result for one growth-pick attempt.</summary>
public readonly record struct SwarmGrowthPickResolution(
    bool OfferMatched,
    bool Applied,
    SwarmGrowthOfferState Offer);

/// <summary>
///     Preserves the process-wide offer id sequence used by both human and bot offers. Keeping the
///     sequence outside a match runtime prevents a late pick from an earlier match colliding with
///     a newly created offer after the same client session enters another match.
/// </summary>
public sealed class SwarmGrowthOfferIdSequence
{
    private int _lastOfferId;

    public int Allocate() => Interlocked.Increment(ref _lastOfferId);
}

/// <summary>
///     Owns one match's human growth-offer lifecycle and uses the process-wide id sequence shared
///     with bot offers. Network delivery, cost calculation, offer composition, telemetry, and card
///     effects remain with the caller. Calls are serialized by the enclosing match runtime boundary.
/// </summary>
public sealed class SwarmGrowthOfferCoordinator
{
    private static readonly TimeSpan OfferResendInterval = TimeSpan.FromSeconds(2d);

    private readonly long _matchingId;
    private readonly SwarmGrowthOfferStore _state;
    private readonly SwarmGrowthOfferIdSequence _offerIds;

    public SwarmGrowthOfferCoordinator(
        long matchingId,
        SwarmGrowthOfferStore state,
        SwarmGrowthOfferIdSequence offerIds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(matchingId);
        _matchingId = matchingId;
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _offerIds = offerIds ?? throw new ArgumentNullException(nameof(offerIds));
    }

    /// <summary>
    ///     Checks the standing offer before the caller computes a fresh cost snapshot. A missing
    ///     resend timestamp intentionally makes the first inspection request an immediate resend.
    /// </summary>
    public SwarmStandingOfferDecision EvaluateStanding(long playerId, DateTime nowUtc)
    {
        var key = (_matchingId, playerId);
        if (!_state.Offers.TryGetValue(key, out var offer))
            return new SwarmStandingOfferDecision(SwarmStandingOfferAction.Missing, default);

        if (!_state.OfferResentAtUtc.TryGetValue(key, out var lastSentAtUtc) ||
            nowUtc - lastSentAtUtc >= OfferResendInterval)
        {
            _state.OfferResentAtUtc[key] = nowUtc;
            return new SwarmStandingOfferDecision(SwarmStandingOfferAction.Resend, offer);
        }

        return new SwarmStandingOfferDecision(SwarmStandingOfferAction.Hold, offer);
    }

    /// <summary>
    ///     Updates preview deduplication state when the player cannot yet afford an offer, or
    ///     clears that state when the cheapest card becomes affordable.
    /// </summary>
    public SwarmGrowthFundingAction EvaluateFunding(
        long playerId,
        DateTime nowUtc,
        int stoneCount,
        int finalCost)
    {
        var key = (_matchingId, playerId);
        if (stoneCount >= finalCost)
        {
            _state.PreviewCost.Remove(key);
            _state.OfferResentAtUtc.Remove(key);
            return SwarmGrowthFundingAction.ReadyToCreate;
        }

        bool costChanged =
            !_state.PreviewCost.TryGetValue(key, out int shownCost) || shownCost != finalCost;
        bool resendDue =
            !_state.OfferResentAtUtc.TryGetValue(key, out var lastSentAtUtc) ||
            nowUtc - lastSentAtUtc >= OfferResendInterval;
        if (!costChanged && !resendDue)
            return SwarmGrowthFundingAction.HoldPreview;

        _state.PreviewCost[key] = finalCost;
        _state.OfferResentAtUtc[key] = nowUtc;
        return SwarmGrowthFundingAction.SendPreview;
    }

    /// <summary>
    ///     Allocates from the process-wide sequence. Bot offers use the same sequence even though
    ///     they are applied immediately and never registered as standing offers.
    /// </summary>
    public int AllocateOfferId() => _offerIds.Allocate();

    /// <summary>
    ///     Registers a human offer without recording its initial network send. This preserves the
    ///     current behavior in which the next tick immediately resends the newly registered offer.
    /// </summary>
    public void RegisterOffer(long playerId, SwarmGrowthOfferState offer) =>
        _state.Offers[(_matchingId, playerId)] = offer;

    /// <summary>
    ///     Applies a matching offer through the caller-provided effect and consumes ownership only
    ///     after the effect succeeds. Stale ids never invoke the effect.
    /// </summary>
    public SwarmGrowthPickResolution TryApplyPick(
        long playerId,
        int offerId,
        Func<SwarmGrowthOfferState, bool> tryApply)
    {
        ArgumentNullException.ThrowIfNull(tryApply);

        var key = (_matchingId, playerId);
        if (!_state.Offers.TryGetValue(key, out var offer) || offer.OfferId != offerId)
            return new SwarmGrowthPickResolution(OfferMatched: false, Applied: false, default);

        bool applied = tryApply(offer);
        if (applied)
        {
            _state.Offers.Remove(key);
            _state.OfferResentAtUtc.Remove(key);
        }

        return new SwarmGrowthPickResolution(OfferMatched: true, applied, offer);
    }
}
