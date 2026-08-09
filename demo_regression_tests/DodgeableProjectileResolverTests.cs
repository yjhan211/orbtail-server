using game_server.services;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;

namespace demo_regression_tests;

public class DodgeableProjectileResolverTests
{
    [Fact]
    public void Impact_HitsTargetThatRemainsAtLaunchAimPoint()
    {
        InitializeGameData();
        var resolver = new DodgeableProjectileResolver();
        var now = new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc);
        var actors = CreateGymActors(targetOffsetX: 0f);
        var attack = CreateAttack(107000010);

        var launch = Assert.Single(resolver.Queue(701, new[] { attack }, actors, now));

        Assert.Empty(resolver.ResolveImpacts(701, actors, launch.ImpactAtUtc.AddMilliseconds(-1)));
        var resolution = Assert.Single(resolver.ResolveImpacts(701, actors, launch.ImpactAtUtc));
        Assert.Equal("hit", resolution.Outcome);
        Assert.Equal(attack, Assert.Single(resolution.Hits));
    }

    [Fact]
    public void HopeImpact_HomesIntoTargetThatKeepsMovingInsideRange()
    {
        InitializeGameData();
        var resolver = new DodgeableProjectileResolver();
        var now = new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc);
        var launchActors = CreateGymActors(targetOffsetX: 0f);
        var launch = Assert.Single(resolver.Queue(
            702,
            new[] { CreateAttack(107000010) },
            launchActors,
            now));
        var movedActors = CreateGymActors(targetOffsetX: 2f);

        var resolution = Assert.Single(resolver.ResolveImpacts(702, movedActors, launch.ImpactAtUtc));
        Assert.Equal("hit", resolution.Outcome);
        Assert.Equal(2, Assert.Single(resolution.Hits).TargetPlayerId);
        Assert.True(resolution.TargetDisplacement > 0f);
    }

    [Fact]
    public void Impact_MissesTargetThatLeavesLaunchArea()
    {
        InitializeGameData();
        var resolver = new DodgeableProjectileResolver();
        var now = new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc);
        var launchActors = CreateGymActors(targetOffsetX: 0f);
        var launch = Assert.Single(resolver.Queue(
            703,
            new[] { CreateAttack(107000010) },
            launchActors,
            now));
        var escapedActors = new[]
        {
            launchActors[0],
            launchActors[1] with { Area = AreaType.Corridor }
        };

        var resolution = Assert.Single(resolver.ResolveImpacts(703, escapedActors, launch.ImpactAtUtc));
        Assert.Equal("target_left_area", resolution.Outcome);
        Assert.Empty(resolution.Hits);
    }

    [Fact]
    public void Impact_RecordsLineOfSightBlockedWhenTargetMovesBehindWall()
    {
        InitializeGameData();
        var resolver = new DodgeableProjectileResolver();
        var now = new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc);
        var launchActors = CreateGymActors(targetOffsetX: 0f);
        var launch = Assert.Single(resolver.Queue(
            705,
            new[] { CreateAttack(107000010) },
            launchActors,
            now));
        var blockedCell = new Cell(190, 64); // 강당(x164~187) 밖 공허 — 시야가 끊긴다
        var blockedActors = new[]
        {
            launchActors[0],
            launchActors[1] with
            {
                Cell = blockedCell,
                Position = MapCoordinateConverter.CellToWorld(MapId.School, blockedCell)
            }
        };

        var resolution = Assert.Single(resolver.ResolveImpacts(705, blockedActors, launch.ImpactAtUtc));

        Assert.Equal("line_of_sight_blocked", resolution.Outcome);
        Assert.Empty(resolution.Hits);
    }
    [Fact]
    public void BlueImpact_HitsOnlyItsTargetAfterPatternUnification()
    {
        // #219 M2: 파도 광역 텔레그래프 퇴역 — 전 색 유도 미사일이라 단일 대상만 맞는다.
        InitializeGameData();
        var resolver = new DodgeableProjectileResolver();
        var now = new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc);
        var actors = CreateGymActors(targetOffsetX: 0f).ToList();
        var target = actors[1];
        actors.Add(target with
        {
            PlayerId = 3,
            Position = new Vector3f(target.Position.X + 1f, target.Position.Y, target.Position.Z)
        });
        var launch = Assert.Single(resolver.Queue(
            704,
            new[] { CreateAttack(107000030) },
            actors,
            now));

        var resolution = Assert.Single(resolver.ResolveImpacts(704, actors, launch.ImpactAtUtc));

        Assert.Equal("hit", resolution.Outcome);
        var hit = Assert.Single(resolution.Hits);
        Assert.Equal(2, hit.TargetPlayerId);
        Assert.False(hit.IsWaveAreaSecondary);
    }

    [Fact]
    public void EveryOrbColor_QueuesHomingProjectile()
    {
        // #219 M2: 색=스탯 전환으로 공격 문법은 전 색 유도 미사일 통일 (회복 오브 제외).
        InitializeGameData();
        var resolver = new DodgeableProjectileResolver();
        var actors = CreateGymActors(targetOffsetX: 0f);
        var now = new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc);

        foreach (int itemId in (int[])[107000010, 107000020, 107000030])
        {
            var launches = resolver.Queue(706, new[] { CreateAttack(itemId) }, actors, now);
            Assert.Single(launches);
            Assert.Equal(SurvivorOrbAttackPattern.HomingProjectile,
                SurvivorOrbData.GetAttackPattern(itemId));
        }
    }
    private static ProximityCombatAttack CreateAttack(int itemId) => new(
        1,
        2,
        AreaType.Gym,
        itemId,
        6,
        0.25f,
        0f);

    private static ProximityCombatActor[] CreateGymActors(float targetOffsetX)
    {
        // #219 클론 맵: 강당(13) = (164,52)~(179,75)
        var attackerCell = new Cell(176, 64);
        var targetCell = new Cell(172, 64);
        Vector3f attackerPosition = MapCoordinateConverter.CellToWorld(MapId.School, attackerCell);
        Vector3f targetPosition = MapCoordinateConverter.CellToWorld(MapId.School, targetCell);
        targetPosition = new Vector3f(
            targetPosition.X + targetOffsetX,
            targetPosition.Y,
            targetPosition.Z);

        return new[]
        {
            CreateActor(1, attackerCell, attackerPosition, 107000010),
            CreateActor(2, targetCell, targetPosition, 0)
        };
    }

    private static ProximityCombatActor CreateActor(
        long playerId,
        Cell cell,
        Vector3f position,
        int weaponItemId)
    {
        bool armed = weaponItemId > 0;
        return new ProximityCombatActor(
            playerId,
            AreaType.Gym,
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
