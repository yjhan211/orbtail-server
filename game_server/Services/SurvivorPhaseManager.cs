using System.Collections.Concurrent;
using network.common;

namespace game_server.services;

public enum SurvivorMatchPhase
{
    ROOM_COMBAT,
    ROOM_CLOSURE_WARNING,
    CORRIDOR_ENTRY,
    CORRIDOR_COMBAT,
    ROOM_SELECTION,
    CORRIDOR_CLOSURE_WARNING,
    FINAL,
    FINISHED
}

public sealed class SurvivorPhaseManager
{
    public const int RoomCombatSeconds = 45;
    public const int RoomClosureWarningSeconds = 10;
    public const int CorridorEntrySeconds = 3;
    public const int CorridorCombatSeconds = 7;
    public const int RoomSelectionSeconds = 3;
    public const int CorridorClosureWarningSeconds = 5;

    private static readonly int[] OpenRoomCounts = [8, 6, 4, 2];
    private static readonly AreaType[] RoomCandidates =
    [
        AreaType.ExamRoom,
        AreaType.BroadcastRoom,
        AreaType.Classroom2,
        AreaType.Classroom3,
        AreaType.Classroom4,
        AreaType.Storage,
        AreaType.Storage2,
        AreaType.AdminOffice,
        AreaType.StaffRoom
    ];

    private readonly ConcurrentDictionary<long, SurvivorPhaseState> _states = new();
    private readonly Func<DateTime> _utcNow;

