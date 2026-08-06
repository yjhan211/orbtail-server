using game_server.services;
using network.common;
using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

public class SurvivorPhaseManagerTests
{
    [Fact]
    public void Initialize_OpensExactSixStartingRoomsAndCorridorStaysSafe()
    {
        DateTime now = new(2026, 8, 4, 0, 0, 0, DateTimeKind.Utc);
        var manager = new SurvivorPhaseManager(() => now);

        var first = manager.InitializeMatching(214001, now);
        var second = manager.InitializeMatching(214001, now.AddMinutes(1));

        Assert.Equal(SurvivorMatchPhase.ROOM_COMBAT, first.Phase);
        Assert.Equal(6, first.CurrentRooms.Count);
        // 방 커밋은 잠긴 문이 강제한다. 복도는 개방이라 클리어한 플레이어의 조기 진출이
        // 폐쇄 피해로 벌받지 않고, 클리어자끼리의 복도 교전(PvP)은 허용된다.
        Assert.Equal(7, first.OpenAreas.Count);
        Assert.Contains(AreaType.Corridor, first.OpenAreas);
        Assert.Equal(first.CurrentRooms, second.CurrentRooms);
        Assert.True(manager.IsPveAllowed(214001, first.CurrentRooms[0]));
        Assert.True(manager.IsPvpAllowed(214001, first.CurrentRooms[0]));
        Assert.True(manager.IsPvpAllowed(214001, AreaType.Corridor));
        Assert.False(manager.IsPveAllowed(214001, AreaType.Corridor));
        // 방 페이즈의 복도는 클리어자의 대기·정비 공간 — 소환·머지·파괴가 허용된다.
        Assert.True(manager.AreOrbBoardActionsAllowed(214001, AreaType.Corridor));
        // #217 6인 3쌍 깔때기: 시작방은 고사실·창고들·보건실·행정실·교무실 여섯 곳뿐이다.
        Assert.DoesNotContain(AreaType.Junkyard2, first.CurrentRooms);
        Assert.DoesNotContain(AreaType.Gym, first.CurrentRooms);
    }

    [Fact]
    public void ClearingOneRoom_UnlocksOnlyThatRoomAndDoesNotEndPhaseEarly()
    {
        DateTime now = new(2026, 8, 4, 0, 0, 0, DateTimeKind.Utc);
        var manager = new SurvivorPhaseManager(() => now);
        var initial = manager.InitializeMatching(214002, now);
        AreaType room = initial.CurrentRooms[0];
        long[] alive = [1, 2, 3, 4, 5, 6, 7, 8];
        manager.Tick(214002, alive);

        Assert.True(manager.ReportRoomCleared(214002, room));
        Assert.False(manager.ReportRoomCleared(214002, room));
        Assert.True(manager.IsRoomCleared(214002, room));

        var tick = manager.Tick(214002, alive);
        Assert.Equal(SurvivorMatchPhase.ROOM_COMBAT, tick.Snapshot.Phase);
        Assert.Equal(75, tick.Snapshot.RemainingSeconds);
        Assert.Equal([room], tick.Snapshot.ClearedRooms);
        Assert.Empty(tick.Transitions);
    }

    [Fact]
    public void CorridorPhases_ApplyProtectionCombatSelectionAndWarningWindows()
    {
        DateTime now = new(2026, 8, 4, 0, 0, 0, DateTimeKind.Utc);
        var manager = new SurvivorPhaseManager(() => now);
        manager.InitializeMatching(214003, now);
        long[] alive = [1, 2, 3, 4, 5, 6, 7, 8];

        now = now.AddSeconds(85);
        var entry = manager.Tick(214003, alive).Snapshot;
        Assert.Equal(SurvivorMatchPhase.CORRIDOR_ENTRY, entry.Phase);
        Assert.Equal([AreaType.Corridor], entry.OpenAreas);
        Assert.False(manager.IsPvpAllowed(214003, AreaType.Corridor));
        Assert.False(manager.AreOrbBoardActionsAllowed(214003, AreaType.Corridor));

        now = now.AddSeconds(3);
        Assert.Equal(SurvivorMatchPhase.CORRIDOR_COMBAT, manager.Tick(214003, alive).Snapshot.Phase);
        Assert.True(manager.IsPvpAllowed(214003, AreaType.Corridor));

        now = now.AddSeconds(7);
        var selection = manager.Tick(214003, alive).Snapshot;
        Assert.Equal(SurvivorMatchPhase.ROOM_SELECTION, selection.Phase);
        Assert.Equal(4, selection.NextRooms.Count);
        Assert.All(selection.NextRooms, area => Assert.Contains(area, selection.OpenAreas));

        now = now.AddSeconds(3);
        var warning = manager.Tick(214003, alive).Snapshot;
        Assert.Equal(SurvivorMatchPhase.CORRIDOR_CLOSURE_WARNING, warning.Phase);
        Assert.Equal([AreaType.Corridor], warning.WarningAreas);
        Assert.Equal(5, warning.RemainingSeconds);
    }

