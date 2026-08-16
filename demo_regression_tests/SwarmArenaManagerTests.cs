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
        // 지역 공급(#226 단계 B)을 끄고 캠프 정규 동작을 검증한다. 공급 테스트는 개별로 켠다.
        SwarmArenaManager.RegionSupplyModeEnabled = false;
    }

    [Fact]
    public void RegionSupply_HoldsZoneTargetWithTopUpsAndWipeRest()
    {
        // #229 4단계: 점유한 열린 구역마다 목표 수를 유지한다. 0초부터 1.5초마다 2마리씩
        // 보충하고, 목표에 닿으면 멈춘다. 전멸시키면 4초 휴지 뒤 보충이 재개된다.
        SwarmArenaManager.RegionSupplyModeEnabled = true;
        try
        {
            DateTime now = StartUtc.AddSeconds(0.25);
            var manager = CreateManager(() => now);
            var startRoom = SurvivorRoyaleSpawnData.GetPhaseRoomCandidates()[0];
            Vector3f startCenter = AreaCenter(startRoom);

            // 첫 틱부터 보충이 돈다 — 시작 선물 15초 침묵(#226 E)은 퇴역했다.
            var firstTick = manager.Tick(217001, Participants(startCenter, startRoom), now);
            // 초반(페이즈 0)은 작은 몹만 나온다 (#229): 시작 오브 하나로는 핵이 벽처럼 서서
            // 파밍이 막힌다. 웨이브 보충(30마리/12초)은 구역 목표에 잘리므로 첫 웨이브는 목표치 8.
            Assert.Equal(8, firstTick.SpawnedMonsters.Count);
            Assert.DoesNotContain(firstTick.SpawnedMonsters, monster => monster.Kind == 2);
            Assert.All(firstTick.SpawnedMonsters, monster =>
            {
                // 공급 몹은 잠든 채 등장한다 — 개전은 근접·피격·접촉의 몫.
                Assert.Equal(0, monster.ChaseTargetPlayerId);
                Assert.Equal(24, monster.MaxHealth); // 페이즈 0 일반 HP (2026-08-16 상향)
                Assert.Equal(1, monster.SummonStoneReward);
            });

            // 웨이브 간격(12초) 안에서는 조용하다 — 웨이브 사이가 곧 정리하는 창이다.
            now = StartUtc.AddSeconds(1.5);
            Assert.Empty(manager.Tick(217001, Participants(startCenter, startRoom), now).SpawnedMonsters);

            // 목표 8을 유지한다 — 2초마다 부족분만큼 한 번에 붓고 쉰다.
            // 창은 60초다 (2026-08-16): 공급이 운동장 발원 침투로 바뀐 뒤로 "구역에 서 있는 수"는
            // 행군 시간만큼 뒤따라온다. 방을 통로로 쓰지 않게 되면서(도서관 관통 금지) 경로가
            // 통로를 도는 만큼 길어져 20초 창에는 절반만 도착했다 — 목표 유지 자체는 성립하므로
            // 도착까지 재는 창으로 넓힌다.
            for (double elapsed = 2d; elapsed <= 60d; elapsed += 0.25d)
            {
                now = StartUtc.AddSeconds(elapsed);
                manager.Tick(217001, Participants(startCenter, startRoom), now);
            }

            int aliveInZone = manager.GetVisualStates(217001)
                .Count(state => state.IsAlive && state.AreaType == startRoom);
            Assert.Equal(8, aliveInZone);

            // 전멸 → 2초 휴지 뒤 보충 재개 (2026-08-16: 웨이브 간격이 12초라 전멸 휴지는 짧게).
            foreach (var target in manager.GetCombatTargets(217001).ToList())
                manager.ApplyMonsterDamage(217001, target.CombatTargetId, attackerPlayerId: 1, damage: 999);
            Assert.Empty(manager.GetVisualStates(217001).Where(state => state.IsAlive));

            now = StartUtc.AddSeconds(60.25);
            manager.Tick(217001, Participants(startCenter, startRoom), now); // 휴지 시작
            now = StartUtc.AddSeconds(61.5d);
            Assert.Empty(manager.Tick(217001, Participants(startCenter, startRoom), now).SpawnedMonsters);
            now = StartUtc.AddSeconds(63d);
            Assert.NotEmpty(manager.Tick(217001, Participants(startCenter, startRoom), now).SpawnedMonsters);
        }
        finally
        {
            SwarmArenaManager.RegionSupplyModeEnabled = false;
        }
    }

    [Fact]
    public void RegionSupply_ScalesHealthByPhase_AndCapsGlobalAlive()
    {
        // 곡선 (2026-08-16 상향): 최종 페이즈(4:10~)는 일반 64 · 핵 160 · 접촉 16.
        // 한 마리를 단단하게 만들어 회전율을 낮춘다 — 나오는 족족 녹던 구조를 끊는다.
        // 전역 상한은 점유 구역 수 × 페이즈 목표(60)로 풀리되 서버 천장 420을 넘지 않는다.
        SwarmArenaManager.RegionSupplyModeEnabled = true;
        try
        {
            DateTime now = StartUtc.AddSeconds(255);
            var manager = new SwarmArenaManager(() => now);
            Assert.True(manager.InitializeMatching(217002, 1, StartUtc));

            var lateTick = manager.Tick(217002, ManyParticipants(8, AreaCenter(AreaType.Ground)), now);
            Assert.All(lateTick.SpawnedMonsters.Where(monster => monster.Kind == 0),
                normal => Assert.Equal(64, normal.MaxHealth));
            Assert.All(lateTick.SpawnedMonsters.Where(monster => monster.Kind == 2),
                core => Assert.Equal(160, core.MaxHealth));

            // 10인이 서로 다른 구역에 흩어져도 전역 상한 48을 넘지 않는다.
            var rooms = SurvivorRoyaleSpawnData.GetPhaseRoomCandidates().Take(5).ToList();
            for (double elapsed = 255.25d; elapsed <= 300d; elapsed += 0.25d)
            {
                now = StartUtc.AddSeconds(elapsed);
                var spread = rooms
                    .SelectMany((room, roomIndex) => Enumerable.Range(0, 2).Select(seat =>
                        new SpotArenaPlayerSpatial(roomIndex * 2 + seat + 1, room, AreaCenter(room))))
                    .ToList();
                manager.Tick(217002, spread, now);
                int alive = manager.GetVisualStates(217002).Count(state => state.IsAlive);
                // 점유 5구역 × 목표 60 = 300, 서버 천장 420 이하.
                Assert.True(alive <= 300, $"점유 구역 비례 상한 300을 초과했다: {alive}");
            }
        }
        finally
        {
            SwarmArenaManager.RegionSupplyModeEnabled = false;
        }
    }

    // #229 4단계-보정: 전역 상한은 하나뿐이라 아무도 없는 구역의 잔상이 살아 있는 전장의
    // 몫을 영구히 먹는다. 폐쇄 구역은 도달조차 못 하므로 순수 낭비다 — 폐쇄가 누적되면
    // 최악에는 전 구역 스폰이 0으로 굳었다. 걷어내는 규칙을 잠근다.
    [Fact]
    public void RegionSupply_ReclaimsStrandedMonstersAfterZoneIsVacated()
    {
        SwarmArenaManager.RegionSupplyModeEnabled = true;
        try
        {
            DateTime now = StartUtc;
            var manager = new SwarmArenaManager(() => now);
            Assert.True(manager.InitializeMatching(217004, 1, StartUtc));
            var room = SurvivorRoyaleSpawnData.GetPhaseRoomCandidates()[0];
            Vector3f roomCenter = AreaCenter(room);
            Vector3f elsewhere = AreaCenter(AreaType.Ground);

            // 방을 채운다.
            for (double elapsed = 0.25d; elapsed <= 12d; elapsed += 0.25d)
            {
                now = StartUtc.AddSeconds(elapsed);
                manager.Tick(217004, Participants(roomCenter, room), now);
            }

            int filled = manager.GetVisualStates(217004)
                .Count(state => state.IsAlive && state.AreaType == room);
            Assert.True(filled > 0, "방이 채워지지 않았다");

            // 방을 비운다 — 유예(6초) 안에는 남아 있어야 한다. 나서자마자 뒤에서 사라지면 눈에 띈다.
            now = StartUtc.AddSeconds(14d);
            manager.Tick(217004, Participants(elsewhere), now);
            now = StartUtc.AddSeconds(17d);
            manager.Tick(217004, Participants(elsewhere), now);
            Assert.True(
                manager.GetVisualStates(217004).Any(state => state.IsAlive && state.AreaType == room),
                "유예 안에 잔상이 사라졌다");

            // 유예가 지나면 걷힌다.
            for (double elapsed = 21d; elapsed <= 24d; elapsed += 0.25d)
            {
                now = StartUtc.AddSeconds(elapsed);
                manager.Tick(217004, Participants(elsewhere), now);
            }

            Assert.DoesNotContain(
                manager.GetVisualStates(217004),
                state => state.IsAlive && state.AreaType == room);
        }
        finally
        {
            SwarmArenaManager.RegionSupplyModeEnabled = false;
        }
    }

    [Fact]
    public void RegionSupply_StoneBudgetSurvivesZoneReentry()
    {
        // #229 4단계: 소환석 예산은 구역·페이즈 단위다. 봇처럼 구역을 들락날락해도
        // 예산이 리셋되면 안 된다 — 실측(매치 9687066)에서 한 구역이 페이즈 1 예산 11석 대신
        // 56석을 받았다. 보충 타이머는 버리되 예산 원장은 남긴다.
        SwarmArenaManager.RegionSupplyModeEnabled = true;
        try
        {
            DateTime now = StartUtc;
            var manager = new SwarmArenaManager(() => now);
            Assert.True(manager.InitializeMatching(217003, 1, StartUtc));
            var room = SurvivorRoyaleSpawnData.GetPhaseRoomCandidates()[0];
            Vector3f roomCenter = AreaCenter(room);
            Vector3f elsewhere = AreaCenter(AreaType.Ground);

            int stones = 0;
            // 이 구역에 머물다 나갔다를 반복한다 — 페이즈 0(0:00~1:40) 안에서만 논다.
            for (double elapsed = 0.25d; elapsed <= 95d; elapsed += 0.25d)
            {
                now = StartUtc.AddSeconds(elapsed);
                bool inRoom = (int)(elapsed / 5d) % 2 == 0;
                var tick = manager.Tick(
                    217003,
                    inRoom ? Participants(roomCenter, room) : Participants(elsewhere),
                    now);
                stones += tick.SupplyPackSpawns.Where(spawn => spawn.Area == room)
                    .Sum(spawn => spawn.StoneTotal);
            }

            // 페이즈 0 예산 90 + 핵 1기 3 = 93이 상한이다.
            Assert.True(stones <= 93, $"페이즈 0 구역 석 예산 93을 초과했다: {stones}");
            Assert.True(stones > 0, "예산이 아예 지급되지 않았다");
        }
        finally
        {
            SwarmArenaManager.RegionSupplyModeEnabled = false;
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
        // 무적창(0.6초)보다 촘촘히 맞을 수 없다 — 8.5초 관찰이면 상한 15대다 (#229).
        Assert.InRange(damageEvents.Count, 1, 15);
    }

    [Fact]
    public void ContactRadius_FollowsClientKindScaleLadder()
    {
        // 판정 = 보이는 몸통 (#229). 클라 ResolveKindScale과 같은 사다리라
        // 한쪽만 바뀌면 스프라이트와 판정이 어긋난다 — 여기서 잠근다.
        Assert.Equal(SwarmArenaManager.ContactRange,
            SwarmArenaManager.GetContactRadius(SwarmMonsterKind.Skeleton), 3);
        Assert.Equal(SwarmArenaManager.ContactRange * 1.4f,
            SwarmArenaManager.GetContactRadius(SwarmMonsterKind.DartGoblin), 3);
        Assert.Equal(SwarmArenaManager.ContactRange * 2.4f,
            SwarmArenaManager.GetContactRadius(SwarmMonsterKind.RunawayGoblin), 3);
        Assert.Equal(SwarmArenaManager.ContactRange * 1.8f,
            SwarmArenaManager.GetContactRadius(SwarmMonsterKind.TreeGiant), 3);

        // 해골 반경은 몸통 반폭(0.31, 클라 실측)을 넘지 않는다.
        Assert.True(SwarmArenaManager.GetContactRadius(SwarmMonsterKind.Skeleton) <= 0.32f);
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

    private static IReadOnlyCollection<SpotArenaPlayerSpatial> ManyParticipants(
        int count,
        Vector3f position,
        AreaType area = AreaType.Ground) =>
        Enumerable.Range(1, count)
            .Select(id => new SpotArenaPlayerSpatial(id, area, position))
            .ToList();

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

    [Fact]
    public void CampAnchors_StayClearOfAreaWalls()
    {
        // #229: 구역 박스의 테두리가 곧 벽선인데 map_region.csv에는 방 둘레가 obstacle로
        // 적혀 있지 않다. 그래서 테두리 앵커가 "통행 가능"으로 통과하고 잔상이 벽에 낀 채로 선다
        // (행정실: 앵커 3개가 전부 경계 1칸 이내, 그중 둘은 경계선 위).
        const int margin = 3;
        foreach (var region in GameMapData.GetAreas(MapId.School))
        {
            if (region.End.X - region.Start.X < margin * 2 ||
                region.End.Y - region.Start.Y < margin * 2)
                continue;

            for (int campIndex = 0; campIndex < 3; campIndex++)
            {
                var authored = GameMonsterCampData.GetAnchor(region.AreaType, campIndex);
                if (authored == null) continue;

                var inset = SwarmArenaManager.InsetAnchorFromAreaEdge(authored, region.AreaType);
                int clearance = Math.Min(
                    Math.Min(inset.X - region.Start.X, region.End.X - inset.X),
                    Math.Min(inset.Y - region.Start.Y, region.End.Y - inset.Y));
                Assert.True(clearance >= margin,
                    $"{region.AreaType} 캠프 {campIndex} 앵커가 벽에 붙었다: " +
                    $"({inset.X},{inset.Y}) 여유 {clearance}");
            }
        }
    }
}
