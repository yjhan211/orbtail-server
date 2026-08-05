using game_server.services;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;

namespace demo_regression_tests;

public class SpotArenaManagerTests
{
    private static readonly DateTime StartUtc =
        new(2026, 8, 4, 0, 0, 0, DateTimeKind.Utc);

    public SpotArenaManagerTests()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();
    }

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
        Assert.True(manager.TryGetPlayerOrbItemId(216001, 2, out int wind));
        Assert.True(manager.TryGetPlayerOrbItemId(216001, 3, out int wave));
        Assert.True(manager.TryGetPlayerOrbItemId(216001, 4, out int recovery));
        Assert.Equal([107000010, 107000020, 107000030, 107000040], [sun, wind, wave, recovery]);

        int[] expectedAnchors = [3, 2, 8, 6];
        for (int index = 0; index < snapshot.Spots.Count; index++)
        {
            var spot = snapshot.Spots.Single(candidate => candidate.OwnerPlayerId == index + 1);
            Assert.Equal(AreaType.Corridor, spot.Area);
            Assert.Equal(SurvivorRoyaleSpawnData.GetCorridorAnchor(expectedAnchors[index]), spot.Cell);
            Cell spawnCell = SurvivorRoyaleSpawnData.GetCorridorSpawnCell(expectedAnchors[index]);
            Assert.Equal(AreaType.Corridor, GameMapData.GetCurrentArea(MapId.School, spawnCell));
            Assert.True(GameMapData.IsMoveablePosition(MapId.School, spawnCell));
            Assert.Contains(spawnCell, spot.Cell.GetAdjacentCells());
        }
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
    public void DestroyingSpot_RelinksRingAndDissolvesItsFronts()
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
            wave => Assert.Equal(4, wave.TargetOwnerPlayerId));

        var nextSpawn = manager.Tick(216001, [], now.AddSeconds(16));
        Assert.Contains(nextSpawn.Snapshot.Waves,
            wave => wave.OwnerPlayerId == 1 && wave.TargetOwnerPlayerId == 3);
        Assert.Contains(nextSpawn.Snapshot.Waves,
            wave => wave.OwnerPlayerId == 3 && wave.TargetOwnerPlayerId == 1);
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
        int[] anchorNumbers = [3, 2, 8, 6];

        for (int index = 0; index < anchorNumbers.Length; index++)
        {
            Cell fromCell = SurvivorRoyaleSpawnData.GetCorridorAnchor(anchorNumbers[index]);
            foreach (int offset in new[] { 1, anchorNumbers.Length - 1 })
            {
                Cell toCell = SurvivorRoyaleSpawnData.GetCorridorAnchor(
                    anchorNumbers[(index + offset) % anchorNumbers.Length]);

                var path = BotPathfinder.FindPath(
                    MapId.School,
                    AreaType.Corridor,
                    fromCell,
                    AreaType.Corridor,
                    toCell);

                Assert.NotNull(path);
                Assert.NotEmpty(path!);
                Assert.Equal(AreaType.Corridor, path![^1].Area);
                Assert.Equal(toCell, path[^1].Cell);
            }
        }
    }

    [Fact]
    public void Waves_SpawnInBatchesTowardBothNeighbors()
    {
        var manager = CreateManager();

        DateTime firstSpawnAt = StartUtc.AddSeconds(SpotArenaManager.WaveIntervalSeconds);
        var first = manager.Tick(216001, [], firstSpawnAt);

        Assert.Equal(4 * 2 * SpotArenaManager.WaveSize, first.SpawnedWaves.Count);
        Assert.All(first.SpawnedWaves,
            wave => Assert.Equal(SpotArenaManager.WaveMaxHealth, wave.CurrentHealth));

        long[][] expectedLanes = [[2, 4], [3, 1], [4, 2], [1, 3]];
        for (long ownerId = 1; ownerId <= 4; ownerId++)
        {
            var ownWaves = first.Snapshot.Waves
                .Where(wave => wave.OwnerPlayerId == ownerId)
                .ToList();
            Assert.Equal(2 * SpotArenaManager.WaveSize, ownWaves.Count);
            foreach (long lane in expectedLanes[ownerId - 1])
            {
                Assert.Equal(SpotArenaManager.WaveSize,
                    ownWaves.Count(wave => wave.TargetOwnerPlayerId == lane));
            }
        }

        var early = manager.Tick(
            216001,
            [],
            firstSpawnAt.AddSeconds(SpotArenaManager.WaveIntervalSeconds - 0.01d));
        Assert.Empty(early.SpawnedWaves);
    }

    [Fact]
    public void OpposingWaves_FormFrontsAndKeepSpotsUntouched()
    {
        var manager = CreateManager();
        var spotsByOwner = manager.GetSnapshot(216001).Spots.ToDictionary(spot => spot.OwnerPlayerId);

        DateTime now = StartUtc.AddSeconds(SpotArenaManager.WaveIntervalSeconds);
        manager.Tick(216001, [], now);
        int clashCount = 0;

        // 소모전으로 구멍이 나기 전인 첫 10초 동안은 어떤 웨이브도 전선을 지나
        // 상대 스팟에 도달할 수 없어야 한다.
        for (int tick = 0; tick < 40; tick++)
        {
            now = now.AddSeconds(0.25);
            var result = manager.Tick(216001, [], now);
            clashCount += result.WaveClashes.Count;
            foreach (var wave in result.Snapshot.Waves)
            {
                var targetSpot = spotsByOwner[wave.TargetOwnerPlayerId];
                Assert.False(
                    MathF.Abs(wave.Position.X - targetSpot.Position.X) < 0.01f &&
                    MathF.Abs(wave.Position.Y - targetSpot.Position.Y) < 0.01f,
                    "대칭 스폰에서는 웨이브가 전선을 뚫고 상대 스팟에 도달하면 안 된다.");
            }
        }

        Assert.True(clashCount > 0, "마주 보는 차선에서 교전이 발생해야 한다.");
        var snapshot = manager.GetSnapshot(216001);
        Assert.All(snapshot.Spots,
            spot => Assert.Equal(SpotArenaManager.SpotMaxHealth, spot.Health));
    }

    [Fact]
    public void RelatedWaves_ClashInsteadOfPassingThrough()
    {
        var manager = CreateManager();
        DateTime now = StartUtc.AddSeconds(SpotArenaManager.WaveIntervalSeconds);
        manager.Tick(216001, [], now);

        var clashEvents = new List<SpotArenaWaveClashEvent>();
        for (int tick = 0; tick < 240 && clashEvents.Count == 0; tick++)
        {
            now = now.AddSeconds(0.25);
            clashEvents.AddRange(manager.Tick(216001, [], now).WaveClashes);
        }

        Assert.NotEmpty(clashEvents);
        Assert.All(clashEvents, clash =>
        {
            Assert.NotEqual(clash.AttackerOwnerPlayerId, clash.TargetOwnerPlayerId);
            Assert.Equal(SpotArenaManager.WaveClashDamage, clash.Damage);
        });
    }

    [Fact]
    public void PlayerCanAttackOnlyWavesMarchingAtThem()
    {
        var manager = CreateManager();

        Assert.True(manager.CanPlayerAttackWave(
            216001, attackerPlayerId: 1, waveMonsterId: 0,
            waveOwnerPlayerId: 4, waveTargetOwnerPlayerId: 1));
        Assert.True(manager.CanPlayerAttackWave(
            216001, attackerPlayerId: 1, waveMonsterId: 0,
            waveOwnerPlayerId: 2, waveTargetOwnerPlayerId: 1));
        Assert.False(manager.CanPlayerAttackWave(
            216001, attackerPlayerId: 1, waveMonsterId: 0,
            waveOwnerPlayerId: 2, waveTargetOwnerPlayerId: 3));
        Assert.False(manager.CanPlayerAttackWave(
            216001, attackerPlayerId: 1, waveMonsterId: 0,
            waveOwnerPlayerId: 3, waveTargetOwnerPlayerId: 4));
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
