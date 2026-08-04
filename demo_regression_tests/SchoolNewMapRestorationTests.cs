using System.Text.RegularExpressions;
using game_server.services;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;

namespace demo_regression_tests;

public class SchoolNewMapRestorationTests
{
    [Fact]
    public void Reinitializing_Map_Data_Replaces_Stale_Area_Regions()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();

        int firstAreaCount = GameMapData.GetAreas(MapId.School).Count;
        Assert.Equal(AreaType.Corridor, GameMapData.GetCurrentArea(MapId.School, new Cell(160, 100)));

        GameDataHelper.Initialize();

        Assert.Equal(firstAreaCount, GameMapData.GetAreas(MapId.School).Count);
        Assert.Equal(AreaType.Corridor, GameMapData.GetCurrentArea(MapId.School, new Cell(160, 100)));
    }

    [Fact]
    public void Stable_Area_Resolution_Requires_Entering_One_Cell_Past_A_Shared_Boundary()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();

        Assert.Equal(
            AreaType.Gym,
            GameMapData.GetStableCurrentArea(MapId.School, new Cell(196, 82), AreaType.Gym));
        Assert.Equal(
            AreaType.Storage2,
            GameMapData.GetStableCurrentArea(MapId.School, new Cell(197, 82), AreaType.Gym));
        Assert.Equal(
            AreaType.Storage2,
            GameMapData.GetStableCurrentArea(MapId.School, new Cell(195, 82), AreaType.Storage2));
        Assert.Equal(
            AreaType.Gym,
            GameMapData.GetStableCurrentArea(MapId.School, new Cell(194, 82), AreaType.Storage2));

        Assert.Equal(
            AreaType.StaffRoom,
            GameMapData.GetStableCurrentArea(MapId.School, new Cell(168, 70), AreaType.StaffRoom));
        Assert.Equal(
            AreaType.Junkyard2,
            GameMapData.GetStableCurrentArea(MapId.School, new Cell(169, 70), AreaType.StaffRoom));
        Assert.Equal(
            AreaType.Junkyard2,
            GameMapData.GetStableCurrentArea(MapId.School, new Cell(167, 70), AreaType.Junkyard2));
        Assert.Equal(
            AreaType.StaffRoom,
            GameMapData.GetStableCurrentArea(MapId.School, new Cell(166, 70), AreaType.Junkyard2));

        Assert.Equal(
            AreaType.Storage2,
            GameMapData.GetStableCurrentArea(MapId.School, new Cell(196, 82), AreaType.None));
    }

    [Fact]
    public void Stable_Area_Resolution_Allows_Every_Authored_Connection_To_Commit()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();

        foreach (var connection in GameAreaConnectionData.GetAll().Where(entry => entry.MapId == MapId.School))
        {
            AssertCanCommit(connection.FromArea, connection.ToArea, connection.SpawnCell);

            var reverseSpawn = GameAreaConnectionData.GetSpawnCell(
                MapId.School,
                connection.ToArea,
                connection.FromArea,
                connection.StairSide);
            Assert.NotNull(reverseSpawn);
            AssertCanCommit(connection.ToArea, connection.FromArea, reverseSpawn!);
        }

        static void AssertCanCommit(AreaType fromArea, AreaType toArea, Cell entryCell)
        {
            Assert.Equal(toArea, GameMapData.GetCurrentArea(MapId.School, entryCell));
            if (GameMapData.GetStableCurrentArea(MapId.School, entryCell, fromArea) == toArea)
                return;

            var innerCells = new[]
            {
                new Cell(entryCell.X - 1, entryCell.Y),
                new Cell(entryCell.X + 1, entryCell.Y),
                new Cell(entryCell.X, entryCell.Y - 1),
                new Cell(entryCell.X, entryCell.Y + 1)
            };
            Assert.Contains(innerCells, cell =>
                GameMapData.IsMoveablePosition(MapId.School, cell) &&
                GameMapData.GetCurrentArea(MapId.School, cell) == toArea &&
                GameMapData.GetStableCurrentArea(MapId.School, cell, fromArea) == toArea);
        }
    }

    [Fact]
    public void BotPathfinder_Crosses_School_Doors_As_Adjacent_Walking_Steps()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();

        var start = GameMapData.GetAreaSpawnCell(MapId.School, AreaType.Library);
        var target = GameAreaConnectionData.GetSpawnCell(
            MapId.School,
            AreaType.Library,
            AreaType.Storage);
        Assert.NotNull(target);

        var path = BotPathfinder.FindPath(
            MapId.School,
            AreaType.Library,
            start,
            AreaType.Storage,
            target!);
        Assert.NotNull(path);

        int transitionIndex = path!.FindIndex(step => step.IsAreaTransition);
        Assert.True(transitionIndex > 0);

        var exitStep = path[transitionIndex - 1];
        var entryStep = path[transitionIndex];
        Assert.Equal(AreaType.Library, exitStep.Area);
        Assert.Equal(AreaType.Storage, entryStep.Area);
        Assert.Equal(AreaType.Library, GameMapData.GetCurrentArea(MapId.School, exitStep.Cell));
        Assert.Equal(AreaType.Storage, GameMapData.GetCurrentArea(MapId.School, entryStep.Cell));
        Assert.InRange(Math.Abs(exitStep.Cell.X - entryStep.Cell.X), 0, 1);
        Assert.InRange(Math.Abs(exitStep.Cell.Y - entryStep.Cell.Y), 0, 1);
        Assert.NotEqual(exitStep.Cell, entryStep.Cell);
    }

    [Fact]
    public void Runtime_Obstacle_Overrides_Cached_Walkability_And_Clear_Restores_It()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();

        var walkableCell = new Cell(120, 108);
        Assert.True(GameMapData.IsMoveablePosition(MapId.School, walkableCell));

        try
        {
            GameMapData.SetRuntimeObstacles(
                MapId.School,
                new[] { new UnityEngine.Vector3Int(walkableCell.X, walkableCell.Y, 0) });
            Assert.False(GameMapData.IsMoveablePosition(MapId.School, walkableCell));
        }
        finally
        {
            GameMapData.ClearRuntimeObstacles(MapId.School);
        }

        Assert.True(GameMapData.IsMoveablePosition(MapId.School, walkableCell));
    }

    [Fact]
    public void School_Map_Uses_Legacy_Continuous_Map_Contract()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();

        var info = GameMapData.GetMapInfo(MapId.School);
        Assert.NotNull(info);
        Assert.Equal("School_New", info.SceneName);
        Assert.Equal(186, info.InitCell.position.x);
        Assert.Equal(93, info.InitCell.position.y);

        var spawn = new Cell(186, 93);
        Assert.Equal(AreaType.Gym, GameMapData.GetCurrentArea(MapId.School, spawn));
        Assert.True(GameMapData.IsMoveablePosition(MapId.School, spawn));

        var corridor = new Cell(120, 108);
        Assert.Equal(AreaType.Corridor, GameMapData.GetCurrentArea(MapId.School, corridor));
        Assert.True(GameMapData.GetCurrentArea(MapId.School, corridor).IsCorridor());
        Assert.True(GameMapData.IsMoveablePosition(MapId.School, corridor));

        Assert.Contains(
            GameMapData.GetMapRegions(MapId.School),
            region => region.RegionType.Equals("obstacle", StringComparison.OrdinalIgnoreCase));
        // 벽은 obstacle CSV가 막고, 고사실 문 앞의 비워 둔 셀은 통과 가능해야 한다.
        Assert.False(GameMapData.IsMoveablePosition(MapId.School, new Cell(118, 113)));
        Assert.True(GameMapData.IsMoveablePosition(MapId.School, new Cell(117, 114)));
        Assert.True(GameMapData.IsMoveablePosition(MapId.School, new Cell(82, 18)));
        Assert.False(GameMapData.IsMoveablePosition(MapId.School, new Cell(0, 0)));

        Assert.Equal(AreaType.ExamRoom, GameMapData.GetCurrentArea(MapId.School, new Cell(117, 113)));
        Assert.Equal(AreaType.BroadcastRoom, GameMapData.GetCurrentArea(MapId.School, new Cell(148, 113)));
        Assert.Equal(AreaType.Classroom2, GameMapData.GetCurrentArea(MapId.School, new Cell(181, 113)));
        Assert.Equal(AreaType.Gym, GameMapData.GetCurrentArea(MapId.School, new Cell(198, 90)));

        var schoolAreas = GameMapData.GetAreas(MapId.School)
            .Select(region => region.AreaType)
            .ToHashSet();
        var schoolConnections = GameAreaConnectionData.GetAll()
            .Where(connection => connection.MapId == MapId.School)
            .ToList();

        Assert.Equal(19, schoolConnections.Count);
        Assert.DoesNotContain(schoolConnections, connection =>
            connection.FromArea is AreaType.Corridor1F or AreaType.Corridor2F or AreaType.Corridor3F or AreaType.Corridor4F ||
            connection.ToArea is AreaType.Corridor1F or AreaType.Corridor2F or AreaType.Corridor3F or AreaType.Corridor4F);
        Assert.All(schoolConnections, connection =>
        {
            Assert.Contains(connection.FromArea, schoolAreas);
            Assert.Contains(connection.ToArea, schoolAreas);
            Assert.True(GameMapData.IsMoveablePosition(MapId.School, connection.SpawnCell));
            Assert.Equal(connection.ToArea, GameMapData.GetCurrentArea(MapId.School, connection.SpawnCell));

            var reverseSpawn = GameAreaConnectionData.GetSpawnCell(
                MapId.School,
                connection.ToArea,
                connection.FromArea,
                connection.StairSide);
            Assert.NotNull(reverseSpawn);
            Assert.True(GameMapData.IsMoveablePosition(MapId.School, reverseSpawn));
            Assert.Equal(connection.FromArea, GameMapData.GetCurrentArea(MapId.School, reverseSpawn));
        });
    }

    [Fact]
    public void DoorStateManager_CanCloseAndReopenAllDoorsForAnAreaIdempotently()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();

        const long matchingId = 99101;
        var manager = new DoorStateManager();
        manager.InitializeMatching(matchingId);

        var doorIds = GameDoorData.GetByAreaType(AreaType.ExamRoom)
            .Select(door => door.DoorId)
            .ToList();
        Assert.NotEmpty(doorIds);
        Assert.All(doorIds, doorId => Assert.True(manager.IsDoorOpen(matchingId, doorId)));

        Assert.Equal(doorIds.Order(), manager.CloseDoorsForAreas(matchingId, [AreaType.ExamRoom]).Order());
        Assert.Empty(manager.CloseDoorsForAreas(matchingId, [AreaType.ExamRoom]));
        Assert.All(doorIds, doorId => Assert.False(manager.IsDoorOpen(matchingId, doorId)));

        Assert.Equal(doorIds.Order(), manager.OpenDoorsForAreas(matchingId, [AreaType.ExamRoom]).Order());
        Assert.Empty(manager.OpenDoorsForAreas(matchingId, [AreaType.ExamRoom]));
        Assert.All(doorIds, doorId => Assert.True(manager.IsDoorOpen(matchingId, doorId)));
    }

    [Fact]
    public void DoorStateManager_InitializesAllStartingRoomDoorsLocked()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();

        const long matchingId = 193002;
        var manager = new DoorStateManager();
        var startingRooms = SurvivorRoyaleSpawnData.GetPhaseRoomCandidates();
        manager.InitializeMatching(matchingId, startingRooms);

        var startingDoorIds = startingRooms
            .SelectMany(GameDoorData.GetByAreaType)
            .Select(door => door.DoorId)
            .Distinct()
            .ToArray();

        Assert.NotEmpty(startingDoorIds);
        Assert.All(startingDoorIds, doorId => Assert.False(manager.IsDoorOpen(matchingId, doorId)));

        AreaType clearedRoom = startingRooms[0];
        var clearedDoorIds = GameDoorData.GetByAreaType(clearedRoom)
            .Select(door => door.DoorId)
            .ToArray();
        Assert.Equal(clearedDoorIds.Order(), manager.OpenDoorsForAreas(matchingId, [clearedRoom]).Order());
        Assert.All(clearedDoorIds, doorId => Assert.True(manager.IsDoorOpen(matchingId, doorId)));
    }
    [Fact]
    public void School_New_Does_Not_Load_Corridor_Stop_Penalty_Rule()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();

        Assert.DoesNotContain(
            GameAreaRuleData.GetByArea(AreaType.Corridor),
            rule => rule.Id == 6);
    }

    [Fact]
    public void School_New_Content_References_Are_Internally_Consistent()
    {
        var networkBasePath = FindNetworkBasePath();
        GameDataHelper.SetBasePath(networkBasePath);
        GameDataHelper.Initialize();

        var interactables = GameInteractableData.GetAll().ToDictionary(info => info.Id);
        Assert.Equal(61, interactables.Count);
        Assert.DoesNotContain(701000055, interactables.Keys);
        Assert.DoesNotContain(701000056, interactables.Keys);

        var csvDirectory = Path.Combine(networkBasePath, "Common", "csv");
        var referencedInteractables = Directory
            .EnumerateFiles(csvDirectory, "*.csv")
            .SelectMany(file => Regex
                .Matches(File.ReadAllText(file), @"(?<!\d)701\d{6}(?!\d)")
                .Cast<Match>()
                .Select(match => (File: Path.GetFileName(file), Id: int.Parse(match.Value))))
            .Distinct()
            .ToList();

        var missingReferences = referencedInteractables
            .Where(reference => !interactables.ContainsKey(reference.Id))
            .OrderBy(reference => reference.File)
            .ThenBy(reference => reference.Id)
            .ToList();
        Assert.True(
            missingReferences.Count == 0,
            $"Missing interactable definitions: {string.Join(", ", missingReferences)}");

        var misplacedRuleTargets = Enum
            .GetValues<AreaType>()
            .SelectMany(GameAreaRuleData.GetByArea)
            .Where(rule => rule.TargetInteractId > 0)
            .Where(rule =>
                !interactables.TryGetValue(rule.TargetInteractId, out var target) ||
                target.ZoneId != (int)rule.AreaType)
            .Select(rule => $"rule {rule.Id}: area {(int)rule.AreaType}, target {rule.TargetInteractId}")
            .ToList();
        Assert.True(
            misplacedRuleTargets.Count == 0,
            $"Area rules target objects in another area: {string.Join("; ", misplacedRuleTargets)}");

        var invalidRuleActions = Enum
            .GetValues<AreaType>()
            .SelectMany(GameAreaRuleData.GetByArea)
            .Where(rule => rule.TargetInteractId > 0 && rule.TargetActionId > 0)
            .Where(rule =>
                interactables.TryGetValue(rule.TargetInteractId, out var target) &&
                target.Actions.All(action => action.ActionId != rule.TargetActionId))
            .Select(rule => $"rule {rule.Id}: target {rule.TargetInteractId}, action {rule.TargetActionId}")
            .ToList();
        Assert.True(
            invalidRuleActions.Count == 0,
            $"Area rules target unavailable actions: {string.Join("; ", invalidRuleActions)}");

        Assert.Equal(AreaType.Gym, GameDoorData.Get(101)!.AreaType);
        Assert.Equal(AreaType.Classroom4, GameDoorData.Get(102)!.AreaType);
        Assert.Equal(AreaType.Classroom4, GameDoorData.Get(103)!.AreaType);
        Assert.Equal(AreaType.Classroom3, GameDoorData.Get(104)!.AreaType);
        Assert.Equal(AreaType.Classroom3, GameDoorData.Get(105)!.AreaType);
        Assert.Equal(AreaType.StaffRoom, GameDoorData.Get(106)!.AreaType);
        Assert.Equal(AreaType.ExamRoom, GameDoorData.Get(107)!.AreaType);
        Assert.Equal(AreaType.BroadcastRoom, GameDoorData.Get(108)!.AreaType);
        Assert.Equal(AreaType.Classroom2, GameDoorData.Get(109)!.AreaType);
        Assert.Equal(AreaType.Library, GameDoorData.Get(110)!.AreaType);
        Assert.Equal(AreaType.Library, GameDoorData.Get(111)!.AreaType);
        Assert.Equal(AreaType.Storage, GameDoorData.Get(112)!.AreaType);
        Assert.Equal(AreaType.Storage, GameDoorData.Get(113)!.AreaType);
        Assert.Equal(AreaType.Storage2, GameDoorData.Get(114)!.AreaType);
        Assert.Equal(AreaType.Storage2, GameDoorData.Get(115)!.AreaType);
        Assert.Equal(AreaType.Ground, GameDoorData.Get(116)!.AreaType);
        Assert.Equal(AreaType.AdminOffice, GameDoorData.Get(117)!.AreaType);
        Assert.Equal(AreaType.AdminOffice, GameDoorData.Get(118)!.AreaType);
        Assert.Equal(AreaType.StaffRoom, GameDoorData.Get(119)!.AreaType);
        Assert.Equal(19, GameDoorData.GetAll().Count());
        Assert.All(GameDoorData.GetAll(), door =>
        {
            Assert.True(door.IsInitiallyOpen);
            Assert.Equal(0, door.RequiredItemId);
            Assert.Equal(2f, door.InteractDistance);
        });

        var classroomDoor = GameDoorData.GetDoorForTransition(
            AreaType.Classroom4,
            AreaType.Corridor,
            new Cell(116, 91),
            new Cell(113, 91));
        Assert.NotNull(classroomDoor);
        Assert.Equal(102, classroomDoor.DoorId);
        Assert.Equal(2f, classroomDoor.InteractDistance);
        Assert.True(GameDoorData.IsOutsidePassageRadius(classroomDoor, new Cell(119, 91)));

        var examRoomDoor = GameDoorData.GetDoorForTransition(
            AreaType.ExamRoom,
            AreaType.Corridor,
            new Cell(117, 114),
            new Cell(117, 112));
        Assert.NotNull(examRoomDoor);
        Assert.Equal(107, examRoomDoor.DoorId);

        var infirmaryDoor = GameDoorData.GetDoorForTransition(
            AreaType.Classroom2,
            AreaType.Corridor,
            new Cell(181, 114),
            new Cell(180, 112));
        Assert.NotNull(infirmaryDoor);
        Assert.Equal(109, infirmaryDoor.DoorId);

        // The Library/Corridor boundary remains inside Door_110's two-cell interaction radius.
        // Passage detection must identify the door so relocking can wait for the player to leave it.
        var libraryCorridorDoor = GameDoorData.GetDoorForTransition(
            AreaType.Library,
            AreaType.Corridor,
            new Cell(103, 97),
            new Cell(103, 97));
        Assert.NotNull(libraryCorridorDoor);
        Assert.Equal(110, libraryCorridorDoor.DoorId);
        Assert.False(GameDoorData.IsOutsidePassageRadius(libraryCorridorDoor, new Cell(103, 97)));
        Assert.True(GameDoorData.IsOutsidePassageRadius(libraryCorridorDoor, new Cell(103, 93)));

        var libraryStorageDoor = GameDoorData.GetDoorForTransition(
            AreaType.Library,
            AreaType.Storage,
            new Cell(93, 72),
            new Cell(92, 71));
        Assert.NotNull(libraryStorageDoor);
        Assert.Equal(111, libraryStorageDoor.DoorId);
        Assert.True(GameMapData.IsMoveablePosition(MapId.School, new Cell(93, 72)));
        Assert.True(GameMapData.IsMoveablePosition(MapId.School, new Cell(92, 71)));

        Assert.Equal(2f, GameDoorData.Get(112)!.InteractDistance);
        Assert.True(GameDoorData.IsOutsidePassageRadius(GameDoorData.Get(112)!, new Cell(96, 57)));

        var storageGroundDoor = GameDoorData.GetDoorForTransition(
            AreaType.Storage,
            AreaType.Ground,
            new Cell(93, 57),
            new Cell(92, 56));
        Assert.NotNull(storageGroundDoor);
        Assert.Equal(112, storageGroundDoor.DoorId);
        Assert.True(GameMapData.IsMoveablePosition(MapId.School, new Cell(93, 57)));
        Assert.True(GameMapData.IsMoveablePosition(MapId.School, new Cell(92, 56)));

        Assert.True(GameAreaConnectionData.IsAdjacent(MapId.School, AreaType.Library, AreaType.Storage));
        Assert.Equal(new Cell(92, 71),
            GameAreaConnectionData.GetSpawnCell(MapId.School, AreaType.Library, AreaType.Storage));
        Assert.Equal(new Cell(93, 72),
            GameAreaConnectionData.GetSpawnCell(MapId.School, AreaType.Storage, AreaType.Library));
        Assert.Equal(new Cell(93, 57),
            GameAreaConnectionData.GetSpawnCell(MapId.School, AreaType.Ground, AreaType.Storage));
        Assert.Equal(new Cell(92, 56),
            GameAreaConnectionData.GetSpawnCell(MapId.School, AreaType.Storage, AreaType.Ground));

        var groundCorridorDoor = GameDoorData.GetDoorForTransition(
            AreaType.Ground,
            AreaType.Corridor,
            new Cell(140, 56),
            new Cell(141, 57));
        Assert.NotNull(groundCorridorDoor);
        Assert.Equal(116, groundCorridorDoor.DoorId);
        Assert.True(GameMapData.IsMoveablePosition(MapId.School, new Cell(140, 56)));
        Assert.True(GameMapData.IsMoveablePosition(MapId.School, new Cell(141, 57)));
        Assert.True(GameAreaConnectionData.IsAdjacent(MapId.School, AreaType.Ground, AreaType.Corridor));
        Assert.Equal(new Cell(141, 57),
            GameAreaConnectionData.GetSpawnCell(MapId.School, AreaType.Ground, AreaType.Corridor));
        Assert.Equal(new Cell(140, 56),
            GameAreaConnectionData.GetSpawnCell(MapId.School, AreaType.Corridor, AreaType.Ground));

        var adminOfficeDoor = GameDoorData.GetDoorForTransition(
            AreaType.AdminOffice,
            AreaType.Corridor,
            new Cell(132, 74),
            new Cell(133, 74));
        Assert.NotNull(adminOfficeDoor);
        Assert.Equal(117, adminOfficeDoor.DoorId);

        var westDumpDoor = GameDoorData.GetDoorForTransition(
            AreaType.AdminOffice,
            AreaType.Junkyard,
            new Cell(117, 74),
            new Cell(116, 74));
        Assert.NotNull(westDumpDoor);
        Assert.Equal(118, westDumpDoor.DoorId);

        var eastDumpDoor = GameDoorData.GetDoorForTransition(
            AreaType.StaffRoom,
            AreaType.Junkyard2,
            new Cell(167, 71),
            new Cell(168, 71));
        Assert.NotNull(eastDumpDoor);
        Assert.Equal(119, eastDumpDoor.DoorId);

        Assert.True(GameAreaConnectionData.IsAdjacent(MapId.School, AreaType.AdminOffice, AreaType.Junkyard));
        Assert.True(GameAreaConnectionData.IsAdjacent(MapId.School, AreaType.StaffRoom, AreaType.Junkyard2));
        Assert.Equal(new Cell(116, 74),
            GameAreaConnectionData.GetSpawnCell(MapId.School, AreaType.AdminOffice, AreaType.Junkyard));
        Assert.Equal(new Cell(167, 71),
            GameAreaConnectionData.GetSpawnCell(MapId.School, AreaType.Junkyard2, AreaType.StaffRoom));

        Assert.Null(GameDoorData.GetDoorForTransition(
            AreaType.Classroom4,
            AreaType.Corridor,
            new Cell(120, 95),
            new Cell(120, 94)));

        Assert.Equal(new[] { AreaType.BroadcastRoom }, GameRoomEventData.Get(188003)!.AreaTypes);
        Assert.Equal(new[] { AreaType.ExamRoom }, GameRoomEventData.Get(188004)!.AreaTypes);
        Assert.DoesNotContain(
            GameRoomEventData.GetByArea(AreaType.Corridor),
            roomEvent => roomEvent.EventId is 188003 or 188004);

        var poolAreas = File
            .ReadLines(Path.Combine(csvDirectory, "area_item_pool.csv"))
            .Skip(1)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => int.Parse(line[..line.IndexOf(',')]))
            .ToHashSet();
        var missingPoolAreas = GameMapData
            .GetAreas(MapId.School)
            .Select(area => (int)area.AreaType)
            .Distinct()
            .Where(area => !poolAreas.Contains(area))
            .OrderBy(area => area)
            .ToList();
        Assert.True(
            missingPoolAreas.Count == 0,
            $"School_New areas without an explicit item pool: {string.Join(", ", missingPoolAreas)}");
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