    [Fact]
    public void FullSchedule_UsesSixFourTwoRoomsThenGroundFinal()
    {
        DateTime now = new(2026, 8, 4, 0, 0, 0, DateTimeKind.Utc);
        var manager = new SurvivorPhaseManager(() => now);
        var initial = manager.InitializeMatching(214004, now);
        long[] alive = [1, 2, 3, 4, 5, 6];

        Assert.Equal(6, initial.OpenRoomCount);
        Assert.Equal(SurvivorPhaseManager.FirstRoomCombatSeconds,
            SurvivorPhaseManager.GetPhaseDurationSeconds(initial));

        var allowedRooms = SurvivorRoyaleSpawnData.GetPhaseRoomCandidates().ToHashSet();
        for (int expectedStage = 1; expectedStage < 3; expectedStage++)
        {
            now = now.AddSeconds(expectedStage == 1 ? 103 : 73);
            var snapshot = manager.Tick(214004, alive).Snapshot;
            Assert.Equal(SurvivorMatchPhase.ROOM_COMBAT, snapshot.Phase);
            Assert.Equal(expectedStage, snapshot.StageIndex);
            Assert.Equal(new[] { 6, 4, 2 }[expectedStage], snapshot.CurrentRooms.Count);
            Assert.All(snapshot.CurrentRooms, area => Assert.Contains(area, allowedRooms));
            Assert.Equal(snapshot.CurrentRooms.Count, snapshot.CurrentRooms.Distinct().Count());
            Assert.Equal(SurvivorPhaseManager.RoomCombatSeconds,
                SurvivorPhaseManager.GetPhaseDurationSeconds(snapshot));
        }

        now = now.AddSeconds(73);
        var final = manager.Tick(214004, alive).Snapshot;
        Assert.Equal(SurvivorMatchPhase.FINAL, final.Phase);
        Assert.Equal([AreaType.Ground], final.OpenAreas);
        Assert.True(manager.IsPvpAllowed(214004, AreaType.Ground));
        Assert.True(manager.IsPveAllowed(214004, AreaType.Ground));
    }

    [Fact]
    public void Initialize_KeepsAllOccupiedStartingRoomsOpen()
    {
        DateTime now = new(2026, 8, 4, 0, 0, 0, DateTimeKind.Utc);
        var manager = new SurvivorPhaseManager(() => now);
        AreaType[] occupied =
        [
            AreaType.ExamRoom,
            AreaType.Storage,
            AreaType.Classroom2,
            AreaType.Storage2,
            AreaType.AdminOffice,
            AreaType.StaffRoom
        ];

        var initial = manager.InitializeMatching(214006, now, occupied);

        Assert.Equal(6, initial.CurrentRooms.Count);
        Assert.All(occupied, area => Assert.Contains(area, initial.CurrentRooms));
        Assert.DoesNotContain(AreaType.Corridor, initial.CurrentRooms);
    }

    [Fact]
    public void PhaseRoomAssignments_UseSixUniqueWalkableRoomCells()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();
        long[] playerIds = Enumerable.Range(1, 6).Select(value => (long)value).ToArray();
        HashSet<AreaType> expectedRooms =
        [
            AreaType.ExamRoom,
            AreaType.Storage,
            AreaType.Classroom2,
            AreaType.Storage2,
            AreaType.AdminOffice,
            AreaType.StaffRoom
        ];

        var assignments = SurvivorRoyaleSpawnData.CreatePhaseRoomAssignments(214007, playerIds);
        var cells = assignments.Values.ToArray();
        var resolvedAreas = cells
            .Select(cell => GameMapData.GetCurrentArea(MapId.School, cell))
            .ToArray();

        Assert.Equal(6, assignments.Count);
        Assert.Equal(6, cells.Select(cell => (cell.X, cell.Y)).Distinct().Count());
        Assert.Equal(6, resolvedAreas.Distinct().Count());
        Assert.All(cells, cell =>
        {
            Assert.NotEqual((0, 0), (cell.X, cell.Y));
            Assert.True(GameMapData.IsMoveablePosition(MapId.School, cell));
        });
        Assert.Equal(expectedRooms.OrderBy(area => area), resolvedAreas.OrderBy(area => area));
        Assert.DoesNotContain(AreaType.Gym, resolvedAreas);
    }

    [Fact]
    public void Cleanup_RemovesMatchState()
    {
        var manager = new SurvivorPhaseManager();
        manager.InitializeMatching(214005);
        manager.CleanupMatching(214005);
        Assert.Equal(SurvivorPhaseSnapshot.Empty, manager.GetSnapshot(214005));
    }

    private static string FindNetworkBasePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "network", "Common", "csv");
            if (Directory.Exists(candidate))
                return Path.Combine(dir.FullName, "network");

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate network/Common/csv from test output path.");
    }
}
