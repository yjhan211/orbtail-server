using System.Collections.Concurrent;
using network.common;
using network.common.data;

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
    public const int FirstRoomCombatSeconds = 75;
    public const int RoomCombatSeconds = 45;
    public const int RoomClosureWarningSeconds = 10;
    public const int CorridorEntrySeconds = 3;
    public const int CorridorCombatSeconds = 7;
    public const int RoomSelectionSeconds = 3;
    public const int CorridorClosureWarningSeconds = 5;

    private static readonly int[] OpenRoomCounts = [8, 6, 4, 2];
    private static readonly AreaType[] RoomCandidates =
        SurvivorRoyaleSpawnData.GetPhaseRoomCandidates().ToArray();

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

    public bool ReportRoomCleared(long matchingId, AreaType area)
    {
        if (!_states.TryGetValue(matchingId, out var state))
            return false;

        lock (state.SyncRoot)
        {
            if (state.Phase != SurvivorMatchPhase.ROOM_COMBAT ||
                !state.CurrentRooms.Contains(area))
            {
                return false;
            }

            return state.ClearedRooms.Add(area);
        }
    }

    public bool IsRoomCleared(long matchingId, AreaType area)
    {
        if (!_states.TryGetValue(matchingId, out var state))
            return false;

        lock (state.SyncRoot)
            return state.ClearedRooms.Contains(area);
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
                snapshot.OpenAreas.Contains(area),
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

    public bool AreOrbBoardActionsAllowed(long matchingId, AreaType area)
    {
        var snapshot = GetSnapshot(matchingId);
        return snapshot.Phase switch
        {
            // 방 페이즈의 복도는 클리어한 플레이어의 대기·정비 공간이다. 소환·머지·파괴를
            // 방에서만 허용하면 클리어하고 나온 순간 보드가 잠겨 "머지가 안 되는" 경험이 된다.
            SurvivorMatchPhase.ROOM_COMBAT or SurvivorMatchPhase.ROOM_CLOSURE_WARNING =>
                snapshot.OpenAreas.Contains(area),
            SurvivorMatchPhase.FINAL => area == AreaType.Ground,
            _ => false
        };
    }

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

    public static int GetPhaseDurationSeconds(SurvivorPhaseSnapshot snapshot) =>
        snapshot.PhaseEndsAtUtc == DateTime.MaxValue
            ? 0
            : Math.Max(0,
                (int)Math.Round((snapshot.PhaseEndsAtUtc - snapshot.PhaseStartedAtUtc).TotalSeconds));

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
        var stageRooms = BuildStageRooms(orderedRooms, random);

        return new SurvivorPhaseState
        {
            MatchingId = matchingId,
            Phase = SurvivorMatchPhase.ROOM_COMBAT,
            StageIndex = 0,
            OrderedRooms = orderedRooms,
            StageRooms = stageRooms,
            CurrentRooms = stageRooms[0].ToHashSet(),
            NextRooms = stageRooms[1].ToHashSet(),
            PhaseStartedAtUtc = startsAtUtc,
            PhaseEndsAtUtc = startsAtUtc.AddSeconds(FirstRoomCombatSeconds)
        };
    }

    private static AreaType[][] BuildStageRooms(AreaType[] orderedRooms, Random random)
    {
        var stages = new AreaType[OpenRoomCounts.Length][];
        stages[0] = orderedRooms.Take(OpenRoomCounts[0]).ToArray();

        for (int stageIndex = 1; stageIndex < OpenRoomCounts.Length; stageIndex++)
        {
            var previous = stages[stageIndex - 1].ToHashSet();
            var outsidePrevious = orderedRooms.Where(area => !previous.Contains(area)).ToList();
            var insidePrevious = orderedRooms.Where(previous.Contains).ToList();
            Shuffle(outsidePrevious, random);
            Shuffle(insidePrevious, random);
            stages[stageIndex] = outsidePrevious
                .Concat(insidePrevious)
                .Take(OpenRoomCounts[stageIndex])
                .ToArray();
        }

        return stages;
    }

    private static void Shuffle<T>(IList<T> values, Random random)
    {
        for (int index = values.Count - 1; index > 0; index--)
        {
            int swapIndex = random.Next(index + 1);
            (values[index], values[swapIndex]) = (values[swapIndex], values[index]);
        }
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
                state.ClearedRooms.Clear();
                if (state.StageIndex >= OpenRoomCounts.Length)
                {
                    state.CurrentRooms.Clear();
                    state.NextRooms.Clear();
                    SetPhase(state, SurvivorMatchPhase.FINAL, nextStartedAtUtc, null);
                    break;
                }

                state.CurrentRooms = state.StageRooms[state.StageIndex].ToHashSet();
                state.NextRooms = state.StageIndex + 1 < OpenRoomCounts.Length
                    ? state.StageRooms[state.StageIndex + 1].ToHashSet()
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
            // 방 페이즈에도 복도는 개방이다. 방 커밋은 잠긴 문이 강제하므로 복도에 나올 수
            // 있는 것은 방을 클리어한 플레이어뿐이고, 복도를 폐쇄하면 클리어 보상(조기 진출·
            // 선점·교전)이 폐쇄 피해로 바뀐다. 복도 잔상은 여전히 생성하지 않는다.
            SurvivorMatchPhase.ROOM_COMBAT or SurvivorMatchPhase.ROOM_CLOSURE_WARNING =>
                state.CurrentRooms.Append(AreaType.Corridor).ToHashSet(),
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
            state.ClearedRooms.OrderBy(area => area).ToArray(),
            state.ClearedRooms.Count,
            state.CurrentRooms.Count);
    }

    private sealed class SurvivorPhaseState
    {
        public object SyncRoot { get; } = new();
        public long MatchingId { get; init; }
        public SurvivorMatchPhase Phase { get; set; }
        public int StageIndex { get; set; }
        public AreaType[] OrderedRooms { get; init; } = [];
        public AreaType[][] StageRooms { get; init; } = [];
        public HashSet<AreaType> CurrentRooms { get; set; } = [];
        public HashSet<AreaType> NextRooms { get; set; } = [];
        public HashSet<long> AlivePlayerIds { get; } = [];
        public HashSet<AreaType> ClearedRooms { get; } = [];
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
    IReadOnlyList<AreaType> ClearedRooms,
    int CoreClearCount,
    int RequiredCoreClearCount)
{
    public static SurvivorPhaseSnapshot Empty => new(
        0, SurvivorMatchPhase.FINISHED, 0, 0, [], [], [], [], 0, DateTime.MinValue, DateTime.MinValue, [], 0, 0);
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
