using game_server.services;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;

namespace demo_regression_tests;

public class SwarmArenaManagerTests
{
    private static readonly DateTime StartUtc =
        new(2026, 8, 6, 0, 0, 0, DateTimeKind.Utc);

    public SwarmArenaManagerTests()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();
    }

    [Fact]
    public void FirstPattern_SpawnsRingWithTelegraph()
    {
        DateTime now = StartUtc;
        var manager = CreateManager(() => now);
        Vector3f center = AreaCenter(AreaType.Ground);

        // 첫 틱에 스케줄이 잡히고, 3초 뒤 첫 패턴이 나온다.
        manager.Tick(217001, Participants(center), StartUtc.AddSeconds(0.25));
        now = StartUtc.AddSeconds(3.5);
        var tick = manager.Tick(217001, Participants(center), now);

        Assert.Equal(SwarmArenaManager.RingSpawnCount, tick.SpawnedMonsters.Count);
        Assert.All(tick.SpawnedMonsters, monster =>
        {
            Assert.Equal(SwarmArenaManager.MonsterMaxHealth, monster.CurrentHealth);
            Assert.True(monster.IsAlive);
            Assert.Equal(AreaType.Ground, monster.AreaType);
        });

        // 예고 시간 안에는 전투 대상으로 잡히지 않는다.
        Assert.Empty(manager.GetCombatTargets(217001));
        now = StartUtc.AddSeconds(3.5 + SwarmArenaManager.RingTelegraphSeconds + 0.1);
        Assert.Equal(SwarmArenaManager.RingSpawnCount, manager.GetCombatTargets(217001).Count);
    }

    [Fact]
    public void Corridor_NeverSpawnsSwarm()
    {
        DateTime now = StartUtc;
        var manager = CreateManager(() => now);
        Vector3f corridor = AreaCenter(AreaType.Corridor);

        for (double elapsed = 0.25d; elapsed <= 15d; elapsed += 0.25d)
        {
            now = StartUtc.AddSeconds(elapsed);
            manager.Tick(217001, Participants(corridor, AreaType.Corridor), now);
        }

        Assert.Empty(manager.GetVisualStates(217001));
    }

    [Fact]
    public void AreaDensity_StaysWithinProfileCap()
    {
        DateTime now = StartUtc;
        var manager = CreateManager(() => now);
        Vector3f center = AreaCenter(AreaType.Ground);

        for (double elapsed = 0.25d; elapsed <= 40d; elapsed += 0.25d)
        {
            now = StartUtc.AddSeconds(elapsed);
            manager.Tick(217001, Participants(center), now);
            int alive = manager.GetVisualStates(217001).Count(state => state.IsAlive);
            Assert.True(alive <= 14, $"운동장 밀도 상한 14를 초과했다: {alive}");
        }
    }

    [Fact]
    public void ConvergingMonsters_DealContactDamageWithImmunityWindow()
    {
        DateTime now = StartUtc;
        var manager = CreateManager(() => now);
        Vector3f center = AreaCenter(AreaType.Ground);

        var damageEvents = new List<SpotArenaPlayerDamage>();
        for (double elapsed = 3d; elapsed <= 12d; elapsed += 0.25d)
        {
            now = StartUtc.AddSeconds(elapsed);
            damageEvents.AddRange(manager.Tick(217001, Participants(center), now).PlayerDamage);
        }

        Assert.NotEmpty(damageEvents);
        Assert.All(damageEvents, damage =>
        {
            Assert.Equal(SwarmArenaManager.ContactDamage, damage.Damage);
            Assert.Equal(1, damage.TargetPlayerId);
        });
        Assert.Equal(damageEvents.Count, manager.GetSummary(217001).HitsTaken);
    }

    [Fact]
    public void PlayerAttacks_KillMonstersAndCountKills()
    {
        DateTime now = StartUtc;
        var manager = CreateManager(() => now);
        Vector3f center = AreaCenter(AreaType.Ground);

        manager.Tick(217001, Participants(center), StartUtc.AddSeconds(0.25));
        now = StartUtc.AddSeconds(3.5);
        manager.Tick(217001, Participants(center), now);
        now = StartUtc.AddSeconds(4.7);
        manager.Tick(217001, Participants(center), now);

        var target = manager.GetCombatTargets(217001).First();
        var result = manager.ApplyMonsterDamage(
            217001, target.CombatTargetId, attackerPlayerId: 1, SwarmArenaManager.MonsterMaxHealth);

        Assert.True(result.Applied);
        Assert.True(result.Killed);
        Assert.Equal(target.MonsterId, result.MonsterId);
        Assert.Equal(1, manager.GetSummary(217001).Kills);
        Assert.DoesNotContain(
            manager.GetCombatTargets(217001),
            candidate => candidate.CombatTargetId == target.CombatTargetId);
    }

    [Fact]
    public void Monsters_OnlyChaseAndBiteSameAreaParticipants()
    {
        DateTime now = StartUtc;
        var manager = CreateManager(() => now);
        Vector3f ground = AreaCenter(AreaType.Ground);
        Vector3f corridor = AreaCenter(AreaType.Corridor);

        var participants = new[]
        {
            new SpotArenaPlayerSpatial(1, AreaType.Ground, ground),
            new SpotArenaPlayerSpatial(2, AreaType.Corridor, corridor)
        };

        var damageEvents = new List<SpotArenaPlayerDamage>();
        for (double elapsed = 3d; elapsed <= 12d; elapsed += 0.25d)
        {
            now = StartUtc.AddSeconds(elapsed);
            damageEvents.AddRange(manager.Tick(217001, participants, now).PlayerDamage);
        }

        // 운동장 스웜은 운동장의 1번만 문다. 복도의 2번은 무관하다.
        Assert.NotEmpty(damageEvents);
        Assert.All(damageEvents, damage => Assert.Equal(1, damage.TargetPlayerId));
    }

    private static IReadOnlyCollection<SpotArenaPlayerSpatial> Participants(
        Vector3f position,
        AreaType area = AreaType.Ground) =>
        [new SpotArenaPlayerSpatial(1, area, position)];

    private static SwarmArenaManager CreateManager(Func<DateTime> clock)
    {
        var manager = new SwarmArenaManager(clock);
        Assert.True(manager.InitializeMatching(217001, 1, StartUtc));
        return manager;
    }

    private static Vector3f AreaCenter(AreaType area) =>
        MapCoordinateConverter.CellToWorld(
            MapId.School, GameMapData.GetAreaSpawnCell(MapId.School, area));

    private static string FindNetworkBasePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "network", "Common", "csv");
            if (Directory.Exists(candidate))
                return Path.Combine(directory.FullName, "network");
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate network/Common/csv.");
    }
}
