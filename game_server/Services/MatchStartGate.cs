using System.Collections.Concurrent;

namespace game_server.services;

/// <summary>
/// Keeps a match inert until every expected human client has connected, then releases all
/// server-authoritative gameplay on the same five-second countdown boundary.
/// </summary>
public static class MatchStartGate
{
    private const int MatchCapacity = 8;
    private static readonly ConcurrentDictionary<long, State> States = new();

    public static void RegisterHumanPlayer(long matchingId, long playerId, int botCount)
    {
        var state = States.GetOrAdd(matchingId, _ => new State());
        lock (state.SyncRoot)
        {
            state.ExpectedHumanCount = Math.Max(1, MatchCapacity - Math.Max(0, botCount));
            state.ConnectedHumanIds.Add(playerId);
        }
    }

    public static void MarkHumanReady(long matchingId, long playerId)
    {
        if (!States.TryGetValue(matchingId, out var state))
            return;

        lock (state.SyncRoot)
        {
            state.ReadyHumanIds.Add(playerId);
            if (state.CountdownEndsAtUtc == null &&
                state.ReadyHumanIds.Count >= state.ExpectedHumanCount)
            {
                state.CountdownEndsAtUtc = DateTime.UtcNow.AddSeconds(5);
            }
        }
    }

    public static bool IsGameplayActive(long matchingId)
    {
        if (!States.TryGetValue(matchingId, out var state))
            return true;

        lock (state.SyncRoot)
        {
            return state.CountdownEndsAtUtc is { } endsAt && DateTime.UtcNow >= endsAt;
        }
    }

    public static MatchStartSnapshot GetSnapshot(long matchingId)
    {
        if (!States.TryGetValue(matchingId, out var state))
            return new MatchStartSnapshot(false, 0);

        lock (state.SyncRoot)
        {
            if (state.CountdownEndsAtUtc is not { } endsAt)
                return new MatchStartSnapshot(true, -1);

            var remaining = Math.Max(0, (int)Math.Ceiling((endsAt - DateTime.UtcNow).TotalSeconds));
            return new MatchStartSnapshot(true, remaining);
        }
    }

    public static void RemoveMatching(long matchingId)
    {
        States.TryRemove(matchingId, out _);
    }

    private sealed class State
    {
        public object SyncRoot { get; } = new();
        public HashSet<long> ConnectedHumanIds { get; } = [];
        public HashSet<long> ReadyHumanIds { get; } = [];
        public int ExpectedHumanCount { get; set; } = MatchCapacity;
        public DateTime? CountdownEndsAtUtc { get; set; }
    }
}

public readonly record struct MatchStartSnapshot(bool IsKnown, int RemainingSeconds);
