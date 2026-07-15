using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;
using System.Text.RegularExpressions;

namespace demo_regression_tests;

public class SchoolNewMapRestorationTests
{
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
    }

    [Fact]
    public void School_New_Content_References_Are_Internally_Consistent()
    {
        var networkBasePath = FindNetworkBasePath();
        GameDataHelper.SetBasePath(networkBasePath);
        GameDataHelper.Initialize();

        var interactables = GameInteractableData.GetAll().ToDictionary(info => info.Id);
        Assert.Equal(56, interactables.Count);

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
        Assert.All(GameDoorData.GetAll(), door => Assert.True(door.IsInitiallyOpen));

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