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
    public void RegionSupply_FieldRingWaveSpawnsAtBoundary_AndKeepsWaveRhythm()
    {
        // #269 링 스폰 (2026-08-28 유저 결정): 잔상은 자기장 경계 링에서 지속 스트림(1.5초
        // 주기)으로 태어나 열린 경로로 가운데를 향해 흐른다. 전역 목표 = 인당 목표 × 참가자 수.
        // 봉인 구역(게이지 문 전부 닫힘)에는 태어나지도, 들어가지도 않는다.
        SwarmArenaManager.RegionSupplyModeEnabled = true;
        var sealedRooms = MatchSpawnData.GetPhaseRoomCandidates().ToHashSet();
        SwarmArenaManager.SealedAreaResolver = (_, area) => sealedRooms.Contains(area);
        try
        {
            DateTime now = StartUtc.AddSeconds(0.25);
            var manager = CreateManager(() => now);
            var startRoom = MatchSpawnData.GetPhaseRoomCandidates()[0];
            Vector3f startCenter = AreaCenter(startRoom);

            // 첫 틱부터 웨이브가 돈다 — 페이즈 0 전역 목표 = 인당 8 × 1인.
            var firstTick = manager.Tick(217001, Participants(startCenter, startRoom), now);
            Assert.Equal(8, firstTick.SpawnedMonsters.Count);
            Assert.DoesNotContain(firstTick.SpawnedMonsters, monster => monster.Kind == 2);
            // 수축 전 링 = 열린(비봉인) 셀 중 최외곽 밴드 — 초반 최외곽은 닫힌 방 안이라,
            // 스폰 거리는 열린 셀 최대 거리를 기준으로 잰다.
            int openMaxDistance = SwarmPressureField.DistancesByCell
                .Where(pair => !sealedRooms.Contains(GameMapData.GetCurrentArea(
                    Config.SWARM_MATCH_MAP, new Cell(pair.Key.X, pair.Key.Y))))
                .Max(pair => pair.Value);
            Assert.All(firstTick.SpawnedMonsters, monster =>
            {
                // 페이즈 0 일반 HP — 상향분 원복 (2026-08-16 유저 결정: 잘 죽되 맞으면 치명적)
                Assert.Equal(16, monster.MaxHealth);
                Assert.Equal(1, monster.SummonStoneReward);
                // 봉인 구역(닫힌 방)에서는 태어나지 않는다.
                Assert.DoesNotContain((AreaType)monster.AreaType, sealedRooms);
                var cell = MapCoordinateConverter.WorldToCell(
                    Config.SWARM_MATCH_MAP, new Vector3f(monster.PositionX, monster.PositionY, 0f));
                Assert.True(SwarmPressureField.GetDistance(cell) > openMaxDistance - 8,
                    $"링 밴드 밖 스폰: dist={SwarmPressureField.GetDistance(cell)} openMax={openMaxDistance}");
            });

            // 스트림 주기(1.5초) 안에서는 조용하다.
            now = StartUtc.AddSeconds(1.5);
            Assert.Empty(manager.Tick(217001, Participants(startCenter, startRoom), now).SpawnedMonsters);

            // 전역 목표에 도달해 있으면 스트림도 침묵한다 — 죽는 만큼만 스며나온다.
            for (double elapsed = 2d; elapsed <= 30d; elapsed += 0.25d)
            {
                now = StartUtc.AddSeconds(elapsed);
                Assert.Empty(manager.Tick(217001, Participants(startCenter, startRoom), now).SpawnedMonsters);
            }

            // 전멸 → 다음 웨이브 주기에 재보충된다.
            foreach (var target in manager.GetCombatTargets(217001).ToList())
                manager.ApplyMonsterDamage(217001, target.CombatTargetId, attackerPlayerId: 1, damage: 999);
            Assert.DoesNotContain(manager.GetVisualStates(217001), state => state.IsAlive);

            bool respawned = false;
            for (double elapsed = 30.25d; elapsed <= 45d && !respawned; elapsed += 0.25d)
            {
                now = StartUtc.AddSeconds(elapsed);
                respawned = manager.Tick(217001, Participants(startCenter, startRoom), now)
                    .SpawnedMonsters.Count > 0;
            }

            Assert.True(respawned, "전멸 후 다음 웨이브 주기에 재보충되지 않았다");
        }
        finally
        {
            SwarmArenaManager.RegionSupplyModeEnabled = false;
            SwarmArenaManager.SealedAreaResolver = null;
        }
    }

    [Fact]
    public void RegionSupply_ScalesHealthByPhase_AndCapsGlobalAlive()
    {
        // 곡선 (2026-08-16 유저 결정: 잘 죽되 맞으면 치명적): 최종 페이즈(4:10~)는
        // 일반 22 · 핵 120 · 접촉 40. 단단하게 만드는 방향은 되돌리고 위협은 접촉이 진다.
        // 전역 상한은 구역 목표(= 인당 목표 × 구역 인원)의 합이되 서버 천장 420을 넘지 않는다.
        SwarmArenaManager.RegionSupplyModeEnabled = true;
        try
        {
            DateTime now = StartUtc.AddSeconds(255);
            var manager = new SwarmArenaManager(() => now);
            Assert.True(manager.InitializeMatching(217002, 1, StartUtc));

            var lateTick = manager.Tick(217002, ManyParticipants(8, AreaCenter(Config.SWARM_MATCH_GROUND_AREA)), now);
            Assert.All(lateTick.SpawnedMonsters.Where(monster => monster.Kind == 0),
                normal => Assert.Equal(22, normal.MaxHealth));
            Assert.All(lateTick.SpawnedMonsters.Where(monster => monster.Kind == 2),
                core => Assert.Equal(120, core.MaxHealth));

            // 10인이 서로 다른 구역에 흩어져도 전역 상한 48을 넘지 않는다.
            var rooms = MatchSpawnData.GetPhaseRoomCandidates().Take(5).ToList();
            for (double elapsed = 255.25d; elapsed <= 300d; elapsed += 0.25d)
            {
                now = StartUtc.AddSeconds(elapsed);
                var spread = rooms
                    .SelectMany((room, roomIndex) => Enumerable.Range(0, 2).Select(seat =>
                        new SpotArenaPlayerSpatial(roomIndex * 2 + seat + 1, room, AreaCenter(room))))
                    .ToList();
                manager.Tick(217002, spread, now);
                int alive = manager.GetVisualStates(217002).Count(state => state.IsAlive);
                // 점유 5구역 × 2명 × 인당 목표 28 = 280, 서버 천장 420 이하.
                Assert.True(alive <= 300, $"구역 목표 합 상한 300을 초과했다: {alive}");
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
            var room = MatchSpawnData.GetPhaseRoomCandidates()[0];
            Vector3f roomCenter = AreaCenter(room);
            Vector3f elsewhere = AreaCenter(Config.SWARM_MATCH_GROUND_AREA);

            // 방을 채운다 — #272 School2: 운동장 발원 침투의 행군 거리가 길어져(외곽 시작방)
            // 도착까지 재는 창을 40초로 넓힌다 (RegionSupply_Holds의 60초 창과 같은 이유).
            for (double elapsed = 0.25d; elapsed <= 40d; elapsed += 0.25d)
            {
                now = StartUtc.AddSeconds(elapsed);
                manager.Tick(217004, Participants(roomCenter, room), now);
            }

            int filled = manager.GetVisualStates(217004)
                .Count(state => state.IsAlive && state.AreaType == room);
            Assert.True(filled > 0, "방이 채워지지 않았다");

            // 방을 비운다 — 유예(6초) 안에는 남아 있어야 한다. 나서자마자 뒤에서 사라지면 눈에 띈다.
            now = StartUtc.AddSeconds(42d);
            manager.Tick(217004, Participants(elsewhere), now);
            now = StartUtc.AddSeconds(45d);
            manager.Tick(217004, Participants(elsewhere), now);
            Assert.True(
                manager.GetVisualStates(217004).Any(state => state.IsAlive && state.AreaType == room),
                "유예 안에 잔상이 사라졌다");

            // 유예가 지나면 걷힌다.
            for (double elapsed = 49d; elapsed <= 52d; elapsed += 0.25d)
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
            var room = MatchSpawnData.GetPhaseRoomCandidates()[0];
            Vector3f roomCenter = AreaCenter(room);
            Vector3f elsewhere = AreaCenter(Config.SWARM_MATCH_GROUND_AREA);

            // #269 링 스폰: 예산은 스폰이 아니라 처치 시점에, 죽은 구역 기준으로 차감된다.
            // 페이즈 0(0:00~1:40) 동안 나오는 몹을 즉시 전멸시키기를 반복하며, 구역별 실지급
            // 합이 구역·페이즈 예산(90 + 핵 3 = 93)을 넘지 않는지 잰다.
            var stonesByArea = new Dictionary<AreaType, int>();
            for (double elapsed = 0.25d; elapsed <= 95d; elapsed += 0.25d)
            {
                now = StartUtc.AddSeconds(elapsed);
                bool inRoom = (int)(elapsed / 5d) % 2 == 0;
                manager.Tick(
                    217003,
                    inRoom ? Participants(roomCenter, room) : Participants(elsewhere),
                    now);
                foreach (var target in manager.GetCombatTargets(217003).ToList())
                {
                    var damageResult = manager.ApplyMonsterDamage(
                        217003, target.CombatTargetId, attackerPlayerId: 1, damage: 999);
                    if (damageResult is { Killed: true, MonsterState: not null })
                    {
                        var area = damageResult.MonsterState.AreaType;
                        stonesByArea[area] = stonesByArea.GetValueOrDefault(area) +
                                             damageResult.MonsterState.SummonStoneReward;
                    }
                }
            }

            Assert.True(stonesByArea.Values.Sum() > 0, "예산이 아예 지급되지 않았다");
            foreach (var (area, stones) in stonesByArea)
                Assert.True(stones <= 93, $"페이즈 0 구역 석 예산 93을 초과했다: {area}={stones}");
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
        Vector3f center = AreaCenter(Config.SWARM_MATCH_GROUND_AREA);

        var tick = manager.Tick(217001, Participants(center), now);

        Assert.Equal(7, tick.SpawnedMonsters.Count);
        Assert.Equal(1, tick.SpawnedMonsters.Count(monster => monster.MaxHealth == 260));
        Assert.Equal(6, tick.SpawnedMonsters.Count(
            monster => monster.MaxHealth == SwarmArenaManager.MonsterMaxHealth));
        Assert.All(tick.SpawnedMonsters, monster =>
        {
            Assert.Equal(monster.MaxHealth, monster.CurrentHealth);
            Assert.True(monster.IsAlive);
            Assert.Equal(Config.SWARM_MATCH_GROUND_AREA, monster.AreaType);
        });

        // 캠프 몹은 예고 없이 즉시 전투 대상이다 — 잠들어 있을 뿐 실체다.
        now = now.AddSeconds(0.1);
        Assert.Equal(7, manager.GetCombatTargets(217001).Count);
    }

    // CorridorBand_SpawnsCampsInCloneMap 퇴역 (#272 가운데 병합): 테라스·1차 통로·운동장이
    // S2Corridor9 하나가 되면서 별도 중간 밴드 캠프가 사라졌다 — 병합 구역의 캠프(트리 자이언트
    // 260 + 해골)는 아래 운동장 캠프 테스트들이 잠근다.

    [Fact]
    public void PodArea_SpawnsSkeletonPackPlusDartAndBruiser()
    {
        // #219 SB 몬스터 4종 (08-09 T1 발수 정렬): 해골 무리(12×3) + 다트(18) + 탈주(60)|볼러(48).
        DateTime now = StartUtc.AddSeconds(0.25);
        var manager = CreateManager(() => now);
        // #272 School2: 포드 = 시작방 8곳 (합류 구역은 포드가 아니다).
        var pod = MatchSpawnData.GetPhaseRoomCandidates()[0];
        Vector3f podCenter = AreaCenter(pod);

        var tick = manager.Tick(217001, Participants(podCenter, pod), now);

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
        Vector3f center = AreaCenter(Config.SWARM_MATCH_GROUND_AREA);

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
        Vector3f center = AreaCenter(Config.SWARM_MATCH_GROUND_AREA);
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
        Vector3f center = AreaCenter(Config.SWARM_MATCH_GROUND_AREA);

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
        // #272 가운데 병합: 밴드가 운동장과 같은 구역이 됐다 — 다른 구역 참가자는 도서관1의
        // 먼 구석에 세운다 (좁은 복도는 캠프 앵커와 겹쳐 물린다).
        Vector3f corridor = MapCoordinateConverter.CellToWorld(Config.SWARM_MATCH_MAP, new Cell(126, 90));
        manager.Tick(217001, [new SpotArenaPlayerSpatial(1, Config.SWARM_MATCH_GROUND_AREA, AreaCenter(Config.SWARM_MATCH_GROUND_AREA))], now);

        var monster = manager.GetVisualStates(217001).First(state => state.IsAlive);
        var onMonster = new Vector3f(monster.PositionX, monster.PositionY, 0f);

        var damageEvents = new List<SpotArenaPlayerDamage>();
        for (double elapsed = 0.5d; elapsed <= 9d; elapsed += 0.25d)
        {
            now = StartUtc.AddSeconds(elapsed);
            var participants = new[]
            {
                new SpotArenaPlayerSpatial(1, Config.SWARM_MATCH_GROUND_AREA, onMonster),
                new SpotArenaPlayerSpatial(2, AreaType.S2Library1, corridor)
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
        Vector3f center = AreaCenter(Config.SWARM_MATCH_GROUND_AREA);
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
        AreaType area = AreaType.None) =>
        [new SpotArenaPlayerSpatial(1, ResolveArea(area), position)];

    private static IReadOnlyCollection<SpotArenaPlayerSpatial> ManyParticipants(
        int count,
        Vector3f position,
        AreaType area = AreaType.None) =>
        Enumerable.Range(1, count)
            .Select(id => new SpotArenaPlayerSpatial(id, ResolveArea(area), position))
            .ToList();

    // 기본 구역 = 매치 맵 운동장 (기본 매개변수는 컴파일 상수만 허용 — None을 센티널로 쓴다).
    private static AreaType ResolveArea(AreaType area) =>
        area == AreaType.None ? Config.SWARM_MATCH_GROUND_AREA : area;

    [Fact]
    public void RegionSupply_MonsterPursuesOwnerAcrossDoor()
    {
        // 문 너머 추격 (2026-08-16 유저 결정, 2026-08-28 플레이 제보 "몹이 문 너머로 안 따라온다"):
        // 방에서 나를 담당하던(주인) 몹은 내가 복도로 나가면 문을 넘어 따라와야 한다.
        SwarmArenaManager.RegionSupplyModeEnabled = true;
        try
        {
            DateTime now = StartUtc.AddSeconds(0.25);
            var manager = CreateManager(() => now);
            var startRoom = MatchSpawnData.GetPhaseRoomCandidates()[0];
            Vector3f roomCenter = AreaCenter(startRoom);

            for (double elapsed = 0.25d; elapsed <= 40d; elapsed += 0.25d)
            {
                now = StartUtc.AddSeconds(elapsed);
                manager.Tick(217001, Participants(roomCenter, startRoom), now);
            }

            var roomMonsterIds = manager.GetVisualStates(217001)
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
                manager.Tick(217001, Participants(corridorCenter, corridor), now);
            }

            for (double elapsed = 42.25d; elapsed <= 70d; elapsed += 0.25d)
            {
                now = StartUtc.AddSeconds(elapsed);
                manager.Tick(217001, Participants(corridorCenter, corridor), now);
            }

            var pursuerCount = manager.GetVisualStates(217001)
                .Count(state => state.IsAlive && roomMonsterIds.Contains(state.MonsterId) &&
                                state.AreaType == corridor);
            Assert.True(pursuerCount > 0,
                "방에서 나를 담당하던 몹이 문 너머 복도로 따라와야 한다 — " +
                string.Join(", ", manager.GetVisualStates(217001)
                    .Where(state => state.IsAlive && roomMonsterIds.Contains(state.MonsterId))
                    .Take(4)
                    .Select(state =>
                        $"{state.MonsterId}@{state.AreaType}({state.PositionX:F1},{state.PositionY:F1}) chase={state.ChaseTargetPlayerId}")));
        }
        finally
        {
            SwarmArenaManager.RegionSupplyModeEnabled = false;
        }
    }

    private static SwarmArenaManager CreateManager(Func<DateTime> clock)
    {
        var manager = new SwarmArenaManager(clock);
        Assert.True(manager.InitializeMatching(217001, 1, StartUtc));
        return manager;
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
    public void CampAnchors_StayClearOfAreaWalls()
    {
        // #229: 구역 박스의 테두리가 곧 벽선인데 map_region.csv에는 방 둘레가 obstacle로
        // 적혀 있지 않다. 그래서 테두리 앵커가 "통행 가능"으로 통과하고 잔상이 벽에 낀 채로 선다
        // (행정실: 앵커 3개가 전부 경계 1칸 이내, 그중 둘은 경계선 위).
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
