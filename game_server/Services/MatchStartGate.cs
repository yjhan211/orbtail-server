using System.Collections.Concurrent;
using network.common;

namespace game_server.services;

/// <summary>
/// Keeps a match inert until every expected human client has connected, then releases all
/// server-authoritative gameplay on the same five-second countdown boundary.
/// </summary>
public static class MatchStartGate
{
    private static readonly ConcurrentDictionary<long, State> States = new();

    public static bool IsSoloMapValidationEnabled =>
        Environment.GetEnvironmentVariable("SOLO_MAP_VALIDATION") == "1";

    /// <summary>탐사 모드에서 캠프 몹만 되살린다 — 봇 없이 몹 상대 검증용.</summary>
    public static bool IsSoloMonstersEnabled =>
        Environment.GetEnvironmentVariable("SOLO_MONSTERS") == "1";

    private static int MatchCapacity => IsSoloMapValidationEnabled
        ? 1
        : global::network.common.Config.SWARM_PLAYERS_PER_MATCH;

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

    public static bool IsAdmissionTimedOut(long matchingId, DateTime utcNow)
    {
        if (!States.TryGetValue(matchingId, out var state))
            return false;

        lock (state.SyncRoot)
        {
            return state.CountdownEndsAtUtc == null &&
                   utcNow - state.CreatedAtUtc >= MatchingHandoffRedisKeys.AdmissionTimeout;
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

    public static DateTime? GetGameplayStartedAtUtc(long matchingId)
    {
        if (!States.TryGetValue(matchingId, out var state))
            return null;

        lock (state.SyncRoot)
            return state.CountdownEndsAtUtc;
    }

    public static void RemoveMatching(long matchingId)
    {
        States.TryRemove(matchingId, out _);
    }

    private sealed class State
    {
        public object SyncRoot { get; } = new();
        public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;
        public HashSet<long> ConnectedHumanIds { get; } = [];
        public HashSet<long> ReadyHumanIds { get; } = [];
        public int ExpectedHumanCount { get; set; } = MatchCapacity;
        public DateTime? CountdownEndsAtUtc { get; set; }
    }
}

public readonly record struct MatchStartSnapshot(bool IsKnown, int RemainingSeconds);