    public SurvivorPhaseManager(Func<DateTime>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public bool HasMatching(long matchingId) => _states.ContainsKey(matchingId);

    public SurvivorPhaseSnapshot InitializeMatching(
        long matchingId,
        DateTime? gameplayStartsAtUtc = null,
        IReadOnlyCollection<AreaType>? occupiedStartingRooms = null)
    {
        if (matchingId <= 0)
            return SurvivorPhaseSnapshot.Empty;

        var state = _states.GetOrAdd(
            matchingId,
            id => CreateState(id, gameplayStartsAtUtc ?? _utcNow(), occupiedStartingRooms));
        lock (state.SyncRoot)
            return CreateSnapshot(state, _utcNow());
    }

    public SurvivorPhaseTick Tick(long matchingId, IReadOnlyCollection<long> alivePlayerIds)
    {
        if (!_states.TryGetValue(matchingId, out var state))
            return SurvivorPhaseTick.Empty;

        DateTime nowUtc = _utcNow();
        var transitions = new List<SurvivorPhaseTransition>();
        lock (state.SyncRoot)
        {
            state.AlivePlayerIds.Clear();
            state.AlivePlayerIds.UnionWith(alivePlayerIds.Where(id => id != 0));
            state.CoreClearPlayerIds.IntersectWith(state.AlivePlayerIds);

            bool shouldEndRoomEarly = state.Phase == SurvivorMatchPhase.ROOM_COMBAT &&
                                      state.AlivePlayerIds.Count > 0 &&
                                      state.CoreClearPlayerIds.Count >= RequiredCoreClearCount(state.AlivePlayerIds.Count);
            if (shouldEndRoomEarly)
                state.PhaseEndsAtUtc = nowUtc;

            int transitionGuard = 0;
            while (state.Phase is not (SurvivorMatchPhase.FINAL or SurvivorMatchPhase.FINISHED) &&
                   nowUtc >= state.PhaseEndsAtUtc && transitionGuard++ < 16)
            {
                SurvivorPhaseSnapshot before = CreateSnapshot(state, nowUtc);
                Advance(state);
                SurvivorPhaseSnapshot after = CreateSnapshot(state, nowUtc);
                transitions.Add(new SurvivorPhaseTransition(before, after));
            }

            return new SurvivorPhaseTick(CreateSnapshot(state, nowUtc), transitions);
        }
    }

    public bool ReportCoreDefeated(long matchingId, long playerId, AreaType area)
    {
        if (playerId == 0 || !_states.TryGetValue(matchingId, out var state))
            return false;

        lock (state.SyncRoot)
        {
            if (state.Phase != SurvivorMatchPhase.ROOM_COMBAT ||
                !state.CurrentRooms.Contains(area) ||
                state.AlivePlayerIds.Count > 0 && !state.AlivePlayerIds.Contains(playerId))
            {
                return false;
            }

            return state.CoreClearPlayerIds.Add(playerId);
        }
    }

    public SurvivorPhaseSnapshot GetSnapshot(long matchingId)
    {
        if (!_states.TryGetValue(matchingId, out var state))
            return SurvivorPhaseSnapshot.Empty;
        lock (state.SyncRoot)
            return CreateSnapshot(state, _utcNow());
    }

    public bool IsPvpAllowed(long matchingId, AreaType area)
    {
        var snapshot = GetSnapshot(matchingId);
        return snapshot.Phase switch
        {
            SurvivorMatchPhase.ROOM_COMBAT or SurvivorMatchPhase.ROOM_CLOSURE_WARNING =>
                snapshot.OpenAreas.Contains(area) && !area.IsCorridor(),
            SurvivorMatchPhase.CORRIDOR_COMBAT or SurvivorMatchPhase.ROOM_SELECTION or
                SurvivorMatchPhase.CORRIDOR_CLOSURE_WARNING => area.IsCorridor(),
            SurvivorMatchPhase.FINAL => area == AreaType.Ground,
            _ => false
        };
    }

    public bool IsPveAllowed(long matchingId, AreaType area)
    {
        var snapshot = GetSnapshot(matchingId);
        return snapshot.Phase switch
        {
            SurvivorMatchPhase.ROOM_COMBAT or SurvivorMatchPhase.ROOM_CLOSURE_WARNING =>
                snapshot.CurrentRooms.Contains(area),
            SurvivorMatchPhase.FINAL => area == AreaType.Ground,
            _ => false
        };
    }

    public bool AreOrbBoardActionsAllowed(long matchingId, AreaType area) => IsPveAllowed(matchingId, area);

    public static RoundPhase ToRoundPhase(SurvivorMatchPhase phase) => phase switch
    {
        SurvivorMatchPhase.ROOM_COMBAT => RoundPhase.SurvivorRoomCombat,
        SurvivorMatchPhase.ROOM_CLOSURE_WARNING => RoundPhase.SurvivorRoomClosureWarning,
        SurvivorMatchPhase.CORRIDOR_ENTRY => RoundPhase.SurvivorCorridorEntry,
        SurvivorMatchPhase.CORRIDOR_COMBAT => RoundPhase.SurvivorCorridorCombat,
        SurvivorMatchPhase.ROOM_SELECTION => RoundPhase.SurvivorRoomSelection,
        SurvivorMatchPhase.CORRIDOR_CLOSURE_WARNING => RoundPhase.SurvivorCorridorClosureWarning,
        SurvivorMatchPhase.FINAL => RoundPhase.SurvivorFinal,
        _ => RoundPhase.Ended
    };

    public static int GetPhaseDurationSeconds(SurvivorMatchPhase phase) => phase switch
    {
        SurvivorMatchPhase.ROOM_COMBAT => RoomCombatSeconds,
        SurvivorMatchPhase.ROOM_CLOSURE_WARNING => RoomClosureWarningSeconds,
        SurvivorMatchPhase.CORRIDOR_ENTRY => CorridorEntrySeconds,
        SurvivorMatchPhase.CORRIDOR_COMBAT => CorridorCombatSeconds,
        SurvivorMatchPhase.ROOM_SELECTION => RoomSelectionSeconds,
        SurvivorMatchPhase.CORRIDOR_CLOSURE_WARNING => CorridorClosureWarningSeconds,
        _ => 0
    };

    public void MarkFinished(long matchingId)
    {
        if (!_states.TryGetValue(matchingId, out var state))
            return;
        lock (state.SyncRoot)
        {
            state.Phase = SurvivorMatchPhase.FINISHED;
            state.PhaseStartedAtUtc = _utcNow();
            state.PhaseEndsAtUtc = DateTime.MaxValue;
        }
    }

    public void CleanupMatching(long matchingId) => _states.TryRemove(matchingId, out _);

    private static SurvivorPhaseState CreateState(
        long matchingId,
        DateTime startsAtUtc,
        IReadOnlyCollection<AreaType>? occupiedStartingRooms)
    {
        var random = new Random(unchecked((int)(matchingId ^ (matchingId >> 32))));
        var preferred = occupiedStartingRooms?
            .Where(RoomCandidates.Contains)
            .Distinct()
            .OrderBy(area => area)
            .ToList() ?? [];
        var remaining = RoomCandidates.Except(preferred).ToArray();
        for (int i = remaining.Length - 1; i > 0; i--)
        {
            int swapIndex = random.Next(i + 1);
            (remaining[i], remaining[swapIndex]) = (remaining[swapIndex], remaining[i]);
        }

        var orderedRooms = preferred.Concat(remaining).ToArray();

        return new SurvivorPhaseState
        {
            MatchingId = matchingId,
            Phase = SurvivorMatchPhase.ROOM_COMBAT,
            StageIndex = 0,
            OrderedRooms = orderedRooms,
            CurrentRooms = orderedRooms.Take(OpenRoomCounts[0]).ToHashSet(),
            NextRooms = orderedRooms.Take(OpenRoomCounts[1]).ToHashSet(),
            PhaseStartedAtUtc = startsAtUtc,
            PhaseEndsAtUtc = startsAtUtc.AddSeconds(RoomCombatSeconds)
        };
    }

    private static void Advance(SurvivorPhaseState state)
    {
        DateTime nextStartedAtUtc = state.PhaseEndsAtUtc;
        switch (state.Phase)
        {
            case SurvivorMatchPhase.ROOM_COMBAT:
                SetPhase(state, SurvivorMatchPhase.ROOM_CLOSURE_WARNING, nextStartedAtUtc,
                    RoomClosureWarningSeconds);
                break;
            case SurvivorMatchPhase.ROOM_CLOSURE_WARNING:
                SetPhase(state, SurvivorMatchPhase.CORRIDOR_ENTRY, nextStartedAtUtc, CorridorEntrySeconds);
                break;
            case SurvivorMatchPhase.CORRIDOR_ENTRY:
                SetPhase(state, SurvivorMatchPhase.CORRIDOR_COMBAT, nextStartedAtUtc, CorridorCombatSeconds);
                break;
            case SurvivorMatchPhase.CORRIDOR_COMBAT:
                SetPhase(state, SurvivorMatchPhase.ROOM_SELECTION, nextStartedAtUtc, RoomSelectionSeconds);
                break;
            case SurvivorMatchPhase.ROOM_SELECTION:
                SetPhase(state, SurvivorMatchPhase.CORRIDOR_CLOSURE_WARNING, nextStartedAtUtc,
                    CorridorClosureWarningSeconds);
                break;
            case SurvivorMatchPhase.CORRIDOR_CLOSURE_WARNING:
                state.StageIndex++;
                state.CoreClearPlayerIds.Clear();
                if (state.StageIndex >= OpenRoomCounts.Length)
                {
                    state.CurrentRooms.Clear();
                    state.NextRooms.Clear();
                    SetPhase(state, SurvivorMatchPhase.FINAL, nextStartedAtUtc, null);
                    break;
                }

                state.CurrentRooms = state.OrderedRooms.Take(OpenRoomCounts[state.StageIndex]).ToHashSet();
                state.NextRooms = state.StageIndex + 1 < OpenRoomCounts.Length
                    ? state.OrderedRooms.Take(OpenRoomCounts[state.StageIndex + 1]).ToHashSet()
                    : [];
                SetPhase(state, SurvivorMatchPhase.ROOM_COMBAT, nextStartedAtUtc, RoomCombatSeconds);
                break;
        }
    }

    private static void SetPhase(SurvivorPhaseState state, SurvivorMatchPhase phase, DateTime startsAtUtc,
        int? durationSeconds)
    {
        state.Phase = phase;
        state.PhaseStartedAtUtc = startsAtUtc;
        state.PhaseEndsAtUtc = durationSeconds.HasValue
            ? startsAtUtc.AddSeconds(durationSeconds.Value)
            : DateTime.MaxValue;
    }

    private static SurvivorPhaseSnapshot CreateSnapshot(SurvivorPhaseState state, DateTime nowUtc)
    {
        HashSet<AreaType> openAreas = state.Phase switch
        {
            SurvivorMatchPhase.ROOM_COMBAT or SurvivorMatchPhase.ROOM_CLOSURE_WARNING =>
                state.CurrentRooms.ToHashSet(),
            SurvivorMatchPhase.CORRIDOR_ENTRY or SurvivorMatchPhase.CORRIDOR_COMBAT =>
                [AreaType.Corridor],
            SurvivorMatchPhase.ROOM_SELECTION or SurvivorMatchPhase.CORRIDOR_CLOSURE_WARNING =>
                state.NextRooms.Append(AreaType.Corridor).ToHashSet(),
            SurvivorMatchPhase.FINAL => [AreaType.Ground],
            _ => []
        };
        HashSet<AreaType> warningAreas = state.Phase switch
        {
            SurvivorMatchPhase.ROOM_CLOSURE_WARNING => state.CurrentRooms.ToHashSet(),
            SurvivorMatchPhase.CORRIDOR_CLOSURE_WARNING => [AreaType.Corridor],
            _ => []
        };
        int remainingSeconds = state.PhaseEndsAtUtc == DateTime.MaxValue
            ? 0
            : Math.Max(0, (int)Math.Ceiling((state.PhaseEndsAtUtc - nowUtc).TotalSeconds));

        return new SurvivorPhaseSnapshot(
            state.MatchingId,
            state.Phase,
            state.StageIndex,
            OpenRoomCounts.ElementAtOrDefault(state.StageIndex),
            state.CurrentRooms.OrderBy(area => area).ToArray(),
            state.NextRooms.OrderBy(area => area).ToArray(),
            openAreas.OrderBy(area => area).ToArray(),
            warningAreas.OrderBy(area => area).ToArray(),
            remainingSeconds,
            state.PhaseStartedAtUtc,
            state.PhaseEndsAtUtc,
            state.CoreClearPlayerIds.Count,
            RequiredCoreClearCount(state.AlivePlayerIds.Count));
    }

    private static int RequiredCoreClearCount(int alivePlayerCount) =>
        alivePlayerCount <= 0 ? 0 : (alivePlayerCount + 1) / 2;

    private sealed class SurvivorPhaseState
    {
        public object SyncRoot { get; } = new();
        public long MatchingId { get; init; }
        public SurvivorMatchPhase Phase { get; set; }
        public int StageIndex { get; set; }
        public AreaType[] OrderedRooms { get; init; } = [];
        public HashSet<AreaType> CurrentRooms { get; set; } = [];
        public HashSet<AreaType> NextRooms { get; set; } = [];
        public HashSet<long> AlivePlayerIds { get; } = [];
        public HashSet<long> CoreClearPlayerIds { get; } = [];
        public DateTime PhaseStartedAtUtc { get; set; }
        public DateTime PhaseEndsAtUtc { get; set; }
    }
}

public readonly record struct SurvivorPhaseSnapshot(
    long MatchingId,
    SurvivorMatchPhase Phase,
    int StageIndex,
    int OpenRoomCount,
    IReadOnlyList<AreaType> CurrentRooms,
    IReadOnlyList<AreaType> NextRooms,
    IReadOnlyList<AreaType> OpenAreas,
    IReadOnlyList<AreaType> WarningAreas,
    int RemainingSeconds,
    DateTime PhaseStartedAtUtc,
    DateTime PhaseEndsAtUtc,
    int CoreClearCount,
    int RequiredCoreClearCount)
{
    public static SurvivorPhaseSnapshot Empty => new(
        0, SurvivorMatchPhase.FINISHED, 0, 0, [], [], [], [], 0, DateTime.MinValue, DateTime.MinValue, 0, 0);
}

public readonly record struct SurvivorPhaseTransition(
    SurvivorPhaseSnapshot Before,
    SurvivorPhaseSnapshot After);

public readonly record struct SurvivorPhaseTick(
    SurvivorPhaseSnapshot Snapshot,
    IReadOnlyList<SurvivorPhaseTransition> Transitions)
{
    public static SurvivorPhaseTick Empty => new(SurvivorPhaseSnapshot.Empty, []);
}
