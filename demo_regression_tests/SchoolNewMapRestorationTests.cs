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

        Assert.DoesNotContain(
            GameMapData.GetMapRegions(MapId.School),
            region => region.RegionType.Equals("obstacle", StringComparison.OrdinalIgnoreCase));
        Assert.True(GameMapData.IsMoveablePosition(MapId.School, new Cell(82, 18)));
        Assert.False(GameMapData.IsMoveablePosition(MapId.School, new Cell(0, 0)));

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
    public void DoorStateManager_Initializes_All_Doors_Open_And_Does_Not_Close_Them()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();

        const long matchingId = 99101;
        var manager = new DoorStateManager();
        manager.InitializeMatching(matchingId);

        var doorIds = GameDoorData.GetAll().Select(door => door.DoorId).ToList();
        Assert.Equal(doorIds.Order(), manager.GetOpenDoors(matchingId).Order());
        Assert.All(doorIds, doorId => Assert.True(manager.IsDoorOpen(matchingId, doorId)));
        Assert.False(manager.OpenDoor(matchingId, 101));
        Assert.True(manager.IsDoorOpen(matchingId, 101));
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
        });

        var classroomDoor = GameDoorData.GetDoorForTransition(
            AreaType.Classroom4,
            AreaType.Corridor,
            new Cell(116, 91),
            new Cell(113, 91));
        Assert.NotNull(classroomDoor);
        Assert.Equal(102, classroomDoor.DoorId);

        var examRoomDoor = GameDoorData.GetDoorForTransition(
            AreaType.ExamRoom,
            AreaType.Corridor,
            new Cell(117, 113),
            new Cell(117, 112));
        Assert.NotNull(examRoomDoor);
        Assert.Equal(107, examRoomDoor.DoorId);

        var infirmaryDoor = GameDoorData.GetDoorForTransition(
            AreaType.Classroom2,
            AreaType.Corridor,
            new Cell(181, 113),
            new Cell(180, 112));
        Assert.NotNull(infirmaryDoor);
        Assert.Equal(109, infirmaryDoor.DoorId);

        // The Library/Corridor boundary is one cell beyond the door's interaction radius.
        // Passage detection must identify Door_110 so relocking can wait for the player to leave its radius.
        var libraryCorridorDoor = GameDoorData.GetDoorForTransition(
            AreaType.Library,
            AreaType.Corridor,
            new Cell(103, 95),
            new Cell(103, 95));
        Assert.NotNull(libraryCorridorDoor);
        Assert.Equal(110, libraryCorridorDoor.DoorId);
        Assert.False(GameDoorData.IsOutsidePassageRadius(libraryCorridorDoor, new Cell(103, 95)));
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
