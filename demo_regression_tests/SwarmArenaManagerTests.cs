using game_server.services;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;

namespace demo_regression_tests;

public class SwarmArenaManagerTests
{
    private static readonly DateTime StartUtc =
        new(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc);

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
        Vector3f center = GroundCenter();

        now = StartUtc.AddSeconds(3);
        var tick = manager.Tick(217001, center, now);

        Assert.Equal(SwarmArenaManager.RingSpawnCount, tick.SpawnedMonsters.Count);
        Assert.All(tick.SpawnedMonsters, monster =>
        {
            Assert.Equal(SwarmArenaManager.MonsterMaxHealth, monster.CurrentHealth);
            Assert.True(monster.IsAlive);
        });

        // 예고 시간 안에는 전투 대상으로 잡히지 않는다.
        Assert.Empty(manager.GetCombatTargets(217001));
        now = StartUtc.AddSeconds(3 + SwarmArenaManager.RingTelegraphSeconds + 0.1);
        Assert.Equal(SwarmArenaManager.RingSpawnCount, manager.GetCombatTargets(217001).Count);
    }

    [Fact]
    public void ConvergingMonsters_DealContactDamageWithCooldown()
    {
        DateTime now = StartUtc;
        var manager = CreateManager(() => now);
        Vector3f center = GroundCenter();

        var damageEvents = new List<SpotArenaPlayerDamage>();
        for (double elapsed = 3d; elapsed <= 12d; elapsed += 0.25d)
        {
            now = StartUtc.AddSeconds(elapsed);
            damageEvents.AddRange(manager.Tick(217001, center, now).PlayerDamage);
        }

        Assert.NotEmpty(damageEvents);
        Assert.All(damageEvents, damage =>
        {
            Assert.Equal(SwarmArenaManager.ContactDamage, damage.Damage);
            Assert.Equal(1, damage.TargetPlayerId);
        });

        var summary = manager.GetSummary(217001);
        Assert.Equal(damageEvents.Count, summary.HitsTaken);
        Assert.True(summary.PatternHits.Values.Sum() == damageEvents.Count);
    }

    [Fact]
    public void PlayerAttacks_KillMonstersAndCountKills()
    {
        DateTime now = StartUtc;
        var manager = CreateManager(() => now);
        Vector3f center = GroundCenter();

        now = StartUtc.AddSeconds(3);
        manager.Tick(217001, center, now);
        now = StartUtc.AddSeconds(4.2);
        manager.Tick(217001, center, now);

        var target = manager.GetCombatTargets(217001).First();
        var result = manager.ApplyMonsterDamage(
            217001, target.CombatTargetId, SwarmArenaManager.MonsterMaxHealth);

        Assert.True(result.Applied);
        Assert.True(result.Killed);
        Assert.Equal(target.MonsterId, result.MonsterId);
        Assert.Equal(1, manager.GetSummary(217001).Kills);
        Assert.DoesNotContain(
            manager.GetCombatTargets(217001),
            candidate => candidate.CombatTargetId == target.CombatTargetId);
    }

    [Fact]
    public void Density_StaysWithinFirstStageCap()
    {
        DateTime now = StartUtc;
        var manager = CreateManager(() => now);
        Vector3f center = GroundCenter();

        for (double elapsed = 0.25d; elapsed <= 29d; elapsed += 0.25d)
        {
            now = StartUtc.AddSeconds(elapsed);
            manager.Tick(217001, center, now);
            int alive = manager.GetVisualStates(217001).Count(state => state.IsAlive);
            Assert.True(alive <= 20, $"1단계 밀도 상한 20을 초과했다: {alive}");
        }
    }

    [Fact]
    public void Timeout_EndsMatchAsSurvived()
    {
        DateTime now = StartUtc;
        var manager = CreateManager(() => now);

        now = StartUtc.AddSeconds(SwarmArenaManager.MatchDurationSeconds);
        var tick = manager.Tick(217001, GroundCenter(), now);

        Assert.True(tick.MatchEnded);
        Assert.True(tick.Survived);
        Assert.True(manager.TryGetEndState(217001, out bool survived));
        Assert.True(survived);
    }

    [Fact]
    public void Death_EndsMatchAsNotSurvived()
    {
        DateTime now = StartUtc;
        var manager = CreateManager(() => now);

        now = StartUtc.AddSeconds(10);
        manager.EndForDeath(217001, now);

        Assert.True(manager.TryGetEndState(217001, out bool survived));
        Assert.False(survived);
        var summary = manager.GetSummary(217001);
        Assert.False(summary.Survived);
        Assert.True(summary.SurvivalSeconds <= 10.01d);
    }

    private static SwarmArenaManager CreateManager(Func<DateTime> clock)
    {
        var manager = new SwarmArenaManager(clock);
        Cell center = GameMapData.GetAreaSpawnCell(MapId.School, AreaType.Ground);
        Assert.True(manager.InitializeMatching(217001, 1, AreaType.Ground, center, StartUtc));
        return manager;
    }

    private static Vector3f GroundCenter() =>
        MapCoordinateConverter.CellToWorld(
            MapId.School, GameMapData.GetAreaSpawnCell(MapId.School, AreaType.Ground));

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
