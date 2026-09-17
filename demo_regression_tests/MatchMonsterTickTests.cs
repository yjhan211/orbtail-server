using game_server.matches;
using game_server.matches.monsters;
using game_server.players;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;

namespace demo_regression_tests;

public class MatchMonsterTickTests
{
    private static readonly DateTime StartUtc =
        new(2026, 8, 6, 0, 0, 0, DateTimeKind.Utc);

    public MatchMonsterTickTests()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();
    }

    [Theory]
    [InlineData(0.049, true)]
    [InlineData(0.05, false)]
    public void CoreSpawnRollUsesFivePercentPerMonsterWithoutAliveCoreLimit(double roll, bool expectCore)
    {
        var runtime = CreateManager().Runtime;
        using var scope = runtime.Enter();
        runtime.Monsters.Rng = new FixedRollRandom(roll);
        var supply = new MatchMonsterSpawnService();
        Assert.Equal(0.05d, Config.SWARM_MONSTER_SUPPLY_CORE_SPAWN_CHANCE);

        supply.ProcessTick(runtime, StartUtc);
        Assert.NotEmpty(runtime.Monsters.Entities);
        Assert.All(runtime.Monsters.Entities.Values, monster => Assert.Equal(MonsterKind.Skeleton, monster.Kind));
        foreach (var monster in runtime.Monsters.Entities.Values.ToArray()) runtime.RemoveMonster(monster);

        int firstPhase = Config.SWARM_MONSTER_SUPPLY_CORE_FIRST_PHASE_INDEX;
        var now = StartUtc.AddSeconds(SwarmSupplyPhaseData.GetAll()[firstPhase - 1].UntilSeconds);
        supply.ProcessTick(runtime, now);
        int firstWaveCount = runtime.Monsters.Entities.Count;
        Assert.True(firstWaveCount > 1);
        supply.ProcessTick(runtime, now.AddSeconds(Config.SWARM_MONSTER_SUPPLY_TOP_UP_INTERVAL_SECONDS));
        Assert.Equal(firstWaveCount * 2, runtime.Monsters.Entities.Count);
        var expectedKind = expectCore ? MonsterKind.RunawayGoblin : MonsterKind.Skeleton;
        Assert.All(runtime.Monsters.Entities.Values, monster => Assert.Equal(expectedKind, monster.Kind));
    }

    private sealed class FixedRollRandom(double roll) : Random(42)
    {
        public override double NextDouble() => roll;
    }

    [Fact]
    public void BoundarySupplyRepeatsWithoutParticipantsOrWipeRest()
    {
        var runtime = CreateManager().Runtime;
        using var scope = runtime.Enter();
        var supply = new MatchMonsterSpawnService();
        double interval = Config.SWARM_MONSTER_SUPPLY_TOP_UP_INTERVAL_SECONDS;
        supply.ProcessTick(runtime, StartUtc);
        int waveCount = runtime.Monsters.Entities.Count;
        Assert.Equal(Config.SWARM_MONSTER_SUPPLY_TOP_UP_COUNT, waveCount);
        Assert.All(runtime.Monsters.Entities.Values, monster =>
        {
            int distance = SwarmPressureField.GetDistance(monster.Info.ObjectInfo.Cell);
            Assert.InRange(distance, SwarmPressureField.MaxDistance - Config.SWARM_MONSTER_FIELD_SPAWN_BAND_CELLS, SwarmPressureField.MaxDistance);
            Assert.True(GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, monster.Info.ObjectInfo.Cell));
            Assert.Empty(monster.Movement.Waypoints);
        });
        supply.ProcessTick(runtime, StartUtc.AddSeconds(interval).AddTicks(-1));
        Assert.Equal(waveCount, runtime.Monsters.Entities.Count);
        supply.ProcessTick(runtime, StartUtc.AddSeconds(interval));
        Assert.Equal(waveCount * 2, runtime.Monsters.Entities.Count);
        foreach (var monster in runtime.Monsters.Entities.Values.ToArray()) runtime.RemoveMonster(monster);
        supply.ProcessTick(runtime, StartUtc.AddSeconds(interval * 2));
        Assert.Equal(waveCount, runtime.Monsters.Entities.Count);
    }

    [Fact]
    public void BoundarySupplyUsesCurrentFieldAndPhaseAndRespectsGlobalCap()
    {
        var runtime = CreateManager(217002).Runtime;
        using var scope = runtime.Enter();
        runtime.Closures.GameStartTime = StartUtc;
        var supply = new MatchMonsterSpawnService();
        var now = StartUtc.AddSeconds(255);
        supply.ProcessTick(runtime, now);
        Assert.NotEmpty(runtime.Monsters.Entities);
        double radius = runtime.Closures.GetSafeDistance(now);
        Assert.All(runtime.Monsters.Entities.Values, monster =>
        {
            Assert.InRange((double)SwarmPressureField.GetDistance(monster.Info.ObjectInfo.Cell),
                Math.Max(0d, radius - Config.SWARM_MONSTER_FIELD_SPAWN_BAND_CELLS), radius);
            Assert.Equal(monster.Kind == MonsterKind.RunawayGoblin ? 120 : 22, monster.MaxHealthValue);
        });
        int cap = Config.SWARM_MONSTER_SUPPLY_GLOBAL_ALIVE_HARD_CAP;
        for (int id = 1; runtime.Monsters.Entities.Count < cap - 1; id++)
            runtime.Monsters.Entities[id] = new Monster { MonsterId = id, Alive = true };
        supply.ProcessTick(runtime, runtime.Monsters.NextSpawnAtUtc);
        Assert.Equal(cap, runtime.Monsters.Entities.Count);
        supply.ProcessTick(runtime, runtime.Monsters.NextSpawnAtUtc);
        Assert.Equal(cap, runtime.Monsters.Entities.Count);
    }

    [Fact]
    public void BoundarySupplyDoesNotReclaimMonstersWhenPlayersLeave()
    {
        var runtime = CreateManager(217004).Runtime;
        using var scope = runtime.Enter();
        var supply = new MatchMonsterSpawnService();
        supply.ProcessTick(runtime, StartUtc);
        var initial = runtime.Monsters.Entities.Keys.ToArray();
        supply.ProcessTick(runtime, StartUtc.AddSeconds(60));
        Assert.All(initial, id => Assert.True(runtime.Monsters.Entities.ContainsKey(id)));
        Assert.True(runtime.Monsters.Entities.Count > initial.Length);
    }

    [Theory]
    [InlineData(MonsterKind.Skeleton, 1)]
    [InlineData(MonsterKind.RunawayGoblin, 3)]
    public void EveryMonsterKillGrantsConfiguredReward(MonsterKind kind, int reward)
    {
        var runtime = CreateManager(217003).Runtime;
        using var scope = runtime.Enter();
        var combat = new MonsterCombatService();
        for (int index = 0; index < 20; index++)
        {
            var monster = new Monster
            {
                MonsterId = 7000000 + index,
                Position = network.common.data.MapCoordinateConverter.CellToWorld(network.common.Config.SWARM_MATCH_MAP, network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Corridor9))), Kind = kind, Health = 10, Alive = true,
                SummonStoneReward = reward
            };
            runtime.Monsters.Entities.Add(monster.MonsterId, monster);
            var hit = combat.ApplyMonsterDamage(runtime, monster.MonsterId, 1, 1, StartUtc);
            Assert.False(hit.Killed);
            Assert.Equal(0, hit.SummonStoneReward);
            var kill = combat.ApplyMonsterDamage(runtime, monster.MonsterId, 1, 9, StartUtc);
            Assert.True(kill.Killed);
            Assert.Equal(reward, kill.SummonStoneReward);
            var duplicate = combat.ApplyMonsterDamage(runtime, monster.MonsterId, 1, 10, StartUtc);
            Assert.False(duplicate.Killed);
            Assert.Equal(0, duplicate.SummonStoneReward);
        }
    }

    [Fact]
    public void ContactOnSupplyMonster_DealsDamageWithImmunityWindow()
    {
        // 공급 몹에 부딪히면 문다 — 접촉이 곧 개전이고, 무적창 리듬은 유지된다.
        // 운동장은 침투 발원지라 첫 틱에 앵커 자리에 바로 선다 (행군 없음).
        DateTime now = StartUtc.AddSeconds(0.25);
        var manager = CreateManager();
        Vector3f center = AreaCenter(AreaType.S2Corridor9);
        var damageEvents = new List<MonsterContactDamage>();
        var firstTick = manager.Tick(Participants(center), true, now);
        Assert.NotEmpty(firstTick.SpawnedMonsters);
        damageEvents.AddRange(firstTick.PlayerDamage);

        var monster = manager.GetVisualStates().First(state => state.IsAlive);
        var onMonster = new Vector3f(monster.ObjectInfo.Position.X, monster.ObjectInfo.Position.Y, 0f);

        for (double elapsed = 0.5d; elapsed <= 9d; elapsed += 0.25d)
        {
            now = StartUtc.AddSeconds(elapsed);
            damageEvents.AddRange(manager.Tick(Participants(onMonster, monster.AreaType), true, now).PlayerDamage);
        }

        Assert.NotEmpty(damageEvents);
        Assert.All(damageEvents, damage =>
        {
            // 페이즈 0 일반 몹 접촉 피해 10 (SupplyPhases 곡선 — 잘 죽되 맞으면 치명적).
            Assert.Equal(10, damage.Damage);
            Assert.Equal(1, damage.TargetPlayerId);
        });
        // 무적창(0.6초)보다 촘촘히 맞을 수 없다 — 8.75초 관찰이면 상한 15대다 (#229).
        Assert.InRange(damageEvents.Count, 1, 15);
    }

    [Fact]
    public void ContactRadius_FollowsClientKindScaleLadder()
    {
        // 판정 = 보이는 몸통 (#229). 클라 ResolveKindScale과 같은 사다리라
        // 한쪽만 바뀌면 스프라이트와 판정이 어긋난다 — 여기서 잠근다.
        Assert.Equal(Monster.BaseContactRadius,
            Monster.GetContactRadius(MonsterKind.Skeleton), 3);
        Assert.Equal(Monster.BaseContactRadius * 2.4f,
            Monster.GetContactRadius(MonsterKind.RunawayGoblin), 3);

        // 셀 중심에 도착하면 같은 셀 내부의 플레이어까지 접촉 반경이 닿는다.
        Assert.Equal(0.5f, Monster.GetContactRadius(MonsterKind.Skeleton));
    }

    [Fact]
    public void PlayerAttacks_KillMonstersAndCountKills()
    {
        DateTime now = StartUtc;
        var manager = CreateManager();
        Vector3f center = AreaCenter(AreaType.S2Corridor9);

        manager.Tick(Participants(center), true, StartUtc.AddSeconds(0.25));
        now = StartUtc.AddSeconds(3.5);
        manager.Tick(Participants(center), true, now);
        now = StartUtc.AddSeconds(4.7);
        manager.Tick(Participants(center), true, now);

        // 일반 몹(해골, Kind 0)을 골라 원킬을 검증한다 — 피통은 페이즈 곡선(0페이즈 16)이 정한다.
        var skeleton = manager.GetVisualStates()
            .First(state => state.IsAlive && state.Kind == 0);
        var target = manager.GetCombatTargets().First(candidate =>
            candidate.MonsterId == skeleton.MonsterId);
        var result = manager.ApplyMonsterDamage(target.MonsterId, attackerPlayerId: 1, skeleton.MaxHealth);

        Assert.True(result.Applied);
        Assert.True(result.Killed);
        Assert.Equal(target.MonsterId, result.Monster!.MonsterId);
        Assert.DoesNotContain(
            manager.GetCombatTargets(),
            candidate => candidate.MonsterId == target.MonsterId);
    }

    [Fact]
    public void MonstersOnlyBiteParticipantsInTheirCurrentArea()
    {
        DateTime now = StartUtc.AddSeconds(0.25);
        var manager = CreateManager();
        // 다른 구역 참가자는 도서관1에 세운다. 몹은 자기장 경계 띠에서 태어나므로 위치는 실제 스폰 규칙을 따른다.
        Vector3f corridor = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, new Cell(126, 90));
        manager.Tick([new ParticipantInput(1, AreaType.S2Corridor9, AreaCenter(AreaType.S2Corridor9))], true, now);

        var monster = manager.GetVisualStates().First(state => state.IsAlive);
        var onMonster = new Vector3f(monster.ObjectInfo.Position.X, monster.ObjectInfo.Position.Y, 0f);

        var damageEvents = new List<MonsterContactDamage>();
        for (double elapsed = 0.5d; elapsed <= 9d; elapsed += 0.25d)
        {
            now = StartUtc.AddSeconds(elapsed);
            var participants = new[]
            {
                new ParticipantInput(1, monster.AreaType, onMonster),
                new ParticipantInput(2, AreaType.S2Library1, corridor)
            };
            damageEvents.AddRange(manager.Tick(participants, true, now).PlayerDamage);
        }

        // 참가자는 자기가 선 구역의 몹에게만 물린다. 2번이 선 도서관1에도 공급 몹이 서지만
        // 그 몹은 2번만, 운동장 몹은 1번만 문다 — 구역이 다르면 거리와 무관하게 물지 않는다.
        Assert.NotEmpty(damageEvents);
        Assert.Contains(damageEvents, damage => damage.TargetPlayerId == 1);
        Assert.All(damageEvents, damage => Assert.Equal(
            damage.TargetPlayerId == 1 ? monster.AreaType : AreaType.S2Library1,
            damage.Area));
    }

    private static IReadOnlyCollection<ParticipantInput> Participants(
        Vector3f position,
        AreaType area = AreaType.None) =>
        [new ParticipantInput(1, ResolveArea(area), position)];

    // 기본 구역 = 매치 맵 운동장 (기본 매개변수는 컴파일 상수만 허용 — None을 센티널로 쓴다).
    private static AreaType ResolveArea(AreaType area) =>
        area == AreaType.None ? AreaType.S2Corridor9 : area;

    [Fact]
    public void MonsterPursuesPlayerAcrossDoor()
    {
        DateTime now = StartUtc;
        var manager = CreateManager();
        var startRoom = MatchSpawnData.GetPhaseRoomCandidates()[0];
        Vector3f roomCenter = AreaCenter(startRoom);
        using (manager.Runtime.Enter())
        {
            // 공급 위치와 무관하게 기존 몬스터의 문 통과만 검증한다.
            manager.Runtime.Monsters.NextSpawnAtUtc = DateTime.MaxValue;
            manager.Runtime.Monsters.Entities[1] = new Monster
            {
                MonsterId = 1, Alive = true, Health = 100, Position = roomCenter,
                ChaseTargetPlayerId = 1
            };
        }
        var roomMonsterIds = new HashSet<int> { 1 };
        // 방을 나가 복도로 — 새 공급분과 섞이지 않게 "방에 있던 몹"의 ID로만 판정한다.
        var corridor = AreaType.S2Corridor1;
        Vector3f corridorCenter = AreaCenter(corridor);
        for (double elapsed = 40.25d; elapsed <= 42d; elapsed += 0.25d)
        {
            now = StartUtc.AddSeconds(elapsed);
            manager.Tick(Participants(corridorCenter, corridor), true, now);
        }

        for (double elapsed = 42.25d; elapsed <= 70d; elapsed += 0.25d)
        {
            now = StartUtc.AddSeconds(elapsed);
            manager.Tick(Participants(corridorCenter, corridor), true, now);
        }

        var pursuerCount = manager.GetVisualStates()
            .Count(state => state.IsAlive && roomMonsterIds.Contains(state.MonsterId) &&
                            state.AreaType == corridor);
        Assert.True(pursuerCount > 0,
            "방에서 나를 담당하던 몹이 문 너머 복도로 따라와야 한다 — " +
            string.Join(", ", manager.GetVisualStates()
                .Where(state => state.IsAlive && roomMonsterIds.Contains(state.MonsterId))
                .Take(4)
                .Select(state =>
                    $"{state.MonsterId}@{state.AreaType}({state.ObjectInfo.Position.X:F1},{state.ObjectInfo.Position.Y:F1}) chase={state.ChaseTargetPlayerId}")));
    }

    [Fact]
    public void Tick_PreservesPreGameSupplyBeforeGameplayStarts()
    {
        var preGame = CreateManager();
        var active = CreateManager(217005);

        // 시작 전후 모두 참가자 유무와 무관하게 경계에서 공급한다.
        Assert.NotEmpty(preGame.Tick([], false, StartUtc.AddSeconds(0.25)).SpawnedMonsters);
        Assert.NotEmpty(active.Tick([], true, StartUtc.AddSeconds(0.25)).SpawnedMonsters);
    }

    [Fact]
    public void Tick_UsesClosureStateInitializedAfterDirectorConstruction()
    {
        var manager = CreateManager(217006);
        var closures = manager.Runtime.Closures;
        var room = MatchSpawnData.GetPhaseRoomCandidates()[0];
        Assert.False(closures.IsAreaClosed(room));

        AreaType[] allAreas = GameMapData.GetAreas(Config.SWARM_MATCH_MAP)
            .Select(region => region.AreaType)
            .Where(area => area != AreaType.None)
            .Distinct()
            .ToArray();
        closures.InitializeMatching(allAreas.Select(area => (area, 0)).ToArray());
        closures.CloseDueAreas();
        Assert.True(closures.IsAreaClosed(room));
        var result = manager.Tick(Participants(AreaCenter(room), room), false, StartUtc.AddSeconds(0.25));
        Assert.Empty(result.SpawnedMonsters);
    }

    private static Arena CreateManager(long matchingId = 217001) => new(matchingId);

    /// <summary>틱이 만든 접촉 피해와 그 틱에 새로 태어난 개체.</summary>
    private sealed record TickResult(IReadOnlyList<MonsterContactDamage> PlayerDamage, IReadOnlyList<Monster> SpawnedMonsters);

    // 운영 계약이 아니라 테스트에서 참가자의 위치를 지정하기 위한 입력 자료다.
    private readonly record struct ParticipantInput(long PlayerId, AreaType Area, Vector3f Position);

    /// <summary>운영 전투 조율자의 몬스터 단계를 매치 잠금 안에서 실행한다. 마지막 틱 시각을 피해 정산·표적 조회에 쓴다.</summary>
    private sealed class Arena
    {
        private readonly MatchMoveService _movement = new(null!, new MonsterBehaviorService());
        private readonly MatchCombatService _combat = TestGameSessionServices.CreateMonsterTickService();
        private readonly MonsterCombatService _monsterCombat = new();
        private DateTime _lastNow = StartUtc;

        public Arena(long matchingId)
        {
            Runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(matchingId);
            using (Runtime.Enter())
            {
                Assert.True(Runtime.Monsters.Initialize(StartUtc));
            }
        }

        public MatchRuntime Runtime { get; }

        public TickResult Tick(IReadOnlyCollection<ParticipantInput> participants, bool isGameplayActive, DateTime nowUtc)
        {
            _lastNow = nowUtc;
            var known = Runtime.Monsters.Entities.Keys.ToHashSet();
            using (Runtime.Enter())
            {
                // 테스트 입력을 매치의 실제 Player에 반영한다.
                foreach (var participant in participants)
                {
                    if (Runtime.GetPlayer(participant.PlayerId) == null)
                    {
                        Runtime.RegisterPlayer(new Player(new PlayerInfo { PlayerId = participant.PlayerId }));
                    }
                }
                foreach (var player in Runtime.GetAlivePlayers())
                    player.Position = null;
                foreach (var participant in participants)
                {
                    var player = Runtime.GetPlayer(participant.PlayerId)!;
                    player.Position = participant.Position;
                    if (player.GameInfo.ObjectInfo.Area != participant.Area)
                        player.Position = TestMapPosition.In(participant.Area);
                }
                if (isGameplayActive) Runtime.StartGameplay(StartUtc);
                // 운영 틱과 같은 공급 → 이동 → 접촉 순서. 빈 참가자 공급 정책도 직접 검증한다.
                new MatchMonsterSpawnService().ProcessTick(Runtime, nowUtc);
                if (participants.Count > 0)
                {
                    _movement.ProcessTick(Runtime, nowUtc);
                }
                var contactPlayers = participants.Select(participant => Runtime.GetPlayer(participant.PlayerId)!).ToList();
                var monsterContactDamages = _combat.CollectMonsterContactDamages(Runtime, contactPlayers, nowUtc);
                var spawned = Runtime.Monsters.Entities.Values.Where(monster => !known.Contains(monster.MonsterId)).ToList();
                return new TickResult(monsterContactDamages, spawned);
            }
        }

        public MonsterDamageResult ApplyMonsterDamage(int monsterId, long attackerPlayerId, int damage)
        {
            using (Runtime.Enter())
            {
                return _monsterCombat.ApplyMonsterDamage(Runtime, monsterId, attackerPlayerId, damage, _lastNow);
            }
        }

        public IReadOnlyList<MonsterInfo> GetVisualStates() => Runtime.Monsters.Entities.Values.Select(monster => monster.ToMonsterInfo()).ToList();

        public IReadOnlyList<Monster> GetCombatTargets()
        {
            using (Runtime.Enter())
            {
                return Runtime.Monsters.GetCombatTargets();
            }
        }
    }

    private static Vector3f AreaCenter(AreaType area) =>
        MapCoordinateConverter.CellToWorld(
            Config.SWARM_MATCH_MAP, GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, area));

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

    [Fact]
    public void SupplyAnchors_StayClearOfAreaWalls()
    {
        // #229: 구역 박스의 테두리가 곧 벽선인데 map_region.csv에는 방 둘레가 obstacle로
        // 적혀 있지 않다. 그래서 테두리 앵커가 "통행 가능"으로 통과하고 잔상이 벽에 낀 채로 선다
        // (행정실: 앵커 3개가 전부 경계 1칸 이내, 그중 둘은 경계선 위).
        // monster_camp_anchor.csv의 앵커는 공급 무리의 행군 도착지로 쓰인다 (#325 캠프 모드 삭제 후).
        const int margin = 3;
        foreach (var region in GameMapData.GetAreas(Config.SWARM_MATCH_MAP))
        {
            if (region.End.X - region.Start.X < margin * 2 ||
                region.End.Y - region.Start.Y < margin * 2)
                continue;

            for (int campIndex = 0; campIndex < 3; campIndex++)
            {
                var authored = GameMonsterCampData.GetAnchor(region.AreaType, campIndex);
                if (authored == null) continue;

                var inset = MapPathfinder.InsetCellFromAreaEdge(Config.SWARM_MATCH_MAP, authored, region.AreaType, margin);
                int clearance = Math.Min(
                    Math.Min(inset.X - region.Start.X, region.End.X - inset.X),
                    Math.Min(inset.Y - region.Start.Y, region.End.Y - inset.Y));
                Assert.True(clearance >= margin,
                    $"{region.AreaType} 앵커 {campIndex}이(가) 벽에 붙었다: " +
                    $"({inset.X},{inset.Y}) 여유 {clearance}");
            }
        }
    }
}
