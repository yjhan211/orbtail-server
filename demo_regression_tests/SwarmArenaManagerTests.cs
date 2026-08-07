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
        // 비주얼 확인용 임시 편성(고블린만)을 끄고 정규 편성을 검증한다.
        SwarmArenaManager.GoblinOnlySpawnForVisualCheck = false;
    }

    [Fact]
    public void FirstTick_SpawnsAreaCampsImmediately()
    {
        // #219 M1 캠프 모드: 구역 최초 진입 틱에 캠프(3개×3기 = 9기)가 즉시 선다.
        DateTime now = StartUtc.AddSeconds(0.25);
        var manager = CreateManager(() => now);
        Vector3f center = AreaCenter(AreaType.Ground);

        var tick = manager.Tick(217001, Participants(center), now);

        Assert.Equal(9, tick.SpawnedMonsters.Count);
        Assert.All(tick.SpawnedMonsters, monster =>
        {
            Assert.Equal(SwarmArenaManager.MonsterMaxHealth, monster.CurrentHealth);
            Assert.True(monster.IsAlive);
            Assert.Equal(AreaType.Ground, monster.AreaType);
        });

        // 캠프 몹은 예고 없이 즉시 전투 대상이다 — 잠들어 있을 뿐 실체다.
        now = now.AddSeconds(0.1);
        Assert.Equal(9, manager.GetCombatTargets(217001).Count);
    }

    [Fact]
    public void CorridorBand_SpawnsCampsInCloneMap()
    {
        // SB 클론 균질 밀도: 회랑 밴드(테라스=Corridor)에도 캠프가 선다.
        DateTime now = StartUtc.AddSeconds(0.25);
        var manager = CreateManager(() => now);
        Vector3f corridor = AreaCenter(AreaType.Corridor);

        var tick = manager.Tick(217001, Participants(corridor, AreaType.Corridor), now);

        Assert.Equal(9, tick.SpawnedMonsters.Count);
        Assert.All(tick.SpawnedMonsters, monster => Assert.Equal(AreaType.Corridor, monster.AreaType));
    }

    [Fact]
    public void PodArea_SpawnsSkeletonPackPlusDartAndBruiser()
    {
        // #219 SB 몬스터 4종: 포드 방 = 해골 무리(12×3) + 다트(16) + 탈주(60)|볼러(48).
        DateTime now = StartUtc.AddSeconds(0.25);
        var manager = CreateManager(() => now);
        Vector3f library = AreaCenter(AreaType.Library);

        var tick = manager.Tick(217001, Participants(library, AreaType.Library), now);

        Assert.Equal(5, tick.SpawnedMonsters.Count);
        Assert.Equal(3, tick.SpawnedMonsters.Count(monster => monster.MaxHealth == 12));
        Assert.Equal(1, tick.SpawnedMonsters.Count(monster => monster.MaxHealth == 16));
        Assert.Equal(1, tick.SpawnedMonsters.Count(monster => monster.MaxHealth is 60 or 48));
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
    public void ContactOnSleepingCampMonster_DealsDamageWithImmunityWindow()
    {
        // 잠든 캠프 몹도 부딪히면 문다 — 접촉이 곧 개전이고, 무적창 리듬은 유지된다.
        DateTime now = StartUtc.AddSeconds(0.25);
        var manager = CreateManager(() => now);
        Vector3f center = AreaCenter(AreaType.Ground);
        manager.Tick(217001, Participants(center), now);

        var monster = manager.GetVisualStates(217001).First(state => state.IsAlive);
        var onMonster = new Vector3f(monster.PositionX, monster.PositionY, 0f);

        var damageEvents = new List<SpotArenaPlayerDamage>();
        for (double elapsed = 0.5d; elapsed <= 9d; elapsed += 0.25d)
        {
            now = StartUtc.AddSeconds(elapsed);
            damageEvents.AddRange(manager.Tick(217001, Participants(onMonster), now).PlayerDamage);
        }

        Assert.NotEmpty(damageEvents);
        Assert.All(damageEvents, damage =>
        {
            // 운동장 캠프는 전원 해골 — 접촉 피해는 오브 HP 1이다 (SB: 잡몹은 거의 무해).
            Assert.Equal(1, damage.Damage);
            Assert.Equal(1, damage.TargetPlayerId);
        });
        Assert.Equal(damageEvents.Count, manager.GetSummary(217001).HitsTaken);
        // 무적창(0.8초)보다 촘촘히 맞을 수 없다.
        Assert.InRange(damageEvents.Count, 1, 12);
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
        DateTime now = StartUtc.AddSeconds(0.25);
        var manager = CreateManager(() => now);
        // 복도 밴드 서쪽 끝 — 중앙 캠프(반경 4 + 산포)에서 충분히 떨어진 지점.
        Vector3f corridor = MapCoordinateConverter.CellToWorld(MapId.School, new Cell(116, 79));
        manager.Tick(217001, [new SpotArenaPlayerSpatial(1, AreaType.Ground, AreaCenter(AreaType.Ground))], now);

        var monster = manager.GetVisualStates(217001).First(state => state.IsAlive);
        var onMonster = new Vector3f(monster.PositionX, monster.PositionY, 0f);

        var damageEvents = new List<SpotArenaPlayerDamage>();
        for (double elapsed = 0.5d; elapsed <= 9d; elapsed += 0.25d)
        {
            now = StartUtc.AddSeconds(elapsed);
            var participants = new[]
            {
                new SpotArenaPlayerSpatial(1, AreaType.Ground, onMonster),
                new SpotArenaPlayerSpatial(2, AreaType.Corridor, corridor)
            };
            damageEvents.AddRange(manager.Tick(217001, participants, now).PlayerDamage);
        }

        // 운동장 캠프는 운동장의 1번만 문다. 복도의 2번은 무관하다.
        Assert.NotEmpty(damageEvents);
        Assert.All(damageEvents, damage => Assert.Equal(1, damage.TargetPlayerId));
    }

    [Fact]
    public void CampMonsters_SleepUntilProvoked_ChaseOnHit_AndLeashHome()
    {
        // 캠프 3원칙: 멀리서 보면 잠들어 있고, 때리면 캠프째 깨어나 쫓아오고,
        // 리쉬 밖으로 도망치면 앵커로 돌아가 다시 잠든다.
        DateTime now = StartUtc.AddSeconds(0.25);
        var manager = CreateManager(() => now);
        Vector3f center = AreaCenter(AreaType.Ground);
        manager.Tick(217001, Participants(center), now);

        var sleeping = manager.GetVisualStates(217001).First(state => state.IsAlive);
        var anchor = new Vector3f(sleeping.PositionX, sleeping.PositionY, 0f);
        // 어그로 반경(2.5) 밖 관찰 지점 — 잠든 몹은 움직이지도 물지도 않는다.
        var watchPoint = new Vector3f(anchor.X + 4f, anchor.Y, 0f);
        for (double elapsed = 0.5d; elapsed <= 3d; elapsed += 0.25d)
        {
            now = StartUtc.AddSeconds(elapsed);
            var tick = manager.Tick(217001, Participants(watchPoint), now);
            Assert.Empty(tick.PlayerDamage);
        }

        var stillSleeping = manager.GetVisualStates(217001)
            .First(state => state.MonsterId == sleeping.MonsterId);
        Assert.Equal(anchor.X, stillSleeping.PositionX, 1);
        Assert.Equal(anchor.Y, stillSleeping.PositionY, 1);

        // 때리면 깨어나 쫓아온다.
        var sleepingTarget = manager.GetCombatTargets(217001)
            .First(target => target.MonsterId == sleeping.MonsterId);
        manager.ApplyMonsterDamage(217001, sleepingTarget.CombatTargetId, attackerPlayerId: 1, damage: 1);
        for (double elapsed = 3.25d; elapsed <= 4.5d; elapsed += 0.25d)
        {
            now = StartUtc.AddSeconds(elapsed);
            manager.Tick(217001, Participants(watchPoint), now);
        }

        var chasing = manager.GetVisualStates(217001)
            .First(state => state.MonsterId == sleeping.MonsterId);
        float chaseDx = chasing.PositionX - watchPoint.X;
        float chaseDy = chasing.PositionY - watchPoint.Y;
        float sleepDx = anchor.X - watchPoint.X;
        Assert.True(chaseDx * chaseDx + chaseDy * chaseDy < sleepDx * sleepDx,
            "어그로 후에는 관찰 지점 쪽으로 접근해야 한다");

        // 리쉬(7) 밖으로 도망치면 몹은 앵커로 귀환한다.
        var farAway = new Vector3f(anchor.X + 20f, anchor.Y, 0f);
        for (double elapsed = 4.75d; elapsed <= 9d; elapsed += 0.25d)
        {
            now = StartUtc.AddSeconds(elapsed);
            manager.Tick(217001, Participants(farAway), now);
        }

        var returned = manager.GetVisualStates(217001)
            .First(state => state.MonsterId == sleeping.MonsterId);
        float homeDx = returned.PositionX - anchor.X;
        float homeDy = returned.PositionY - anchor.Y;
        Assert.True(homeDx * homeDx + homeDy * homeDy < 2.5f * 2.5f,
            "리쉬 이탈 후에는 앵커 근처로 귀환해야 한다");
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
