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

    [Fact(Skip = "유도탄 복귀(2026-08-12): 회피 패턴(TargetArea)을 쓰는 색이 없다 — 부활 시 재작성")]
    public void SunImpact_MissesTargetThatMovedAfterLaunch()
    {
        // 유도탄 복귀 (2026-08-12): 태양이 HomingProjectile로 돌아가 리졸버 안에서도 확정
        // 명중이다 — 회피 문법(TargetArea)은 현재 어떤 색도 쓰지 않아 도달 불가. 부활 시 재작성.
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
        Assert.Equal("dodged", resolution.Outcome);
        Assert.Empty(resolution.Hits);
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
    public void OrbColors_HaveWeaponVerbPatterns()
    {
        // 유도탄 복귀 (2026-08-12): 태양·바람 = 착탄 확정 유도탄(클라 표적 추적),
        // 파도=미사일 없음(None — 물폭탄은 서버 별도 주기).
        InitializeGameData();
        Assert.Equal(OrbAttackPattern.HomingProjectile,
            OrbData.GetAttackPattern(107000010));
        Assert.Equal(OrbAttackPattern.HomingProjectile,
            OrbData.GetAttackPattern(107000020));
        Assert.Equal(OrbAttackPattern.None,
            OrbData.GetAttackPattern(107000030));
    }

    [Fact]
    public void OnlySunOrb_QueuesDodgeableProjectile()
    {
        // #226 색=무기 동사: 회피 가능 투사체는 태양(유도)만 큐잉된다 —
        // 바람은 즉발 런지, 파도는 투사체가 없다.
        InitializeGameData();
        var resolver = new DodgeableProjectileResolver();
        var actors = CreateGymActors(targetOffsetX: 0f);
        var now = new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc);

        Assert.Single(resolver.Queue(706, new[] { CreateAttack(107000010) }, actors, now));
        Assert.Empty(resolver.Queue(707, new[] { CreateAttack(107000030) }, actors, now));
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
