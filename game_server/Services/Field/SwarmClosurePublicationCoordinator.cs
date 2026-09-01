using System.Collections.Concurrent;
using System.Collections.Immutable;
using network.common;
using network.common.data.models;

namespace game_server.services;

/// <summary>
///     Owns the per-match wire order for scheduled field/closure projections. Authoritative state
///     and immutable outbound plans are committed by the caller while it owns the match runtime
///     monitor; packet construction and transport run outside that monitor.
/// </summary>
internal sealed class SwarmClosurePublicationCoordinator
{
    private readonly ConcurrentDictionary<long, PublicationSequenceState> _publicationSequences = new();

    /// <summary>
    ///     Reserves the next closure-publication turn. Callers invoke this only after the complete
    ///     outbound plan has been frozen, while they still own the matching runtime monitor and an
    ///     operation lease that remains held through dispatch.
    /// </summary>
    public SwarmClosurePublicationTicket ReservePublication(long matchingId)
    {
        if (matchingId <= 0)
            throw new ArgumentOutOfRangeException(nameof(matchingId));

        PublicationSequenceState state = _publicationSequences.GetOrAdd(
            matchingId,
            static _ => new PublicationSequenceState());
        long sequence = Interlocked.Increment(ref state.NextTicket) - 1;
        return new SwarmClosurePublicationTicket(matchingId, sequence);
    }

    /// <summary>
    ///     Waits outside the match runtime monitor and dispatches committed projections in ticket
    ///     order. A failed dispatch always retires its turn so later closure projections cannot stall.
    /// </summary>
    public void DispatchInOrder(SwarmClosurePublicationTicket ticket, Action dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        if (!_publicationSequences.TryGetValue(ticket.MatchingId, out PublicationSequenceState? state))
        {
            throw new InvalidOperationException(
                $"Missing closure publication sequence for matching {ticket.MatchingId}.");
        }

        lock (state.WaitGate)
        {
            while (state.ServingTicket < ticket.Sequence)
                Monitor.Wait(state.WaitGate);
            if (state.ServingTicket > ticket.Sequence)
                return;
            if (state.DispatchingTicket == ticket.Sequence)
            {
                while (state.ServingTicket == ticket.Sequence)
                    Monitor.Wait(state.WaitGate);
                return;
            }

            state.DispatchingTicket = ticket.Sequence;
        }

        try
        {
            dispatch();
        }
        finally
        {
            lock (state.WaitGate)
            {
                state.DispatchingTicket = null;
                state.ServingTicket++;
                Monitor.PulseAll(state.WaitGate);
            }
        }
    }

    /// <summary>
    ///     Terminal component cleanup calls this only after every closure publication lease drains.
    /// </summary>
    public void ClearMatching(long matchingId) =>
        _publicationSequences.TryRemove(matchingId, out _);

    private sealed class PublicationSequenceState
    {
        public readonly object WaitGate = new();
        public long NextTicket;
        public long ServingTicket;
        public long? DispatchingTicket;
    }
}

internal readonly record struct SwarmClosurePublicationTicket(long MatchingId, long Sequence);

internal sealed record SwarmClosurePublicationPlan(
    long MatchingId,
    ImmutableArray<SwarmClosureOutbound> Outbound);

internal abstract record SwarmClosureOutbound(ImmutableArray<int> RecipientOrdinals);

internal sealed record SwarmFieldStateOutbound(
    long StartedAtUnixMs,
    ImmutableArray<int> RecipientOrdinals)
    : SwarmClosureOutbound(RecipientOrdinals);

internal sealed record SwarmClosureWarningOutbound(
    AreaType Area,
    int SecondsRemaining,
    long ClosureAtUnixMs,
    ImmutableArray<int> RecipientOrdinals)
    : SwarmClosureOutbound(RecipientOrdinals);

internal sealed record SwarmAreaClosedOutbound(
    AreaType Area,
    ImmutableArray<int> RecipientOrdinals)
    : SwarmClosureOutbound(RecipientOrdinals);

internal sealed record SwarmDoorStateOutbound(
    int DoorId,
    ImmutableArray<int> RecipientOrdinals)
    : SwarmClosureOutbound(RecipientOrdinals);

internal sealed record SwarmInventoryUpdateOutbound(
    SwarmInGameItemSnapshot Item,
    ImmutableArray<int> RecipientOrdinals)
    : SwarmClosureOutbound(RecipientOrdinals);

internal sealed record SwarmRingVfxOutbound(
    long OwnerPlayerId,
    float CenterX,
    float CenterY,
    float Radius,
    int Kind,
    long VictimPlayerId,
    int FromOrdinal,
    ImmutableArray<int> RecipientOrdinals)
    : SwarmClosureOutbound(RecipientOrdinals);

internal readonly record struct SwarmInGameItemSnapshot(
    long ItemUid,
    int ItemId,
    int Count,
    GiftState GiftState)
{
    public static SwarmInGameItemSnapshot Capture(InGameItemInfo item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new SwarmInGameItemSnapshot(item.ItemUid, item.ItemId, item.Count, item.GiftState);
    }

    public InGameItemInfo ToModel() => new()
    {
        ItemUid = ItemUid,
        ItemId = ItemId,
        Count = Count,
        GiftState = GiftState
    };
}
