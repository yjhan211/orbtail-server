using game_server.services;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;

namespace demo_regression_tests;

public class ProximityCombatLineOfSightTests
{
    [Fact]
    public void Resolve_RejectsStaleSameAreaSnapshotAcrossRegionBoundary()
    {
        InitializeGameData();
        var resolver = new ProximityAutoCombatResolver();
        var now = new DateTime(2026, 7, 19, 0, 0, 0, DateTimeKind.Utc);
        var gymCell = new Cell(173, 88);
        var corridorCell = new Cell(172, 88);
        var actors = new[]
        {
            ActorAtCell(1, gymCell, AreaType.Gym, weaponItemId: 107000003),
            // 이동 처리 중 CurrentArea만 Gym으로 남은 상황을 재현한다.
            ActorAtCell(2, corridorCell, AreaType.Gym)
        };

        Assert.Equal(AreaType.Gym, GameMapData.GetCurrentArea(MapId.School, gymCell));
        Assert.Equal(AreaType.Corridor, GameMapData.GetCurrentArea(MapId.School, corridorCell));
        Assert.True(ProximityCombatLineOfSight.HasClearPath(MapId.School, gymCell, corridorCell));

        Assert.Empty(resolver.Resolve(100, actors, now, ProximityCombatLineOfSight.CanTarget));
        Assert.Empty(resolver.Resolve(
            100,
            actors,
            now.Add(ProximityAutoCombatResolver.AimDuration),
            ProximityCombatLineOfSight.CanTarget));
    }

    [Fact]
    public void Resolve_SkipsNearerTargetBehindGymWall_AndAttacksVisibleTarget()
    {
        InitializeGameData();
        var resolver = new ProximityAutoCombatResolver();
        var now = new DateTime(2026, 7, 19, 0, 0, 0, DateTimeKind.Utc);
        var attackerCell = new Cell(195, 93);
        var blockedTargetCell = new Cell(198, 93);
        var visibleTargetCell = new Cell(191, 93);
        var actors = new[]
        {
            ActorAtCell(1, attackerCell, AreaType.Gym, weaponItemId: 107000003),
            ActorAtCell(2, blockedTargetCell, AreaType.Gym),
            ActorAtCell(3, visibleTargetCell, AreaType.Gym)
        };

        Assert.False(ProximityCombatLineOfSight.HasClearPath(
            MapId.School,
            attackerCell,
            blockedTargetCell));
        Assert.True(ProximityCombatLineOfSight.HasClearPath(
            MapId.School,
            attackerCell,
            visibleTargetCell));

        Assert.Empty(resolver.Resolve(100, actors, now, ProximityCombatLineOfSight.CanTarget));
        var attack = Assert.Single(resolver.Resolve(
            100,
            actors,
            now.Add(ProximityAutoCombatResolver.AimDuration),
            ProximityCombatLineOfSight.CanTarget));

        Assert.Equal(1, attack.AttackerPlayerId);
        Assert.Equal(3, attack.TargetPlayerId);
        Assert.Equal(6, attack.Damage);
    }

    private static ProximityCombatActor ActorAtCell(
        long playerId,
        Cell cell,
        AreaType declaredArea,
        int weaponItemId = 0)
    {
        bool armed = weaponItemId > 0;
        var position = MapCoordinateConverter.CellToWorld(MapId.School, cell);
        return new ProximityCombatActor(
            playerId,
            declaredArea,
            position,
            weaponItemId,
            armed ? 9f : 0f,
            armed ? 6 : 0,
            armed ? 1.5f : 0f,
            armed ? 0.25f : 0f,
            0f,
            MapId.School,
            cell);
    }

    private static void InitializeGameData()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();
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
