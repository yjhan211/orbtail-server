using game_server.matches;
using game_server.matches.monsters;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;

namespace demo_regression_tests;

public class MatchMonsterServiceTests
{
    private static readonly DateTime StartUtc =
        new(2026, 8, 6, 0, 0, 0, DateTimeKind.Utc);

    public MatchMonsterServiceTests()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();
    }

    [Fact]
    public void RegionSupply_HoldsZoneTargetWithTopUpsAndWipeRest()
    {
        // #229 4단계: 점유한 열린 구역마다 목표 수를 유지한다. 0초부터 1.5초마다 2마리씩
        // 보충하고, 목표에 닿으면 멈춘다. 전멸시키면 4초 휴지 뒤 보충이 재개된다.
        DateTime now = StartUtc.AddSeconds(0.25);
        var manager = CreateManager();
        var startRoom = MatchSpawnData.GetPhaseRoomCandidates()[0];
        Vector3f startCenter = AreaCenter(startRoom);

        // 첫 틱부터 보충이 돈다 — 시작 선물 15초 침묵(#226 E)은 퇴역했다.
        var firstTick = manager.Tick(Participants(startCenter, startRoom), true, now);
        // 초반(페이즈 0)은 작은 몹만 나온다 (#229): 시작 오브 하나로는 핵이 벽처럼 서서
        // 파밍이 막힌다. 웨이브 보충(30마리/12초)은 구역 목표에 잘리므로 첫 웨이브는 목표치 8.
        Assert.Equal(8, firstTick.SpawnedMonsters.Count);
        Assert.DoesNotContain(firstTick.SpawnedMonsters, monster => monster.Kind == MonsterKind.RunawayGoblin);
        Assert.All(firstTick.SpawnedMonsters, monster =>
        {
            // 공급 몹은 잠든 채 등장한다 — 개전은 근접·피격·접촉의 몫.
            Assert.Equal(0, monster.ChaseTargetPlayerId);
            // 페이즈 0 일반 HP — 상향분 원복 (2026-08-16 유저 결정: 잘 죽되 맞으면 치명적)
            Assert.Equal(16, monster.MaxHealthValue);
            Assert.Equal(1, monster.SummonStoneReward);
        });

        // 웨이브 간격(12초) 안에서는 조용하다 — 웨이브 사이가 곧 정리하는 창이다.
        now = StartUtc.AddSeconds(1.5);
        Assert.Empty(manager.Tick(Participants(startCenter, startRoom), true, now).SpawnedMonsters);

        // 목표 8을 유지한다 — 2초마다 부족분만큼 한 번에 붓고 쉰다.
        // 창은 60초다 (2026-08-16): 공급이 운동장 발원 침투로 바뀐 뒤로 "구역에 서 있는 수"는
        // 행군 시간만큼 뒤따라온다. 방을 통로로 쓰지 않게 되면서(도서관 관통 금지) 경로가
        // 통로를 도는 만큼 길어져 20초 창에는 절반만 도착했다 — 목표 유지 자체는 성립하므로
        // 도착까지 재는 창으로 넓힌다.
        for (double elapsed = 2d; elapsed <= 60d; elapsed += 0.25d)
        {
            now = StartUtc.AddSeconds(elapsed);
            manager.Tick(Participants(startCenter, startRoom), true, now);
        }

        int aliveInZone = manager.GetVisualStates()
            .Count(state => state.IsAlive && state.AreaType == startRoom);
        Assert.Equal(8, aliveInZone);

        // 전멸 → 2초 휴지 뒤 보충 재개 (2026-08-16: 웨이브 간격이 12초라 전멸 휴지는 짧게).
        foreach (var target in manager.GetCombatTargets().ToList())
            manager.ApplyMonsterDamage(target.CombatTargetId, attackerPlayerId: 1, damage: 999);
        Assert.DoesNotContain(manager.GetVisualStates(), state => state.IsAlive);

        now = StartUtc.AddSeconds(60.25);
        manager.Tick(Participants(startCenter, startRoom), true, now); // 휴지 시작
        now = StartUtc.AddSeconds(61.5d);
        Assert.Empty(manager.Tick(Participants(startCenter, startRoom), true, now).SpawnedMonsters);
        now = StartUtc.AddSeconds(63d);
        Assert.NotEmpty(manager.Tick(Participants(startCenter, startRoom), true, now).SpawnedMonsters);
    }

    [Fact]
    public void RegionSupply_ScalesHealthByPhase_AndCapsGlobalAlive()
    {
        // 곡선 (2026-08-16 유저 결정: 잘 죽되 맞으면 치명적): 최종 페이즈(4:10~)는
        // 일반 22 · 핵 120 · 접촉 40. 단단하게 만드는 방향은 되돌리고 위협은 접촉이 진다.
        // 전역 상한은 구역 목표(= 인당 목표 × 구역 인원)의 합이되 서버 천장 420을 넘지 않는다.
        DateTime now = StartUtc.AddSeconds(255);
        var manager = CreateManager(217002);

        var lateTick = manager.Tick(ManyParticipants(8, AreaCenter(Config.SWARM_MATCH_GROUND_AREA)), true, now);
        Assert.All(lateTick.SpawnedMonsters.Where(monster => monster.Kind == MonsterKind.Skeleton),
            normal => Assert.Equal(22, normal.MaxHealthValue));
        Assert.All(lateTick.SpawnedMonsters.Where(monster => monster.Kind == MonsterKind.RunawayGoblin),
            core => Assert.Equal(120, core.MaxHealthValue));

        // 10인이 서로 다른 구역에 흩어져도 전역 상한 48을 넘지 않는다.
        var rooms = MatchSpawnData.GetPhaseRoomCandidates().Take(5).ToList();
        for (double elapsed = 255.25d; elapsed <= 300d; elapsed += 0.25d)
        {
            now = StartUtc.AddSeconds(elapsed);
            var spread = rooms
                .SelectMany((room, roomIndex) => Enumerable.Range(0, 2).Select(seat =>
                    new PlayerPositionSnapshot(roomIndex * 2 + seat + 1, room, AreaCenter(room))))
                .ToList();
            manager.Tick(spread, true, now);
            int alive = manager.GetVisualStates().Count(state => state.IsAlive);
            // 점유 5구역 × 2명 × 인당 목표 28 = 280, 서버 천장 420 이하.
            Assert.True(alive <= 300, $"구역 목표 합 상한 300을 초과했다: {alive}");
        }
    }

    // #229 4단계-보정: 전역 상한은 하나뿐이라 아무도 없는 구역의 잔상이 살아 있는 전장의
    // 몫을 영구히 먹는다. 폐쇄 구역은 도달조차 못 하므로 순수 낭비다 — 폐쇄가 누적되면
    // 최악에는 전 구역 스폰이 0으로 굳었다. 걷어내는 규칙을 잠근다.
    [Fact]
    public void RegionSupply_ReclaimsStrandedMonstersAfterZoneIsVacated()
    {
        DateTime now = StartUtc;
        var manager = CreateManager(217004);
        var room = MatchSpawnData.GetPhaseRoomCandidates()[0];
        Vector3f roomCenter = AreaCenter(room);
        Vector3f elsewhere = AreaCenter(Config.SWARM_MATCH_GROUND_AREA);

        // 방을 채운다 — #272 School2: 운동장 발원 침투의 행군 거리가 길어져(외곽 시작방)
        // 도착까지 재는 창을 40초로 넓힌다 (RegionSupply_Holds의 60초 창과 같은 이유).
        for (double elapsed = 0.25d; elapsed <= 40d; elapsed += 0.25d)
        {
            now = StartUtc.AddSeconds(elapsed);
            manager.Tick(Participants(roomCenter, room), true, now);
        }

        int filled = manager.GetVisualStates()
            .Count(state => state.IsAlive && state.AreaType == room);
        Assert.True(filled > 0, "방이 채워지지 않았다");

        // 방을 비운다 — 유예(6초) 안에는 남아 있어야 한다. 나서자마자 뒤에서 사라지면 눈에 띈다.
        now = StartUtc.AddSeconds(42d);
        manager.Tick(Participants(elsewhere), true, now);
        now = StartUtc.AddSeconds(45d);
        manager.Tick(Participants(elsewhere), true, now);
        Assert.True(
            manager.GetVisualStates().Any(state => state.IsAlive && state.AreaType == room),
            "유예 안에 잔상이 사라졌다");

        // 유예가 지나면 걷힌다.
        for (double elapsed = 49d; elapsed <= 52d; elapsed += 0.25d)
        {
            now = StartUtc.AddSeconds(elapsed);
            manager.Tick(Participants(elsewhere), true, now);
        }

        Assert.DoesNotContain(
            manager.GetVisualStates(),
            state => state.IsAlive && state.AreaType == room);
    }

    [Fact]
    public void RegionSupply_StoneBudgetSurvivesZoneReentry()
    {
        // #229 4단계: 소환석 예산은 구역·페이즈 단위다. 봇처럼 구역을 들락날락해도
        // 예산이 리셋되면 안 된다 — 실측(매치 9687066)에서 한 구역이 페이즈 1 예산 11석 대신
        // 56석을 받았다. 보충 타이머는 버리되 예산 원장은 남긴다.
        DateTime now = StartUtc;
        var manager = CreateManager(217003);
        var room = MatchSpawnData.GetPhaseRoomCandidates()[0];
        Vector3f roomCenter = AreaCenter(room);
        Vector3f elsewhere = AreaCenter(Config.SWARM_MATCH_GROUND_AREA);

        int stones = 0;
        // 이 구역에 머물다 나갔다를 반복한다 — 페이즈 0(0:00~1:40) 안에서만 논다.
        for (double elapsed = 0.25d; elapsed <= 95d; elapsed += 0.25d)
        {
            now = StartUtc.AddSeconds(elapsed);
            bool inRoom = (int)(elapsed / 5d) % 2 == 0;
            var tick = manager.Tick(inRoom ? Participants(roomCenter, room) : Participants(elsewhere),
                true,
                now);
            stones += tick.SpawnedMonsters.Where(monster => monster.HomeArea == room)
                .Sum(monster => monster.SummonStoneReward);
        }

        // 페이즈 0 예산 90 + 핵 1기 3 = 93이 상한이다.
        Assert.True(stones <= 93, $"페이즈 0 구역 석 예산 93을 초과했다: {stones}");
        Assert.True(stones > 0, "예산이 아예 지급되지 않았다");
    }

    [Fact]
    public void ContactOnSupplyMonster_DealsDamageWithImmunityWindow()
    {
        // 공급 몹에 부딪히면 문다 — 접촉이 곧 개전이고, 무적창 리듬은 유지된다.
        // 운동장은 침투 발원지라 첫 틱에 앵커 자리에 바로 선다 (행군 없음).
        DateTime now = StartUtc.AddSeconds(0.25);
        var manager = CreateManager();
        Vector3f center = AreaCenter(Config.SWARM_MATCH_GROUND_AREA);
        var damageEvents = new List<MonsterContactDamage>();
        var firstTick = manager.Tick(Participants(center), true, now);
        Assert.NotEmpty(firstTick.SpawnedMonsters);
        damageEvents.AddRange(firstTick.PlayerDamage);

        var monster = manager.GetVisualStates().First(state => state.IsAlive);
        var onMonster = new Vector3f(monster.PositionX, monster.PositionY, 0f);

        for (double elapsed = 0.5d; elapsed <= 9d; elapsed += 0.25d)
        {
            now = StartUtc.AddSeconds(elapsed);
            damageEvents.AddRange(manager.Tick(Participants(onMonster), true, now).PlayerDamage);
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
        Assert.Equal(Monster.BaseContactRadius * 1.4f,
            Monster.GetContactRadius(MonsterKind.DartGoblin), 3);
        Assert.Equal(Monster.BaseContactRadius * 2.4f,
            Monster.GetContactRadius(MonsterKind.RunawayGoblin), 3);
        Assert.Equal(Monster.BaseContactRadius * 1.8f,
            Monster.GetContactRadius(MonsterKind.Bowler), 3);

        // 해골 반경은 몸통 반폭(0.31, 클라 실측)을 넘지 않는다.
        Assert.True(Monster.GetContactRadius(MonsterKind.Skeleton) <= 0.32f);
    }

    [Fact]
    public void PlayerAttacks_KillMonstersAndCountKills()
    {
        DateTime now = StartUtc;
        var manager = CreateManager();
        Vector3f center = AreaCenter(Config.SWARM_MATCH_GROUND_AREA);

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
        var result = manager.ApplyMonsterDamage(target.CombatTargetId, attackerPlayerId: 1, skeleton.MaxHealth);

        Assert.True(result.Applied);
        Assert.True(result.Killed);
        Assert.Equal(target.MonsterId, result.Monster!.MonsterId);
        Assert.DoesNotContain(
            manager.GetCombatTargets(),
            candidate => candidate.CombatTargetId == target.CombatTargetId);
    }

    [Fact]
    public void Monsters_OnlyChaseAndBiteSameAreaParticipants()
    {
        DateTime now = StartUtc.AddSeconds(0.25);
        var manager = CreateManager();
        // 다른 구역 참가자는 도서관1에 세운다. 몹은 자기장 경계 띠에서 태어나므로 위치는 실제 스폰 규칙을 따른다.
        Vector3f corridor = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, new Cell(126, 90));
        manager.Tick([new PlayerPositionSnapshot(1, Config.SWARM_MATCH_GROUND_AREA, AreaCenter(Config.SWARM_MATCH_GROUND_AREA))], true, now);

        var monster = manager.GetVisualStates().First(state => state.IsAlive);
        var onMonster = new Vector3f(monster.PositionX, monster.PositionY, 0f);

        var damageEvents = new List<MonsterContactDamage>();
        for (double elapsed = 0.5d; elapsed <= 9d; elapsed += 0.25d)
        {
            now = StartUtc.AddSeconds(elapsed);
            var participants = new[]
            {
                new PlayerPositionSnapshot(1, Config.SWARM_MATCH_GROUND_AREA, onMonster),
                new PlayerPositionSnapshot(2, AreaType.S2Library1, corridor)
            };
            damageEvents.AddRange(manager.Tick(participants, true, now).PlayerDamage);
        }

        // 참가자는 자기가 선 구역의 몹에게만 물린다. 2번이 선 도서관1에도 공급 몹이 서지만
        // 그 몹은 2번만, 운동장 몹은 1번만 문다 — 구역이 다르면 거리와 무관하게 물지 않는다.
        Assert.NotEmpty(damageEvents);
        Assert.Contains(damageEvents, damage => damage.TargetPlayerId == 1);
        Assert.All(damageEvents, damage => Assert.Equal(
            damage.TargetPlayerId == 1 ? Config.SWARM_MATCH_GROUND_AREA : AreaType.S2Library1,
            damage.Area));
    }

    private static IReadOnlyCollection<PlayerPositionSnapshot> Participants(
        Vector3f position,
        AreaType area = AreaType.None) =>
        [new PlayerPositionSnapshot(1, ResolveArea(area), position)];

    private static IReadOnlyCollection<PlayerPositionSnapshot> ManyParticipants(
        int count,
        Vector3f position,
        AreaType area = AreaType.None) =>
        Enumerable.Range(1, count)
            .Select(id => new PlayerPositionSnapshot(id, ResolveArea(area), position))
            .ToList();

    // 기본 구역 = 매치 맵 운동장 (기본 매개변수는 컴파일 상수만 허용 — None을 센티널로 쓴다).
    private static AreaType ResolveArea(AreaType area) =>
        area == AreaType.None ? Config.SWARM_MATCH_GROUND_AREA : area;

    [Fact]
    public void RegionSupply_MonsterPursuesOwnerAcrossDoor()
    {
        // 문 너머 추격 (2026-08-16 유저 결정, 2026-08-28 플레이 제보 "몹이 문 너머로 안 따라온다"):
        // 방에서 나를 담당하던(주인) 몹은 내가 복도로 나가면 문을 넘어 따라와야 한다.
        DateTime now = StartUtc.AddSeconds(0.25);
        var manager = CreateManager();
        var startRoom = MatchSpawnData.GetPhaseRoomCandidates()[0];
        Vector3f roomCenter = AreaCenter(startRoom);

        for (double elapsed = 0.25d; elapsed <= 40d; elapsed += 0.25d)
        {
            now = StartUtc.AddSeconds(elapsed);
            manager.Tick(Participants(roomCenter, startRoom), true, now);
        }

        var roomMonsterIds = manager.GetVisualStates()
            .Where(state => state.IsAlive && state.AreaType == startRoom)
            .Select(state => state.MonsterId)
            .ToHashSet();
        Assert.True(roomMonsterIds.Count > 0, "40초 안에 방에 몹이 도착해야 한다");

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
                    $"{state.MonsterId}@{state.AreaType}({state.PositionX:F1},{state.PositionY:F1}) chase={state.ChaseTargetPlayerId}")));
    }

    [Fact]
    public void Tick_PreservesPreGameSupplyBeforeGameplayStarts()
    {
        var preGame = CreateManager();
        var active = CreateManager(217005);

        // 시작 전에는 사람이 아직 없는 방에도 미리 공급하고, 시작 후에는 점유한 방만 공급한다.
        Assert.NotEmpty(preGame.Tick([], false, StartUtc.AddSeconds(0.25)).SpawnedMonsters);
        Assert.Empty(active.Tick([], true, StartUtc.AddSeconds(0.25)).SpawnedMonsters);
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

    /// <summary>매치 런타임 하나와 디렉터를 묶어 매치 잠금 안에서 부른다. 마지막 틱 시각을 피해 정산·표적 조회에 쓴다.</summary>
    private sealed class Arena
    {
        private readonly MatchMonsterService _director = new(new MonsterMovementService());
        private DateTime _lastNow = StartUtc;

        public Arena(long matchingId)
        {
            Runtime = TestGameSessionServices.CreateMatchRuntimeStore(NullLogger.Instance).GetOrCreate(matchingId);
            using (Runtime.Enter())
            {
                Assert.True(_director.Initialize(Runtime, StartUtc));
            }
        }

        public MatchRuntime Runtime { get; }

        public TickResult Tick(IReadOnlyCollection<PlayerPositionSnapshot> participants, bool isGameplayActive, DateTime nowUtc)
        {
            _lastNow = nowUtc;
            var known = Runtime.Monsters.Entities.Keys.ToHashSet();
            using (Runtime.Enter())
            {
                var contacts = _director.ProcessTick(Runtime, participants, isGameplayActive, nowUtc);
                var spawned = Runtime.Monsters.Entities.Values.Where(monster => !known.Contains(monster.MonsterId)).ToList();
                return new TickResult(contacts, spawned);
            }
        }

        public MonsterDamageResult ApplyMonsterDamage(long combatTargetId, long attackerPlayerId, int damage)
        {
            using (Runtime.Enter())
            {
                return _director.ApplyMonsterDamage(Runtime, combatTargetId, attackerPlayerId, damage, _lastNow);
            }
        }

        public IReadOnlyList<MonsterRuntimeInfo> GetVisualStates() => Runtime.Monsters.Entities.Values.Select(monster => monster.ToMonsterRuntimeInfo()).ToList();

        public IReadOnlyList<Monster> GetCombatTargets()
        {
            using (Runtime.Enter())
            {
                return _director.GetCombatTargets(Runtime, _lastNow);
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

                var inset = MatchMonsterService.InsetAnchorFromAreaEdge(authored, region.AreaType);
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
