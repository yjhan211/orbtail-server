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
        // 웨이브 실험(#226)을 끄고 캠프 정규 동작을 검증한다. 웨이브 테스트는 개별로 켠다.
        SwarmArenaManager.WaveModeEnabled = false;
    }

    [Fact]
    public void WaveMode_SpawnsEscalatingWavesThatChaseImmediately()
    {
        // #226 웨이브 모드: 캠프 대신 첫 웨이브(해골 5)가 3초 후 참가자 주변에 선다.
        // 시간이 지나면 편성이 격화된다 — 5웨이브(103초+)에는 탈주(120)·볼러(48)가 섞인다.
        SwarmArenaManager.WaveModeEnabled = true;
        try
        {
            DateTime now = StartUtc.AddSeconds(0.25);
            var manager = CreateManager(() => now);
            Vector3f center = AreaCenter(AreaType.Ground);

            // 첫 틱: 스케줄만 생기고 캠프(보스 포함 7기)는 서지 않는다.
            var firstTick = manager.Tick(217001, Participants(center), now);
            Assert.Empty(firstTick.SpawnedMonsters);

            now = StartUtc.AddSeconds(3.5);
            var waveTick = manager.Tick(217001, Participants(center), now);
            Assert.Equal(3, waveTick.SpawnedMonsters.Count);
            Assert.All(waveTick.SpawnedMonsters, monster =>
            {
                Assert.Equal(SwarmArenaManager.MonsterMaxHealth, monster.MaxHealth);
                // 웨이브 몹은 잠들지 않는다 — 스폰 순간부터 스폰 유발자를 쫓는다.
                Assert.Equal(1, monster.ChaseTargetPlayerId);
            });

            now = StartUtc.AddSeconds(110);
            var lateTick = manager.Tick(217001, Participants(center), now);
            Assert.Contains(lateTick.SpawnedMonsters, monster => monster.MaxHealth == 120);
            Assert.Contains(lateTick.SpawnedMonsters, monster => monster.MaxHealth == 48);
        }
        finally
        {
            SwarmArenaManager.WaveModeEnabled = false;
        }
    }

    [Fact]
    public void FirstTick_SpawnsAreaCampsImmediately()
    {
        // #219 M1 캠프 모드: 구역 최초 진입 틱에 캠프가 즉시 선다.
        // #223 보스: 운동장 캠프 0번 = 트리 자이언트(260) 단독 — 1 + 해골 3×2 = 7기.
        DateTime now = StartUtc.AddSeconds(0.25);
        var manager = CreateManager(() => now);
        Vector3f center = AreaCenter(AreaType.Ground);

        var tick = manager.Tick(217001, Participants(center), now);

        Assert.Equal(7, tick.SpawnedMonsters.Count);
        Assert.Equal(1, tick.SpawnedMonsters.Count(monster => monster.MaxHealth == 260));
        Assert.Equal(6, tick.SpawnedMonsters.Count(
            monster => monster.MaxHealth == SwarmArenaManager.MonsterMaxHealth));
        Assert.All(tick.SpawnedMonsters, monster =>
        {
            Assert.Equal(monster.MaxHealth, monster.CurrentHealth);
            Assert.True(monster.IsAlive);
            Assert.Equal(AreaType.Ground, monster.AreaType);
        });

        // 캠프 몹은 예고 없이 즉시 전투 대상이다 — 잠들어 있을 뿐 실체다.
        now = now.AddSeconds(0.1);
        Assert.Equal(7, manager.GetCombatTargets(217001).Count);
    }

    [Fact]
    public void CorridorBand_SpawnsCampsInCloneMap()
    {
        // SB 클론 균질 밀도: 회랑 밴드(테라스=Corridor)에도 캠프가 선다.
        // #223 보스: 회랑 캠프 0번 = 베이비 드래곤(200) 단독 — 1 + 해골 3×2 = 7기.
        DateTime now = StartUtc.AddSeconds(0.25);
        var manager = CreateManager(() => now);
        Vector3f corridor = AreaCenter(AreaType.Corridor);

        var tick = manager.Tick(217001, Participants(corridor, AreaType.Corridor), now);

        Assert.Equal(7, tick.SpawnedMonsters.Count);
        Assert.Equal(1, tick.SpawnedMonsters.Count(monster => monster.MaxHealth == 200));
        Assert.All(tick.SpawnedMonsters, monster => Assert.Equal(AreaType.Corridor, monster.AreaType));
    }

    [Fact]
    public void PodArea_SpawnsSkeletonPackPlusDartAndBruiser()
    {
        // #219 SB 몬스터 4종 (08-09 T1 발수 정렬): 해골 무리(12×3) + 다트(18) + 탈주(60)|볼러(48).
        DateTime now = StartUtc.AddSeconds(0.25);
        var manager = CreateManager(() => now);
        Vector3f library = AreaCenter(AreaType.Library);

        var tick = manager.Tick(217001, Participants(library, AreaType.Library), now);

        Assert.Equal(5, tick.SpawnedMonsters.Count);
        Assert.Equal(3, tick.SpawnedMonsters.Count(monster => monster.MaxHealth == 12));
        Assert.Equal(1, tick.SpawnedMonsters.Count(monster => monster.MaxHealth == 18));
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
        var damageEvents = new List<SpotArenaPlayerDamage>();
        // 커스텀 앵커(CSV 저작)에서는 스폰 틱에도 중앙 접촉이 날 수 있다 — 첫 틱부터 수집한다.
        damageEvents.AddRange(manager.Tick(217001, Participants(center), now).PlayerDamage);

        // #223 보스: 운동장에 트리 자이언트(접촉 6)가 상주한다 — 해골 위에 서서 검증한다.
        var monster = manager.GetVisualStates(217001)
            .First(state => state.IsAlive && state.MaxHealth == SwarmArenaManager.MonsterMaxHealth);
        var onMonster = new Vector3f(monster.PositionX, monster.PositionY, 0f);

        for (double elapsed = 0.5d; elapsed <= 9d; elapsed += 0.25d)
        {
            now = StartUtc.AddSeconds(elapsed);
            damageEvents.AddRange(manager.Tick(217001, Participants(onMonster), now).PlayerDamage);
        }

        Assert.NotEmpty(damageEvents);
        Assert.All(damageEvents, damage =>
        {
            // 해골 접촉 1 (SB: 잡몹은 거의 무해). 첫 틱 중앙 접촉으로 트리 자이언트(6)가 섞일 수 있다.
            Assert.Contains(damage.Damage, new[] { 1, 6 });
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

        // #223 보스: 운동장 첫 타겟이 트리 자이언트(260)일 수 있다 — 해골을 골라 원킬을 검증한다.
        var skeletonIds = manager.GetVisualStates(217001)
            .Where(state => state.IsAlive && state.MaxHealth == SwarmArenaManager.MonsterMaxHealth)
            .Select(state => state.MonsterId)
            .ToHashSet();
        var target = manager.GetCombatTargets(217001).First(candidate =>
            skeletonIds.Contains(candidate.MonsterId));
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

        // 스폰 틱의 중앙 참가자가 커스텀 앵커(CSV 저작) 캠프를 깨웠을 수 있다 —
        // 관찰 대상은 "아직 안 깨어난" 몹으로 고른다. 보스(#223 고정 포대)는 추격이 없어 제외.
        var sleeping = manager.GetVisualStates(217001)
            .First(state => state.IsAlive && state.ChaseTargetPlayerId == 0 &&
                            state.MaxHealth == SwarmArenaManager.MonsterMaxHealth);
        var anchor = new Vector3f(sleeping.PositionX, sleeping.PositionY, 0f);
        // 관찰 지점은 모든 몹과 리쉬(5.5)+어그로 여유 밖(7) — 깨어난 몹도 추격을 끊고 귀환한다.
        var aliveStates = manager.GetVisualStates(217001).Where(state => state.IsAlive).ToList();
        Vector3f watchPoint = default;
        bool watchPointFound = false;
        foreach (var offset in new[]
                 {
                     (X: 7f, Y: 0f), (X: -7f, Y: 0f), (X: 0f, Y: 7f), (X: 0f, Y: -7f),
                     (X: 9f, Y: 0f), (X: -9f, Y: 0f), (X: 0f, Y: 9f), (X: 0f, Y: -9f),
                     (X: 7f, Y: 7f), (X: -7f, Y: -7f), (X: 11f, Y: 0f), (X: -11f, Y: 0f)
                 })
        {
            var candidate = new Vector3f(anchor.X + offset.X, anchor.Y + offset.Y, 0f);
            bool clearOfAll = aliveStates.All(state =>
            {
                float dx = state.PositionX - candidate.X;
                float dy = state.PositionY - candidate.Y;
                return dx * dx + dy * dy > 7f * 7f;
            });
            if (!clearOfAll) continue;
            watchPoint = candidate;
            watchPointFound = true;
            break;
        }

        Assert.True(watchPointFound, "모든 몹과 7 이상 떨어진 관찰 지점을 찾지 못했다");
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

        // 때리면 깨어나 쫓아온다 — 추격 확인은 리쉬(5.5) 안쪽 지점에서 한다.
        // (관찰 지점은 리쉬 밖이라 어그로가 걸려도 즉시 귀환하는 게 정상 동작이다.)
        var chasePoint = new Vector3f(anchor.X + 4f, anchor.Y, 0f);
        var sleepingTarget = manager.GetCombatTargets(217001)
            .First(target => target.MonsterId == sleeping.MonsterId);
        manager.ApplyMonsterDamage(217001, sleepingTarget.CombatTargetId, attackerPlayerId: 1, damage: 1);
        for (double elapsed = 3.25d; elapsed <= 4.5d; elapsed += 0.25d)
        {
            now = StartUtc.AddSeconds(elapsed);
            manager.Tick(217001, Participants(chasePoint), now);
        }

        var chasing = manager.GetVisualStates(217001)
            .First(state => state.MonsterId == sleeping.MonsterId);
        float chaseDx = chasing.PositionX - chasePoint.X;
        float chaseDy = chasing.PositionY - chasePoint.Y;
        float sleepDx = anchor.X - chasePoint.X;
        Assert.True(chaseDx * chaseDx + chaseDy * chaseDy < sleepDx * sleepDx,
            "어그로 후에는 추격 지점 쪽으로 접근해야 한다");

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
