using game_server.services;
using network.common;
using network.common.data;
using network.common.data.models;

namespace demo_regression_tests;

public class SpotArenaManagerTests
{
    private static readonly DateTime StartUtc =
        new(2026, 8, 4, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Initialize_CreatesFourSpotRingWithThreeMinuteTimer()
    {
        var manager = CreateManager();
        DateTime now = StartUtc;

        var snapshot = manager.GetSnapshot(216001);

        Assert.Equal(4, snapshot.Spots.Count);
        Assert.Equal(SpotArenaManager.MatchDurationSeconds, snapshot.RemainingSeconds);
        Assert.Equal(2, snapshot.Spots.Single(spot => spot.OwnerPlayerId == 1).TargetPlayerId);
        Assert.Equal(1, snapshot.Spots.Single(spot => spot.OwnerPlayerId == 4).TargetPlayerId);
        Assert.All(snapshot.Spots, spot => Assert.Equal(SpotArenaManager.SpotMaxHealth, spot.Health));
        Assert.True(manager.TryGetPlayerOrbItemId(216001, 1, out int sun));
        Assert.True(manager.TryGetPlayerOrbItemId(216001, 2, out int wave));
        Assert.True(manager.TryGetPlayerOrbItemId(216001, 3, out int wind));
        Assert.True(manager.TryGetPlayerOrbItemId(216001, 4, out int recovery));
        Assert.Equal([107000010, 107000030, 107000020, 107000040], [sun, wave, wind, recovery]);
    }

    [Fact]
    public void Initialize_RejectsAnythingOtherThanFourPlayers()
    {
        var manager = new SpotArenaManager(() => StartUtc);

        bool initialized = manager.InitializeMatching(
            216005,
            CreateRegistrations().Take(3).ToArray(),
            StartUtc);

        Assert.False(initialized);
    }

    [Fact]
    public void DestroyingSpot_ReconnectsPredatorWithoutDestroyingItsIncomingWaves()
    {
        var manager = CreateManager();
        DateTime now = StartUtc;
        manager.Tick(216001, [], now.AddSeconds(8));

        var result = manager.ApplySpotDamage(
            216001,
            spotCombatTargetId: manager.GetSnapshot(216001).Spots.Single(spot => spot.OwnerPlayerId == 2).CombatTargetId,
            attackerPlayerId: 1,
            damage: SpotArenaManager.SpotMaxHealth);

        Assert.True(result.DestroyedOrKilled);
        var snapshot = manager.GetSnapshot(216001);
        Assert.False(snapshot.Spots.Single(spot => spot.OwnerPlayerId == 1).Destroyed);
        Assert.Equal(SpotArenaManager.SpotMaxHealth,
            snapshot.Spots.Single(spot => spot.OwnerPlayerId == 1).Health);
        Assert.True(snapshot.Spots.Single(spot => spot.OwnerPlayerId == 2).Destroyed);
        Assert.Equal(3, snapshot.Spots.Single(spot => spot.OwnerPlayerId == 1).TargetPlayerId);
        Assert.DoesNotContain(snapshot.Waves, wave => wave.OwnerPlayerId == 2);
        Assert.DoesNotContain(snapshot.Waves, wave => wave.TargetOwnerPlayerId == 2);
        Assert.All(snapshot.Waves.Where(wave => wave.OwnerPlayerId == 1),
            wave => Assert.Equal(3, wave.TargetOwnerPlayerId));
    }

    [Fact]
    public void Respawn_CompletesAfterFiveSecondsAndGrantsInvulnerability()
    {
        var manager = CreateManager();
        DateTime now = StartUtc;

        Assert.True(manager.BeginRespawn(216001, 1, now));
        Assert.True(manager.IsRespawning(216001, 1));

        var early = manager.Tick(216001, [], now.AddSeconds(4.9));
        Assert.Empty(early.RespawnedPlayers);

        var completed = manager.Tick(216001, [], now.AddSeconds(5));
        Assert.Single(completed.RespawnedPlayers);
        Assert.False(manager.IsRespawning(216001, 1));
        Assert.True(manager.IsInvulnerable(216001, 1, now.AddSeconds(6.9)));
        Assert.False(manager.IsInvulnerable(216001, 1, now.AddSeconds(7.1)));
    }

    [Fact]
    public void Timeout_SelectsHighestSpotHealthThenTargetDamage()
    {
        DateTime now = StartUtc;
        var manager = new SpotArenaManager(() => now);
        manager.InitializeMatching(216002, CreateRegistrations(), now);

        var snapshot = manager.GetSnapshot(216002);
        long player2Spot = snapshot.Spots.Single(spot => spot.OwnerPlayerId == 2).CombatTargetId;
        long player3Spot = snapshot.Spots.Single(spot => spot.OwnerPlayerId == 3).CombatTargetId;
        manager.ApplySpotDamage(216002, player2Spot, 1, 60);
        manager.ApplySpotDamage(216002, player3Spot, 2, 20);
        now = now.AddSeconds(SpotArenaManager.MatchDurationSeconds);

        var tick = manager.Tick(216002, [], now);

        Assert.True(tick.MatchEnded);
        Assert.Equal(1, tick.WinnerPlayerId);
    }

    [Fact]
    public void Timeout_WithExactTieEndsInDraw()
    {
        DateTime now = StartUtc;
        var manager = new SpotArenaManager(() => now);
        manager.InitializeMatching(216004, CreateRegistrations(), now);
        now = now.AddSeconds(SpotArenaManager.MatchDurationSeconds);

        var tick = manager.Tick(216004, [], now);

        Assert.True(tick.MatchEnded);
        Assert.Equal(0, tick.WinnerPlayerId);
    }

    [Fact]
    public void ConfiguredSpotArenaRing_HasTraversableWavePaths()
    {
        var areas = SurvivorRoyaleSpawnData.GetSpotArenaCandidates();

        for (int index = 0; index < areas.Count; index++)
        {
            AreaType fromArea = areas[index];
            AreaType toArea = areas[(index + 1) % areas.Count];
            Cell fromCell = GameMapData.GetAreaSpawnCell(MapId.School, fromArea);
            Cell toCell = GameMapData.GetAreaSpawnCell(MapId.School, toArea);

            var path = BotPathfinder.FindPath(
                MapId.School,
                fromArea,
                fromCell,
                toArea,
                toCell);

            Assert.NotNull(path);
            Assert.NotEmpty(path!);
            Assert.Equal(toArea, path![^1].Area);
        }
    }

    [Fact]
    public void SpawnedWaves_LeaveTheirRoomAndEnterTheirTargetsRoom()
    {
        var areas = SurvivorRoyaleSpawnData.GetSpotArenaCandidates();
        var registrations = areas
            .Select((area, index) => new SpotArenaPlayerRegistration(
                index + 1,
                (index + 1) % areas.Count + 1,
                area,
                GameMapData.GetAreaSpawnCell(MapId.School, area)))
            .ToArray();
        var manager = new SpotArenaManager(() => StartUtc);
        Assert.True(manager.InitializeMatching(216006, registrations, StartUtc));

        DateTime now = StartUtc.AddSeconds(SpotArenaManager.WaveIntervalSeconds);
        manager.Tick(216006, [], now);
        var reachedOwners = new HashSet<long>();

        for (int tick = 0; tick < 116; tick++)
        {
            now = now.AddSeconds(0.25);
            var result = manager.Tick(216006, [], now);
            foreach (var wave in result.Snapshot.Waves)
            {
                AreaType targetArea = registrations.Single(item => item.PlayerId == wave.TargetOwnerPlayerId).Area;
                if (wave.Area == targetArea)
                    reachedOwners.Add(wave.OwnerPlayerId);
            }
        }

        Assert.Equal(registrations.Select(item => item.PlayerId).OrderBy(id => id), reachedOwners.OrderBy(id => id));
    }

    [Fact]
    public void ExplicitTargetPriority_PrefersPlayerThenWaveThenSpot()
    {
        var resolver = new ProximityAutoCombatResolver();
        DateTime now = StartUtc;
        var attacker = Actor(1, 0, 8, 5, 0);
        var spot = Actor(-30, 2, 0, 0, 2);
        var wave = Actor(-20, 1, 0, 0, 1);
        var player = Actor(2, 3, 0, 0, 0);

        resolver.Resolve(216003, [attacker, spot, wave, player], now);
        var attacks = resolver.Resolve(
            216003,
            [attacker, spot, wave, player],
            now.Add(ProximityAutoCombatResolver.AimDuration));

        Assert.Single(attacks);
        Assert.Equal(2, attacks[0].TargetPlayerId);
    }

    private static SpotArenaManager CreateManager()
    {
        var manager = new SpotArenaManager(() => StartUtc);
        Assert.True(manager.InitializeMatching(216001, CreateRegistrations(), StartUtc));
        return manager;
    }

    private static SpotArenaPlayerRegistration[] CreateRegistrations() =>
    [
        new(1, 2, AreaType.Classroom3, new Cell(100, 100)),
        new(2, 3, AreaType.Classroom4, new Cell(110, 100)),
        new(3, 4, AreaType.BroadcastRoom, new Cell(120, 100)),
        new(4, 1, AreaType.Classroom2, new Cell(130, 100))
    ];

    private static ProximityCombatActor Actor(
        long id,
        float x,
        int damage,
        float range,
        int priority) =>
        new(
            id,
            AreaType.Classroom3,
            new Vector3f(x, 0, 0),
            107000010,
            range,
            damage,
            1f,
            MapId: MapId.School,
            Cell: new Cell((int)x, 0),
            TargetPriority: priority);
}
