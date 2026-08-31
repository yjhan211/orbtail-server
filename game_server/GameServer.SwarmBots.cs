using game_server.network;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;
using network.packets;

namespace game_server;

public partial class GameServer
{
    // #312 봇/몹 소유권 분리: SwarmArena 파셜에서 이주한 봇 틱 페이즈·판단 로직.
    // 봇 = 가짜 플레이어(참가자 레이어). 호출 순서는 여전히 SwarmArena 틱 본체가 소유한다.


    // 봇 오염 자연 회복: 회복 오브 운에 기대지 않는 생존 바닥. 마지막 피격 후 유예가
    // 지나면 초당 일정량 회복한다 — "도망 성공"이 실제 생존이 되게 (계측: 매치 2223에서
    // 봇 오염이 단조 증가해 85초 전멸). 사람은 위로 오브가 같은 역할을 하므로 제외.
    // 봇 수동 회복 (#229 4단계-보정 하향): 4초 유예 뒤 초당 4는 접촉 피해 최대치(1.25~2.5/초)를
    // 앞질러, 봇이 잔상에게 수학적으로 죽을 수 없었다 — 매치 9761085에서 탈락 9건이 전부
    // 폐쇄사이고 몹 사망 0건인 이유다. 사람은 이만한 수동 회복이 없다(수면은 정지·무피격을
    // 요구하고 맞으면 끊긴다). 유예를 늘리고 속도를 낮춰 같은 압력을 받게 한다.
    private const double SwarmBotRecoveryGraceSeconds = 6d;
    private const int SwarmBotRecoveryPerSecond = 2;

    private const float SwarmBotOpenRange = 1.6f;
    // 봇 접촉 피해 배율은 퇴역했다 (2026-08-16). 사람 쪽 반감(SwarmMonsterDamageTakenMultiplier)을
    // 걷을 때 이 쌍둥이를 놓쳐, 사람만 설계값 2/3/4/6/8을 받고 봇은 절반을 받고 있었다 —
    // 봇 매치 9864958에서 피격 85건의 피해가 1(77건)·2(8건)뿐이었다(round(2*0.5)=1, round(3*0.5)=2).
    // 같은 규칙을 받아야 봇 매치로 위협도를 잴 수 있다.
    private const float SwarmBotContactDamageMultiplier = 1f;


    /// <summary>
    ///     봇의 스팟 개봉: 게이지 없이 반경 안에서 즉시 연다. 소진 스팟은 사람·봇 공용
    ///     쿨다운 저장소로 잠기므로, 유한 스팟을 둘러싼 경쟁이 성립한다.
    /// </summary>
    // 봇 채집 채널 (#219 탐색 모션): 즉시 개봉은 모션도 없고 사람(1.5초 채집)보다 빨랐다.
    // 사람 클라와 같은 1.5초 채널 동안 EXPLORE_1 상태로 서 있다가 개봉을 확정한다.
    private const double SwarmBotExploreChannelSeconds = 1.5d;

    // 봇 문 잠금해제 (#229). 사람과 같은 규칙을 봇에도 건다 — 봇만 잠긴 문을 통과하면
    // 폐쇄 압력이 봇에게만 무의미해지고, 봇 매치로 이 메카닉을 검증할 수도 없다.
    // #272: 채널 길이는 사람 게이지와 같은 Config 문 등급 값을 쓴다 (합류 문 = 듀얼 관문 12초).
    private const float SwarmBotDoorUnlockRange = 1.6f;

    private void ProcessSwarmBotDoorUnlocks(
        long matchingId,
        List<BotPlayerState> bots,
        List<GameClientSession> sessions,
        DateTime nowUtc)
    {
        foreach (var bot in bots)
        {
            // 진행 중 — 맞았으면 풀리고, 다 채웠으면 열린다
            if (bot.SwarmDoorUnlockDoorId > 0)
            {
                if (bot.LastDamagedAtUtc > bot.SwarmDoorUnlockStartedAtUtc)
                {
                    bot.SwarmDoorUnlockDoorId = 0;
                    bot.SwarmDoorUnlockStartedAtUtc = DateTime.MinValue;
                    continue;
                }

                if ((nowUtc - bot.SwarmDoorUnlockStartedAtUtc).TotalSeconds <
                    Config.GetSwarmDoorGaugeSeconds(bot.SwarmDoorUnlockDoorId))
                    continue;

                int doorId = bot.SwarmDoorUnlockDoorId;
                bot.SwarmDoorUnlockDoorId = 0;
                bot.SwarmDoorUnlockStartedAtUtc = DateTime.MinValue;
                if (!_doorStateManager.OpenDoor(matchingId, doorId)) continue;

                using var openPacket =
                    PacketMaker.G_TO_C_DOOR_STATE_UPDATE(doorId, true, ErrorCode.SUCCESS, bot.PlayerId);
                foreach (var session in sessions) session.Send(openPacket);
                logger.LogInformation(
                    "Swarm bot unlocked door: MatchingId={MatchingId}, BotId={BotId}, DoorId={DoorId}",
                    matchingId, bot.PlayerId, doorId);
                continue;
            }

            // 시작 — 내 구역의 잠긴 게이트 문 중 가장 가까운 것
            if (!TryFindNearestLockedGaugeDoor(matchingId, bot, out int targetDoorId)) continue;

            bot.SwarmDoorUnlockDoorId = targetDoorId;
            bot.SwarmDoorUnlockStartedAtUtc = nowUtc;
        }
    }

    private bool TryFindNearestLockedGaugeDoor(long matchingId, BotPlayerState bot, out int doorId)
    {
        doorId = 0;
        float best = float.MaxValue;
        // 폐쇄 문 규칙은 사람과 같다 (2026-08-18): 밖에서 폐쇄 구역으로 들어가는 문은 못 따고,
        // 내가 폐쇄 구역 안이면 어느 문이든 따서 나간다.
        bool insideClosed = _areaClosureManager.IsAreaClosed(matchingId, bot.CurrentArea);
        foreach (var door in GameDoorData.GetByAreaType(bot.CurrentArea))
        {
            if (!GameInteractableData.IsGaugeGatedDoor(door.DoorId)) continue;
            if (_doorStateManager.IsDoorOpen(matchingId, door.DoorId)) continue;
            // 단방향 문 (2026-08-28 플레이 제보 "봇이 바깥에서 문을 따고 들어온다"): 게이지가
            // 놓인 쪽(안쪽)에서만 딴다 — 사람은 게이지 노출 규칙이 이미 막고 있고, 봇도 같은
            // 표를 따른다. 폐쇄 구역 탈출은 예외 (사람 규칙과 동일).
            if (!insideClosed &&
                !GameInteractableData.IsGaugeDoorOperableFrom(door.DoorId, (int)bot.CurrentArea))
                continue;
            if (!insideClosed &&
                (_areaClosureManager.IsAreaClosed(matchingId, door.AreaType) ||
                 _areaClosureManager.IsAreaClosed(matchingId, door.AreaTypeB)))
                continue;

            // door_info의 좌표는 셀 단위다 — 봇 위치(월드)와 직접 비교하면 절대 닿지 않는다.
            var doorWorld = BotPlayerManager.CellToWorldPosition(
                Config.SWARM_MATCH_MAP, new Cell((int)door.PositionX, (int)door.PositionY));
            float dx = doorWorld.X - bot.Position.X;
            float dy = doorWorld.Y - bot.Position.Y;
            float distanceSquared = dx * dx + dy * dy;
            if (distanceSquared > SwarmBotDoorUnlockRange * SwarmBotDoorUnlockRange) continue;
            if (distanceSquared >= best) continue;

            best = distanceSquared;
            doorId = door.DoorId;
        }

        return doorId > 0;
    }

    private void ProcessSwarmBotExplores(
        long matchingId,
        List<BotPlayerState> bots,
        List<GameClientSession> sessions)
    {
        // #229 5단계: 자동 탐색 임시 중단. 상자 앞에 걸어가 서 있는 봇이 남지 않게
        // 채널 시작 자체를 막는다.
        if (Config.IsSwarmExploreDisabled())
            return;

        foreach (var bot in bots)
        {
            // (2) 채널 진행 중 — 1.5초가 지나면 개봉 확정
            if (bot.SwarmExploreStartedAtUtc != DateTime.MinValue)
            {
                if ((DateTime.UtcNow - bot.SwarmExploreStartedAtUtc).TotalSeconds <
                    SwarmBotExploreChannelSeconds)
                    continue;

                FinishSwarmBotExplore(matchingId, bot, sessions);
                continue;
            }

            // (1) 채널 시작: 근접 + 자금 + 스팟 가용이면 쿨다운을 선점하고 채집 자세로 선다
            if (!TryFindNearestAvailableExploreSpot(
                    matchingId, bot.CurrentArea, bot.Position, out var spot, out float distance) ||
                distance > SwarmBotOpenRange)
                continue;

            // 위협 사거리 안에서는 채집을 열지 않는다 — 채널 홀드 채로 얻어맞는 사고 방지
            // (매치 2376 봇 -108: 빈손으로 채집 반복하며 인지 밖 파도 사거리에 일방 피격).
            float channelPower = GetSwarmSquadPower(matchingId, bot.PlayerId);
            FindNearbySwarmRivals(matchingId, bot, channelPower,
                includeMonstersAsStronger: channelPower <= 0f,
                out var channelThreatPosition, out _);
            if (channelThreatPosition != null)
                continue;

            int exploreCost = GetSwarmBotExploreCost(matchingId, bot.PlayerId);
            // 열쇠 (#222 M4): 충전이 있으면 자금 없이도 개봉을 연다.
            if (_summonStoneManager.GetSnapshot(matchingId, bot.PlayerId).StoneCount < exploreCost &&
                bot.FreeSummonCharges <= 0)
                continue;

            if (!RngCollectCooldownStore.TryAcquireCooldown(
                    matchingId, spot.Id, Config.SWARM_EXPLORE_REGEN_SECONDS, out _))
                continue;

            bot.SwarmExploreSpotId = spot.Id;
            bot.SwarmExploreStartedAtUtc = DateTime.UtcNow;
            bot.HoldForChannel(TimeSpan.FromSeconds(SwarmBotExploreChannelSeconds + 0.5d));
            BroadcastBotExploreStarts(
                matchingId, [(bot.PlayerId, spot.Id, bot.CurrentArea)], sessions);
        }
    }

    private void FinishSwarmBotExplore(long matchingId, BotPlayerState bot, List<GameClientSession> sessions)
    {
        int spotId = bot.SwarmExploreSpotId;
        bot.SwarmExploreSpotId = 0;
        bot.SwarmExploreStartedAtUtc = DateTime.MinValue;
        BroadcastBotExploreEnds(matchingId, [(bot.PlayerId, bot.CurrentArea)], sessions);
        if (spotId <= 0)
            return;

        // #229 5단계: 봇도 사람과 같은 규칙 — 스웜에서는 상자를 열지 않는다.
        if (Config.IsSwarmExploreDisabled())
        {
            RngCollectCooldownStore.ClearCooldown(matchingId, spotId);
            return;
        }

        // #226 단계 C: 상자 = 소모품 공급처 (사람과 같은 규칙) — 오브 성장은 성장 카드가 맡는다.
        if (!_summonStoneManager.TrySpendStones(
                matchingId, bot.PlayerId, Config.SWARM_BOX_OPEN_COST, out _))
        {
            RngCollectCooldownStore.ClearCooldown(matchingId, spotId);
            return;
        }

        int dropItemId = Random.Shared.Next(100) < 60
            ? Config.HEART_GROUND_ITEM_ID
            : Config.BOOTS_GROUND_ITEM_ID;
        var dropped = _groundItemManager.SpawnItems(
            matchingId, bot.CurrentArea, bot.Position.X, bot.Position.Y, [dropItemId],
            mapId: Config.SWARM_MATCH_MAP,
            layout: GroundItemSpawnLayout.EliminationScatter);
        if (dropped.Count > 0)
        {
            int remaining = _areaItemStockManager.GetRemainingCount(matchingId, (int)bot.CurrentArea);
            using var packet = PacketMaker.G_TO_C_GROUND_ITEM_SPAWN(
                (int)bot.CurrentArea, remaining, dropped.ToList());
            foreach (var session in sessions)
                if (session.PlayerId.HasValue && session.CurrentArea == bot.CurrentArea)
                    session.Send(packet);
        }

        BroadcastSwarmExploreConsumed(spotId, Config.SWARM_EXPLORE_REGEN_SECONDS, sessions);
        logger.LogInformation(
            "Swarm bot box consumable: MatchingId={MatchingId}, BotId={BotId}, InteractId={InteractId}, Drop={DropItemId}",
            matchingId, bot.PlayerId, spotId, dropItemId);
    }

    // 시작방 팩이 마르면 봇이 이주할 무한 스폰 사냥터.
    // #272 School2: 합류 구역 4곳 + 운동장 — 순례 목적지가 곧 수렴 동선이다.
    private static readonly AreaType[] SwarmHuntingAreas =
    [
        Config.SWARM_MATCH_GROUND_AREA, AreaType.S2Library1, AreaType.S2Library2,
        AreaType.S2Gym1, AreaType.S2Gym2
    ];

    /// <summary>
    ///     봇 이동 지시 라우팅: 도주(생존) > 바닥 소환석 줍기 > 전 구역 스팟 순례 >
    ///     마른 방 탈출(사냥터 이주) > 스웜 디렉터 배회.
    /// </summary>
    // 왕복 억제 (#226 F): 방금 떠난 구역으로 수 초 내 복귀하는 지시는 판단 떨림이다 —
    // 봇 매치 계측에서 2초 내 직전 구역 복귀가 112회 나왔다. 피격 도주·경계 대피는 예외.
    private const double SwarmBotAreaReturnCooldownSeconds = 5d;

    // 봇 구역 기억·도주 지시·절단 후 회수 창 상태는 GetSwarmMatchRuntime(matchingId).BotTactics (#294 상태 홀더).
    private const double SwarmBotPostCutLootSeconds = 5d;

    // 폐쇄 조기 철수 (#226 F): 경고 잔여가 (기본 + 오브당 가산) 이하로 내려오면 나간다 —
    // 긴 꼬리는 문 통과가 느리고, 폐쇄 잔류 꼬리는 무보상 파괴된다.
    private const double SwarmBotClosureEvacuateBaseSeconds = 4d;
    private const double SwarmBotClosureEvacuatePerOrbSeconds = 0.5d;

    // 자기장 대피 여유 (셀, #272): 경계에 이만큼 다가서면 미리 물러나고, 두 배 안쪽까지 들어간다.
    // 3 → 5 (School2 실측 9006207): 반경이 커진 신맵은 경계가 초당 약 0.28셀로 60% 빨라
    // 3셀 마진이 11초에 불과했다 — 합류→통로 이송 중 봇 5/8이 오염사. 5셀 = 약 18초로
    // School 시절 여유를 복원한다. 재발동 간격도 같은 비율이라 와리가리하지 않는다.
    private const int SwarmBotFieldEvacuateMarginCells = 5;

    // 방 마감 선제 탈출 리드 (초): 문 잠금 전에 방을 비우는 여유 — 큰 방 횡단 + 문 경유 시간.
    private const double SwarmBotAreaExitLeadSeconds = 25d;

    /// <summary>
    ///     자기장 안쪽 대피 목적지 (#272, 매치 3030 실측 수리): 옛 후보(SwarmHuntingAreas =
    ///     외곽 사냥방)는 원형 자기장에서 다음 희생양이라, 대피한 봇들이 바깥 방으로 몰려가
    ///     절반이 자기장에 죽었다. 여유(6셀)까지 안전한 셀을 가진 구역 중 안쪽 셀이 봇에서
    ///     가장 가까운 곳으로 보낸다 — 목적지 셀은 그 구역의 가장 안쪽 셀이다.
    /// </summary>
    private (AreaType Area, Cell Cell) ResolveSwarmFieldEvacuationTarget(long matchingId, Vector3f botPosition)
    {
        double safeDistance = GetSwarmSafeDistance(matchingId, DateTime.UtcNow);
        AreaType bestArea = Config.SWARM_MATCH_GROUND_AREA;
        Cell bestCell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, Config.SWARM_MATCH_GROUND_AREA);
        float bestSq = float.MaxValue;
        foreach (var region in GameMapData.GetAreas(Config.SWARM_MATCH_MAP))
        {
            var area = region.AreaType;
            if (area == AreaType.None) continue;
            var cells = GetSwarmAreaCellsByDistance(area);
            if (cells.Count == 0 ||
                cells[0].Distance > safeDistance - SwarmBotFieldEvacuateMarginCells * 2)
                continue;

            var innermost = BotPlayerManager.CellToWorldPosition(Config.SWARM_MATCH_MAP, cells[0].Cell);
            float dx = innermost.X - botPosition.X;
            float dy = innermost.Y - botPosition.Y;
            float distanceSq = dx * dx + dy * dy;
            if (distanceSq >= bestSq) continue;
            bestSq = distanceSq;
            bestArea = area;
            bestCell = cells[0].Cell;
        }

        return (bestArea, bestCell);
    }

    private SpotArenaBotDirective ResolveSwarmBotDirective(long matchingId, long botPlayerId)
    {
        var directive = ResolveSwarmBotDirectiveCore(matchingId, botPlayerId);
        var bot = _botPlayerManager.GetBots(matchingId)
            .FirstOrDefault(candidate => candidate.PlayerId == botPlayerId);
        if (bot == null || bot.IsEliminated || bot.CurrentArea == AreaType.None)
            return directive;

        var key = (matchingId, botPlayerId);
        if (!GetSwarmMatchRuntime(matchingId).BotTactics.AreaMemory.TryGetValue(key, out var memory))
        {
            GetSwarmMatchRuntime(matchingId).BotTactics.AreaMemory[key] = (bot.CurrentArea, AreaType.None, DateTime.MinValue);
            return directive;
        }

        if (memory.Area != bot.CurrentArea)
        {
            memory = (bot.CurrentArea, memory.Area, DateTime.UtcNow);
            GetSwarmMatchRuntime(matchingId).BotTactics.AreaMemory[key] = memory;
        }

        if (directive.Mode != SpotArenaBotMode.Escort)
            return directive;

        // 도주·대피 지시는 어디로든 즉시 — 억제는 경제·추격 지시의 판단 떨림에만 건다.
        if (GetSwarmMatchRuntime(matchingId).BotTactics.FleeDirective.Contains(key))
            return directive;

        if (directive.DestinationArea == memory.PreviousArea &&
            directive.DestinationArea != bot.CurrentArea &&
            (DateTime.UtcNow - memory.LeftAtUtc).TotalSeconds < SwarmBotAreaReturnCooldownSeconds)
        {
            // 복귀 지시 강등: 쿨다운 동안 현 구역 제자리 — 자동 전투·픽업은 계속 돈다.
            return new SpotArenaBotDirective(
                SpotArenaBotMode.Escort,
                bot.CurrentArea,
                ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, bot.Position),
                bot.Position);
        }

        return directive;
    }

    private SpotArenaBotDirective ResolveSwarmBotDirectiveCore(long matchingId, long botPlayerId)
    {
        GetSwarmMatchRuntime(matchingId).BotTactics.FleeDirective.Remove((matchingId, botPlayerId));
        var directive = _swarmMonsterDirector.GetBotDirective(matchingId, botPlayerId);

        var bot = _botPlayerManager.GetBots(matchingId)
            .FirstOrDefault(candidate => candidate.PlayerId == botPlayerId);
        if (bot == null || bot.IsEliminated)
            return directive;

        // 폐쇄·경계 탈출은 위협 판정보다 위다 (#229 8단계). GetBotDirective는 내 구역에 깨어난
        // 몹이 하나라도 있으면 Return을 준다. 밀도 램프 이후 구역당 7~15마리라 이 조건이 상시
        // 참이 됐고, 아래의 대피·전력 비교·추격이 통째로 죽어 있었다. 봇 매치 9772501에서
        // 10명 중 9명이 스폰 방을 한 번도 안 나가고 그 방이 닫힐 때 죽었다 —
        // 폐쇄가 수렴 장치가 아니라 타이머 처형으로 동작했다.
        // 몹은 도망칠 수 있고 폐쇄는 못 도망친다. 순서가 그대로 우선순위다.

        // 0) 경계 밖 탈출 최우선 — 자기장에서는 안쪽으로 걷는 것 자체가 대피 경로다.
        //    목적지는 자기장 안쪽 대피 구역 (#272 수리: 옛 사냥터 후보는 외곽 방이라 다음 희생양).
        if (IsSwarmAreaOutside(matchingId, bot.CurrentArea))
        {
            GetSwarmMatchRuntime(matchingId).BotTactics.FleeDirective.Add((matchingId, botPlayerId));
            var (evacuationArea, evacuationCell) =
                ResolveSwarmFieldEvacuationTarget(matchingId, bot.Position);
            return new SpotArenaBotDirective(
                SpotArenaBotMode.Escort,
                evacuationArea,
                evacuationCell,
                BotPlayerManager.CellToWorldPosition(Config.SWARM_MATCH_MAP, evacuationCell));
        }

        // 0.2) 자기장 셀 대피 (#272, 2026-08-26 유저 제보 "봇이 자기장을 무시한다"): 구역 단위
        //      신호(완전-밖·경고)만 보면 경계가 방을 관통하는 동안 빨간 쪽에 선 봇이 오염을
        //      그대로 마신다. 내 셀이 경계 밖이거나 여유(3셀) 안이면 같은 구역의 안쪽 셀로
        //      물러나고, 구역에 안전 셀이 없으면 경계 안 이웃 구역으로 나간다.
        double fieldSafeDistance = GetSwarmSafeDistance(matchingId, DateTime.UtcNow);
        if (fieldSafeDistance < double.MaxValue)
        {
            // 0.15) 방 마감 선제 탈출 (#272, 봇 매치 9831482 실측: 3-2교실 폐쇄 39초 뒤에도 봇이
            //       남아 420 사망): 경고(15초 전) 기반 철수는 큰 방·문 경유 이동에 너무 늦다 —
            //       내 구역이 잠기기까지 25초 안이면 지금 나간다. 수축은 선형이라 시각이 정확하다.
            double shrinkRatePerSecond = SwarmPressureField.MaxDistance / SwarmFieldShrinkSeconds;
            int currentAreaMinDistance = SwarmPressureField.GetAreaMinDistance(bot.CurrentArea);
            double secondsUntilAreaOutside =
                (fieldSafeDistance - currentAreaMinDistance) / shrinkRatePerSecond;
            if (secondsUntilAreaOutside < SwarmBotAreaExitLeadSeconds)
            {
                GetSwarmMatchRuntime(matchingId).BotTactics.FleeDirective.Add((matchingId, botPlayerId));
                var (exitArea, exitCell) = ResolveSwarmFieldEvacuationTarget(matchingId, bot.Position);
                return new SpotArenaBotDirective(
                    SpotArenaBotMode.Escort,
                    exitArea,
                    exitCell,
                    BotPlayerManager.CellToWorldPosition(Config.SWARM_MATCH_MAP, exitCell));
            }

            var botCell = ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, bot.Position);
            if (SwarmPressureField.GetDistance(botCell) >
                fieldSafeDistance - SwarmBotFieldEvacuateMarginCells)
            {
                // 대피는 도주 예외 — 왕복 억제를 우회해 즉시 물러난다.
                GetSwarmMatchRuntime(matchingId).BotTactics.FleeDirective.Add((matchingId, botPlayerId));

                // 같은 구역에서 여유 두 배(6셀)까지 안전한 셀 중 가장 가까운 곳으로.
                Cell? retreatCell = null;
                float retreatBestSq = float.MaxValue;
                foreach (var entry in GetSwarmAreaCellsByDistance(bot.CurrentArea))
                {
                    if (entry.Distance > fieldSafeDistance - SwarmBotFieldEvacuateMarginCells * 2)
                        break;
                    var candidate = BotPlayerManager.CellToWorldPosition(Config.SWARM_MATCH_MAP, entry.Cell);
                    float candidateDx = candidate.X - bot.Position.X;
                    float candidateDy = candidate.Y - bot.Position.Y;
                    float candidateSq = candidateDx * candidateDx + candidateDy * candidateDy;
                    if (candidateSq >= retreatBestSq) continue;
                    retreatBestSq = candidateSq;
                    retreatCell = entry.Cell;
                }

                if (retreatCell != null)
                    return new SpotArenaBotDirective(
                        SpotArenaBotMode.Escort,
                        bot.CurrentArea,
                        retreatCell,
                        BotPlayerManager.CellToWorldPosition(Config.SWARM_MATCH_MAP, retreatCell));

                // 이 방엔 이제 설 자리가 없다 — 자기장 안쪽 대피 구역으로.
                var (fieldEvacuationArea, fieldEvacuationCell) =
                    ResolveSwarmFieldEvacuationTarget(matchingId, bot.Position);
                return new SpotArenaBotDirective(
                    SpotArenaBotMode.Escort,
                    fieldEvacuationArea,
                    fieldEvacuationCell,
                    BotPlayerManager.CellToWorldPosition(Config.SWARM_MATCH_MAP, fieldEvacuationCell));
            }
        }

        // 0.3) 폐쇄 조기 철수 (#226 F): 경고 구역에서는 꼬리 길이에 비례해 일찍 나간다.
        var closureSnapshot = _areaClosureManager.GetClientStateSnapshot(matchingId);
        if (closureSnapshot.WarningAreas.Contains(bot.CurrentArea))
        {
            int trailOrbCount = CountSwarmSquadOrbs(matchingId, botPlayerId);
            double evacuateLeadSeconds = SwarmBotClosureEvacuateBaseSeconds +
                                         trailOrbCount * SwarmBotClosureEvacuatePerOrbSeconds;
            if (closureSnapshot.WarningSeconds <= evacuateLeadSeconds)
            {
                // 대피는 도주 예외 — 왕복 억제를 우회해 어디로든 즉시 나간다.
                GetSwarmMatchRuntime(matchingId).BotTactics.FleeDirective.Add((matchingId, botPlayerId));
                // 목적지는 자기장 안쪽 대피 구역 (#272 수리): 전 구역 최근접 후보는 곧 경고가
                // 뜰 바깥 방을 고를 수 있다 — 원형 자기장에서 안전은 항상 안쪽이다.
                var (closureEvacuationArea, closureEvacuationCell) =
                    ResolveSwarmFieldEvacuationTarget(matchingId, bot.Position);
                return new SpotArenaBotDirective(
                    SpotArenaBotMode.Escort,
                    closureEvacuationArea,
                    closureEvacuationCell,
                    BotPlayerManager.CellToWorldPosition(Config.SWARM_MATCH_MAP, closureEvacuationCell));
            }
        }

        // 여기부터는 대피가 필요 없는 상태 — 위협이 있으면 원래 지시(Return)를 따른다.
        if (directive.Mode != SpotArenaBotMode.Escort)
            return directive;

        // 0.5) 상대 전력 비교 (#222): 티어 가중 전력(1/1.75/4)으로 비교한다.
        //      "싸움을 건다 = 유리하다" — 확실히 우세(×1.25 이상)일 때만 추격하고,
        //      동수 포함 그 이하는 회피한다. 동수 대치(뭉쳐서 수동 오브 소모전)가 성립하지
        //      않게 하는 규칙. 임계 사이 구간(1.0~1.25)은 중립 밴드 = 판단 떨림 방지.
        //      빈손은 화력이 0이라 몹도 강자로 취급해 피한다.
        float squadPower = GetSwarmSquadPower(matchingId, botPlayerId);
        bool hasSquadOrbs = squadPower > 0f;
        // 빈손 이속 (#223): 이동 배율이 읽는 플래그 — 판단 틱이 단일 갱신 지점이다.
        // 빈손으로 막 전이한 순간에만 가속 유예를 연다 (#229 12단계).
        if (!hasSquadOrbs && !bot.IsSwarmBareHanded)
            bot.SwarmBareSpeedUntilUtc =
                DateTime.UtcNow.AddSeconds(Config.SWARM_BARE_MOVE_SPEED_SECONDS);
        bot.IsSwarmBareHanded = !hasSquadOrbs;
        // 치명상 이탈 (2026-08-18 촬영 튜닝): 오염이 60%를 넘긴 봇은 전력 비교 없이 모든 상대를 강자로 보고
        // 물러나며(추격도 압박도 없음), 45% 아래로 회복해야 다시 싸운다. 도주 임계를 넓힌 뒤 봇 매치 9133549에서
        // 봇들이 죽을 때까지 맞붙어 2:44에 2마리만 남았다 — 다친 쪽이 등을 보이고 성한 쪽이 쫓는 그림이
        // 카메라에 남아야 하고, 후반까지 살아 있는 봇이 있어야 폐쇄 수렴전이 선다.
        bool wounded = UpdateSwarmBotWoundedState(matchingId, bot);
        FindNearbySwarmRivals(matchingId, bot, wounded ? 0f : squadPower,
            includeMonstersAsStronger: !hasSquadOrbs,
            out Vector3f? strongerPosition,
            out (Vector3f Position, AreaType Area, long PlayerId)? weakerRival);

        // 피격 반응 (#222, 매치 2379 -131 · 2386 -182): 맞는 동안은 절대 서 있지 않는다.
        // 열세·비등이면 그 방향에서 이탈(위협 승격), 우세면 싸우되 좌우 와리가리(스트레이프) —
        // 이동 중 공격이 허용되므로 화력 손실 없이 피격 정지 현상만 사라진다.
        bool recentlyDamaged =
            GetSwarmMatchRuntime(matchingId).BotTactics.LastDamagedAtUtc.TryGetValue((matchingId, botPlayerId), out var lastDamagedAtUtc) &&
            (DateTime.UtcNow - lastDamagedAtUtc).TotalSeconds <= SwarmBotDamagedFleeSeconds;
        Vector3f? recentAttackerPosition = null;
        if (recentlyDamaged && bot.LastProximityAttackerPlayerId != 0)
            TryGetSwarmParticipantPosition(
                matchingId, bot.LastProximityAttackerPlayerId, out recentAttackerPosition);
        if (strongerPosition == null && recentAttackerPosition != null)
        {
            float attackerPower = GetSwarmSquadPower(matchingId, bot.LastProximityAttackerPlayerId);
            // 확실한 강자(×1.5 이상)에게 맞았을 때만 이탈 — 동수·소폭 열세 공격자에게는 압박 전진한다
            // (2026-08-18 촬영 튜닝, SwarmBotFleePowerRatio 주석). 맞고 바로 등을 보이던 봇이 붙어 싸운다.
            // 치명상이면 상대 전력과 무관하게 이탈한다.
            if (wounded || attackerPower >= squadPower * SwarmBotFleePowerRatio)
            {
                strongerPosition = recentAttackerPosition;
            }
            else
            {
                // 우세 피격 반응 (#226 재수리): 수직 와리가리는 버킷을 늘려도 촐싹거렸다 —
                // 이긴다고 판단한 봇은 공격자를 향해 압박 전진한다 (이동 중 공격이라 화력 손실 없음).
                // 이미 붙어 있으면(1.5 이내) 지시 없이 통과 — 교전은 자동전투가 맡는다.
                float pressDx = recentAttackerPosition.X - bot.Position.X;
                float pressDy = recentAttackerPosition.Y - bot.Position.Y;
                if (pressDx * pressDx + pressDy * pressDy > 2.25f)
                {
                    Cell pressCell = ProximityCombatLineOfSight.WorldPositionToCell(
                        Config.SWARM_MATCH_MAP, recentAttackerPosition);
                    if (GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, pressCell) &&
                        GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, pressCell) is var pressArea &&
                        pressArea != AreaType.None)
                    {
                        return new SpotArenaBotDirective(
                            SpotArenaBotMode.Escort,
                            pressArea,
                            pressCell,
                            BotPlayerManager.CellToWorldPosition(Config.SWARM_MATCH_MAP, pressCell));
                    }
                }
            }
        }
        if (strongerPosition != null)
        {
            // 위협 앞에서는 채집 채널 홀드도 끊고 뛴다 — 홀드 채로 맞다 죽는 사고 방지 (매치 2372 봇 -78).
            GetSwarmMatchRuntime(matchingId).BotTactics.FleeDirective.Add((matchingId, botPlayerId));
            bot.CancelChannelHold();
            float fleeDx = bot.Position.X - strongerPosition.X;
            float fleeDy = bot.Position.Y - strongerPosition.Y;
            float fleeLength = MathF.Sqrt(fleeDx * fleeDx + fleeDy * fleeDy);
            if (fleeLength < 0.001f)
            {
                fleeDx = 1f;
                fleeDy = 0f;
                fleeLength = 1f;
            }

            // 도주 방향으로 앞선 가상 지점에서 최근접 스팟을 찾으면 "위협 반대편 스팟"이 된다.
            var fleeProbe = new Vector3f(
                bot.Position.X + fleeDx / fleeLength * SwarmBotFleeProbeDistance,
                bot.Position.Y + fleeDy / fleeLength * SwarmBotFleeProbeDistance,
                0f);
            if (TryFindNearestAvailableExploreSpot(
                    matchingId, area: null, fleeProbe, out var fleeSpot, out _))
            {
                var fleeArea = (AreaType)fleeSpot.ZoneId;
                Cell fleeCell = new(fleeSpot.CellX, fleeSpot.CellY);
                if (!GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, fleeCell))
                {
                    fleeCell = fleeCell.GetAdjacentCells().FirstOrDefault(cell =>
                        GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, cell) &&
                        GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, cell) == fleeArea) ?? fleeCell;
                }

                var fleeWorld = BotPlayerManager.CellToWorldPosition(Config.SWARM_MATCH_MAP, fleeCell);
                // 도주지가 제자리면 도주가 아니다 (#223 구석 정지 수리) — 다음 폴백으로 넘긴다.
                if (IsFarEnoughSwarmFleeTarget(bot, fleeWorld))
                    return new SpotArenaBotDirective(
                        SpotArenaBotMode.Escort, fleeArea, fleeCell, fleeWorld);
            }

            // 폴백 (#222): 도주 방향에 열린 스팟이 없어도 무조건 이탈한다 — 스팟 부재로
            // 지시 없이 낙하해 제자리에서 얻어맞던 구멍(매치 2372 봇 -78) 수리.
            Cell? fleeFallbackCell =
                ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, fleeProbe);
            if (!GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, fleeFallbackCell))
            {
                fleeFallbackCell = fleeFallbackCell.GetAdjacentCells()
                    .FirstOrDefault(cell => GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, cell));
            }

            if (fleeFallbackCell != null &&
                GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, fleeFallbackCell) is var fleeFallbackArea &&
                fleeFallbackArea != AreaType.None)
            {
                var fleeFallbackWorld = BotPlayerManager.CellToWorldPosition(Config.SWARM_MATCH_MAP, fleeFallbackCell);
                // 구석에서 벽에 막힌 probe는 제자리로 수렴한다 (#223) — 가까우면 구역 이탈로.
                if (IsFarEnoughSwarmFleeTarget(bot, fleeFallbackWorld))
                    return new SpotArenaBotDirective(
                        SpotArenaBotMode.Escort, fleeFallbackArea, fleeFallbackCell, fleeFallbackWorld);
            }

            // 벽 방향이거나 도주지가 제자리면 위협 반대편에서 가장 가까운 열린 사냥 구역
            // 스폰으로 물러난다 — 구역을 아예 벗어나야 진짜 도주다 (#223 구석 정지 수리).
            AreaType fleeRetreatArea = SwarmHuntingAreas
                .Where(area => !IsSwarmAreaOutside(matchingId, area))
                .OrderBy(area =>
                {
                    var center = BotPlayerManager.CellToWorldPosition(
                        Config.SWARM_MATCH_MAP, GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, area));
                    float dx = center.X - fleeProbe.X;
                    float dy = center.Y - fleeProbe.Y;
                    return dx * dx + dy * dy;
                })
                .DefaultIfEmpty(Config.SWARM_MATCH_GROUND_AREA)
                .First();
            Cell fleeRetreatCell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, fleeRetreatArea);
            return new SpotArenaBotDirective(
                SpotArenaBotMode.Escort,
                fleeRetreatArea,
                fleeRetreatCell,
                BotPlayerManager.CellToWorldPosition(Config.SWARM_MATCH_MAP, fleeRetreatCell));
        }

        // 절단 직후 회수 (#226 F): 방금 끊은 전리품부터 줍는다 — 추격은 그 다음이다.
        if (GetSwarmMatchRuntime(matchingId).BotTactics.LastTrailCutAtUtc.TryGetValue((matchingId, botPlayerId), out var lastCutAtUtc) &&
            (DateTime.UtcNow - lastCutAtUtc).TotalSeconds < SwarmBotPostCutLootSeconds &&
            TryFindNearestSwarmGroundStone(matchingId, bot, out Vector3f lootPosition))
        {
            return new SpotArenaBotDirective(
                SpotArenaBotMode.Escort,
                bot.CurrentArea,
                ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, lootPosition),
                lootPosition);
        }

        // 선두 점수 보존 (#226 F): 오브 선두는 약자 추격을 자제한다 — 이기고 있을 때
        // 싸움은 절단(상대의 유일한 역전 수단)에 점수를 노출하는 행동이다.
        if (hasSquadOrbs && weakerRival.HasValue &&
            !IsSwarmOrbLeader(matchingId, botPlayerId))
        {
            // 약자 추격은 본체가 아니라 오브열을 겨눈다 (#229 8단계). 코어 동사가 "몸으로 상대
            // 오브열을 자른다"인데 본체로 직진하면 꼬리를 지나칠 수 있다 — 봇 매치 9774851에서
            // 조우 29건에 절단 0건이었다. 꼬리 중간을 목표로 삼으면 접근 경로가 열을 가로지른다.
            var chaseTarget = ResolveSwarmTrailChasePoint(
                matchingId, weakerRival.Value.PlayerId, weakerRival.Value.Position);
            // 추격 계측 (#229 8단계): 조우는 나는데 절단이 0건인 원인을 가르려면 "추격이
            // 발동은 했는가"와 "발동하고도 못 잘랐는가"를 구분해야 한다. 매치 요약에 남긴다.
            LogSwarmChaseIssued(matchingId, botPlayerId, weakerRival.Value.PlayerId, chaseTarget);
            var chaseCell = ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, chaseTarget);
            var chaseArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, chaseCell);
            bool chaseCellUsable = chaseArea != AreaType.None &&
                                   GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, chaseCell);
            return new SpotArenaBotDirective(
                SpotArenaBotMode.Escort,
                chaseCellUsable ? chaseArea : weakerRival.Value.Area,
                chaseCellUsable
                    ? chaseCell
                    : ProximityCombatLineOfSight.WorldPositionToCell(
                        Config.SWARM_MATCH_MAP, weakerRival.Value.Position),
                chaseCellUsable ? chaseTarget : weakerRival.Value.Position);
        }

        // 1) 지갑이 차면 줍기보다 개봉이 먼저 — 열린 구역 중 가장 가까운 스팟으로 순례한다.
        //    줍기가 이 단계를 선점하면 봇이 수십 석을 들고도 개봉을 영영 미룬다 (매치 2221 계측).
        //    폐쇄 필터는 스팟 탐색 안에서 처리한다 — 최근접이 폐쇄라고 순례가 멈추면 안 된다.
        if (TryFindNearestAvailableExploreSpot(
                matchingId, area: null, bot.Position, out var spot, out _) &&
            _summonStoneManager.GetSnapshot(matchingId, botPlayerId).StoneCount >=
            // 비용은 봇 자신의 궤도 크기 기준 — spot.Id를 넘기던 오배선(빈 인벤=0비용) 수리
            GetSwarmBotExploreCost(matchingId, botPlayerId))
        {
            var spotArea = (AreaType)spot.ZoneId;
            Cell spotCell = new(spot.CellX, spot.CellY);
            if (!GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, spotCell))
            {
                spotCell = spotCell.GetAdjacentCells().FirstOrDefault(cell =>
                    GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, cell) &&
                    GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, cell) == spotArea) ?? spotCell;
            }

            return new SpotArenaBotDirective(
                SpotArenaBotMode.Escort,
                spotArea,
                spotCell,
                BotPlayerManager.CellToWorldPosition(Config.SWARM_MATCH_MAP, spotCell));
        }

        // 2) 같은 구역 바닥 소환석 — 걸어가면 자동 픽업 반경이 줍는다.
        if (TryFindNearestSwarmGroundStone(matchingId, bot, out Vector3f stonePosition))
        {
            return new SpotArenaBotDirective(
                SpotArenaBotMode.Escort,
                bot.CurrentArea,
                ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, stonePosition),
                stonePosition);
        }

        // 3) 사냥 정지: 도주·개봉·줍기 용무가 없고 사거리 안에 몹이 있으면 제자리에 선다.
        //    정지 공격 규칙에서 서야 쏘고, 캠프 모드에선 잠든 캠프 옆이 안전 사격 지점이다.
        //    빈손은 제외 — 화력 없이 몹 옆에 서는 건 자살이다 (#222).
        if (hasSquadOrbs && HasSwarmMonsterInBasicRange(matchingId, bot))
        {
            return new SpotArenaBotDirective(
                SpotArenaBotMode.Escort,
                bot.CurrentArea,
                ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, bot.Position),
                bot.Position);
        }

        // 3.5) 소환석 기근 (#219): 다음 개봉 비용이 부족하면 사냥을 나간다.
        //      지역 공급 (#226 단계 B): 몹이 남은 가장 가까운 공급 무리로 향한다 — 몹은
        //      찾아가는 공유 자원이고, 미니맵 스냅샷으로 사람에게도 같은 정보가 보인다.
        //      빈손 봇은 개봉이 무료라 1)에서 이미 스팟 순례로 빠진다.
        if (_summonStoneManager.GetSnapshot(matchingId, botPlayerId).StoneCount <
            GetSwarmBotExploreCost(matchingId, botPlayerId) &&
            hasSquadOrbs)
        {
            if (SwarmMonsterDirector.RegionSupplyModeEnabled &&
                TryFindNearestSwarmSupplyMonster(matchingId, bot, out var supplyArea,
                    out var supplyPosition))
            {
                return new SpotArenaBotDirective(
                    SpotArenaBotMode.Escort,
                    supplyArea,
                    ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, supplyPosition),
                    supplyPosition);
            }

            // 캠프 모드 폴백: 봇은 캠프 '위치'만 알고(지도 지식) 생사는 모른다.
            if (!SwarmMonsterDirector.RegionSupplyModeEnabled &&
                TryChooseSwarmBotCampTarget(matchingId, bot, out var campArea, out var campPosition))
            {
                return new SpotArenaBotDirective(
                    SpotArenaBotMode.Escort,
                    campArea,
                    ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, campPosition),
                    campPosition);
            }
        }

        // 4) 마른 방 탈출: 시작방·복도(또는 몹이 마른 지역 공급 구역)에서 사냥터로 이주한다.
        if (SwarmMonsterDirector.RegionSupplyModeEnabled)
        {
            // 지역 공급: 현재 구역에 살아있는 몹도, 열 수 있는 스팟 용무도 없으면
            // 몹이 남은 공급 구역으로 이주 — 스폰이 멈춘 종반에는 지시 없이 배회(디렉터 몫).
            bool currentAreaHasSupply = _swarmMonsterDirector.GetVisualStates(matchingId)
                .Any(monster => monster.IsAlive && monster.AreaType == bot.CurrentArea);
            if (!currentAreaHasSupply &&
                TryFindNearestSwarmSupplyMonster(matchingId, bot, out var migrateArea,
                    out var migratePosition))
            {
                return new SpotArenaBotDirective(
                    SpotArenaBotMode.Escort,
                    migrateArea,
                    ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, migratePosition),
                    migratePosition);
            }

            return directive;
        }

        if (MatchSpawnData.GetPhaseRoomCandidates().Contains(bot.CurrentArea))
        {
            // 경계 밖 사냥터는 제외 — 전부 밖이면 종착지 운동장으로 (운동장은 항상 안이다).
            AreaType huntingArea = SwarmHuntingAreas
                .Where(area => !IsSwarmAreaOutside(matchingId, area))
                .OrderBy(area =>
                {
                    var center = BotPlayerManager.CellToWorldPosition(
                        Config.SWARM_MATCH_MAP, GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, area));
                    float dx = center.X - bot.Position.X;
                    float dy = center.Y - bot.Position.Y;
                    return dx * dx + dy * dy;
                })
                .DefaultIfEmpty(Config.SWARM_MATCH_GROUND_AREA)
                .First();
            Cell huntingCell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, huntingArea);
            return new SpotArenaBotDirective(
                SpotArenaBotMode.Escort,
                huntingArea,
                huntingCell,
                BotPlayerManager.CellToWorldPosition(Config.SWARM_MATCH_MAP, huntingCell));
        }

        return directive;
    }

    /// <summary>
    ///     지역 공급 사냥 목적지 (#226 단계 B): 폐쇄·경계 밖을 제외하고 살아있는 공급 몹 중
    ///     가장 가까운 개체의 위치. 봇의 파밍 이동은 항상 Escort 모드로 나가야 개봉 채널
    ///     완료 로직이 산다 (Return 단락 사고 2026-08-12).
    /// </summary>
    private bool TryFindNearestSwarmSupplyMonster(
        long matchingId, BotPlayerState bot, out AreaType area, out Vector3f position)
    {
        area = AreaType.None;
        position = null!;
        float bestSquared = float.MaxValue;
        foreach (var monster in _swarmMonsterDirector.GetVisualStates(matchingId))
        {
            if (!monster.IsAlive || IsSwarmAreaOutside(matchingId, monster.AreaType))
                continue;
            float dx = monster.PositionX - bot.Position.X;
            float dy = monster.PositionY - bot.Position.Y;
            float distanceSquared = dx * dx + dy * dy;
            if (distanceSquared >= bestSquared)
                continue;
            bestSquared = distanceSquared;
            area = monster.AreaType;
            position = new Vector3f(monster.PositionX, monster.PositionY, 0f);
        }

        return position != null;
    }

    // 라이벌 스캔 반경: 이 안의 참가자와 전력을 비교해 회피/추격을 정한다.
    // 최대 공격 사거리(기본 7 + 파도 가산 3)보다 넓어야 한다 — 6이던 시절, 파도 빌드가
    // 봇의 인지 밖(6~10)에서 일방적으로 쏘는 사각이 있었다 (매치 2376 봇 -108).
    private const float SwarmBotRivalScanRadius = 11f;

    // 도주 방향 앞의 가상 지점 — 이 지점 기준 최근접 스팟이 "위협 반대편 재기 스팟"이 된다.
    private const float SwarmBotFleeProbeDistance = 8f;

    // 도주지 최소 거리 (#223 구석 정지 수리): 구석에서 벽에 막힌 도주지는 제자리로
    // 수렴한다 — 이보다 가까우면 도주가 아니므로 다음 폴백(구역 이탈)으로 넘긴다.
    private const float SwarmBotMinFleeTargetDistance = 3f;

    private static bool IsFarEnoughSwarmFleeTarget(BotPlayerState bot, Vector3f target)
    {
        float dx = target.X - bot.Position.X;
        float dy = target.Y - bot.Position.Y;
        return dx * dx + dy * dy >=
               SwarmBotMinFleeTargetDistance * SwarmBotMinFleeTargetDistance;
    }

    /// <summary>이 봇의 스쿼드 오브 총 개수 — 저성장(파밍 부족) 판정용.</summary>

    // 추격 우위 임계: 내 전력이 상대의 이 배수 이상일 때만 붙는다.
    private const float SwarmBotChasePowerAdvantage = 1.25f;

    // 도주 임계 (2026-08-18 촬영 튜닝): 상대 전력이 내 전력의 이 배수 이상일 때만 피한다. 그 사이(동수·소폭
    // 열세, 1/1.5 ~ 1.25배)는 중립 — 피하지도 붙지도 않고 하던 일을 한다.
    // 동수 도주(#222 "동수는 강자 취급")는 오브 HP 소모전 시절 "뭉쳐서 대치"를 막던 규칙인데, 오브 손실이 절단
    // 전용이 된 뒤로는 동수 대치가 서로의 꼬리 주위를 도는 코어 동사다. 사람 카메라 앞에서 봇이 늘 등을 보이며
    // 흩어지던 원인이라 넓힌다. 빈손(전력 0)은 여전히 모두를 강자로 본다.
    private const float SwarmBotFleePowerRatio = 1.5f;

    // 치명상 이탈 (2026-08-18 촬영 튜닝): 오염이 최대의 60%(252)에 닿으면 치명상, 45%(189) 아래로 내려와야
    // 해제 — 회복 1틱에 상태가 뒤집혀 "도망↔복귀"가 떨리지 않게 히스테리시스를 둔다.
    private const float SwarmBotWoundedEnterRatio = 0.6f;
    private const float SwarmBotWoundedExitRatio = 0.45f;

    /// <summary>봇의 치명상 상태를 갱신하고 돌려준다 — 진입 60%, 해제 45%.</summary>
    private bool UpdateSwarmBotWoundedState(long matchingId, BotPlayerState bot)
    {
        var key = (matchingId, bot.PlayerId);
        bool wounded = GetSwarmMatchRuntime(matchingId).BotTactics.Wounded.Contains(key);
        float ratio = bot.Corruption / (float)Config.MAX_CORRUPTION;
        if (!wounded && ratio >= SwarmBotWoundedEnterRatio)
        {
            GetSwarmMatchRuntime(matchingId).BotTactics.Wounded.Add(key);
            return true;
        }

        if (wounded && ratio <= SwarmBotWoundedExitRatio)
        {
            GetSwarmMatchRuntime(matchingId).BotTactics.Wounded.Remove(key);
            return false;
        }

        return wounded;
    }

    // 피격 반응 창: 이 시간 안에 맞았으면 중립 밴드 상대도 위협으로 승격한다.
    // 3초는 공격 간헐(조준·쿨다운·재접근)에 못 미쳐 와리가리↔정지가 번갈아 보였다 — 6초로.
    private const double SwarmBotDamagedFleeSeconds = 6d;

    // 피격 중 와리가리: 공격자 방향의 수직으로 이만큼 이동, 1초마다 좌우 반전.
    // 스트레이프(수직 와리가리)는 퇴역 (#226): 1초 반전은 좌우 연타, 3초 버킷도 촐싹거림 —
    // 우세 피격 반응은 압박 전진(ChooseSwarmBotDirective)으로 대체됐다.

    // 봇 절단 자제 (2026-08-18 촬영 튜닝): 절단 뒤 오염이 이 비율(최대 420의 절반 = 210)을 넘으면 봇은 자르지
    // 않고, 자른 뒤 이 시간 동안은 다시 자르지 않는다. 사람에게는 적용하지 않는다.
    private const float SwarmBotCutMaxCorruptionRatio = 0.5f;
    private const double SwarmBotCutCooldownSeconds = 6d;

    /// <summary>
    ///     봇이 지금 절단을 질러도 되는가 — 비용을 내고도 오염 절반 아래이고, 직전 절단에서 쿨다운이 지났는가.
    ///     사람 판정이 아니다: 사람의 절단은 만충 탈락만 아니면 언제나 성립한다.
    /// </summary>
    private bool IsSwarmBotCutAllowed(long matchingId, long botPlayerId, int corruptionBefore, DateTime nowUtc)
    {
        if (corruptionBefore + SwarmSingleCutCorruptionCost >
            Config.MAX_CORRUPTION * SwarmBotCutMaxCorruptionRatio)
            return false;
        return !GetSwarmMatchRuntime(matchingId).BotTactics.LastTrailCutAtUtc.TryGetValue((matchingId, botPlayerId), out var lastCutAtUtc) ||
               (nowUtc - lastCutAtUtc).TotalSeconds >= SwarmBotCutCooldownSeconds;
    }

    /// <summary>
    ///     추격 조준점 (#229 8단계): 상대 오브열의 중간 지점. 열이 없으면 본체를 그대로 돌려준다.
    ///     본체를 겨누면 꼬리를 지나치지 않고 옆으로 붙어 서기만 한다 — 절단이 성립하지 않는다.
    /// </summary>
    private Vector3f ResolveSwarmTrailChasePoint(long matchingId, long targetPlayerId, Vector3f targetPosition)
    {
        int orbCount = CountSwarmSquadOrbs(matchingId, targetPlayerId);
        if (orbCount <= 0)
            return targetPosition;

        // 중간 순번을 노린다 — 꼬리 끝은 손실이 적고, 머리 바로 뒤는 도달 전에 흔들린다.
        int aimOrdinal = Math.Max(1, orbCount / 2);
        return GetSwarmOrbTrailPosition(matchingId, targetPlayerId, aimOrdinal, targetPosition)
               ?? targetPosition;
    }

    private void FindNearbySwarmRivals(
        long matchingId,
        BotPlayerState bot,
        float myPower,
        bool includeMonstersAsStronger,
        out Vector3f? strongerPosition,
        out (Vector3f Position, AreaType Area, long PlayerId)? weakerRival)
    {
        float radiusSquared = SwarmBotRivalScanRadius * SwarmBotRivalScanRadius;
        float bestStrongerDistanceSquared = radiusSquared;
        float bestWeakerDistanceSquared = radiusSquared;
        Vector3f? nearestStronger = null;
        (Vector3f Position, AreaType Area, long PlayerId)? nearestWeaker = null;

        void Consider(long rivalPlayerId, Vector3f position, AreaType area)
        {
            float dx = position.X - bot.Position.X;
            float dy = position.Y - bot.Position.Y;
            float distanceSquared = dx * dx + dy * dy;
            if (distanceSquared >= radiusSquared) return;

            float rivalPower = GetSwarmSquadPower(matchingId, rivalPlayerId);
            // 확실한 강자(×1.5 이상)만 피한다 — 동수·소폭 열세는 중립 (SwarmBotFleePowerRatio 주석).
            // 빈손(myPower 0)은 전력 있는 모두가 강자다.
            if (rivalPower >= myPower * SwarmBotFleePowerRatio && distanceSquared < bestStrongerDistanceSquared)
            {
                bestStrongerDistanceSquared = distanceSquared;
                nearestStronger = position;
            }
            else if (myPower >= rivalPower * SwarmBotChasePowerAdvantage &&
                     distanceSquared < bestWeakerDistanceSquared &&
                     !IsSwarmAreaOutside(matchingId, area))
            {
                bestWeakerDistanceSquared = distanceSquared;
                nearestWeaker = (position, area, rivalPlayerId);
            }
        }

        foreach (var other in _botPlayerManager.GetBots(matchingId))
        {
            if (other.PlayerId == bot.PlayerId || other.IsEliminated) continue;
            Consider(other.PlayerId, other.Position, other.CurrentArea);
        }

        foreach (var session in GetSessionsByInstance(Config.SWARM_MATCH_MAP, matchingId))
        {
            if (!session.PlayerId.HasValue || session.IsEliminated ||
                session.LastValidatedPosition == null)
                continue;
            Consider(session.PlayerId.Value, session.LastValidatedPosition, session.CurrentArea);
        }

        if (includeMonstersAsStronger)
        {
            foreach (var monster in _swarmMonsterDirector.GetVisualStates(matchingId))
            {
                if (!monster.IsAlive) continue;
                float dx = monster.PositionX - bot.Position.X;
                float dy = monster.PositionY - bot.Position.Y;
                float distanceSquared = dx * dx + dy * dy;
                if (distanceSquared >= bestStrongerDistanceSquared) continue;
                bestStrongerDistanceSquared = distanceSquared;
                nearestStronger = new Vector3f(monster.PositionX, monster.PositionY, 0f);
            }
        }

        strongerPosition = nearestStronger;
        weakerRival = nearestWeaker;
    }

    // 빈 캠프 재방문 제외 시간 — 캠프 리스폰(45초)보다 짧게 잡아 순회가 한 바퀴 돌면 돌아온다.
    private const double SwarmBotEmptyCampSkipSeconds = 30d;

    // 캠프 혼잡 판정 반경과 초과 인원당 실효 거리 배율 (#222 봇 뭉침 해소).
    private const float SwarmBotCampCrowdRadius = 7f;
    private const float SwarmBotCampCrowdPenaltyPerBot = 1.5f;

    // 캠프 생사 판정 반경 — 리쉬(5.5) 안에 살아있는 몹이 없으면 그 캠프는 비어 있는 것이다.
    private const float SwarmBotCampAliveCheckRange = 5.5f;


    /// <summary>
    ///     봇의 캠프 순례 목적지 — 정적 앵커(지도 지식)에서 가까운 순으로 고른다. 같은 구역
    ///     캠프는 시야로 생사를 확인할 수 있고, 비어 있으면 스킵 표시 후 다음 후보로 넘어간다.
    ///     다른 구역 캠프는 생사를 모르니 일단 걸어간다 — 도착 후 다음 틱에 같은 규칙으로 판정된다.
    /// </summary>
    private bool TryChooseSwarmBotCampTarget(
        long matchingId, BotPlayerState bot, out AreaType campArea, out Vector3f campPosition)
    {
        DateTime nowUtc = DateTime.UtcNow;
        var visibleAliveMonsters = _swarmMonsterDirector.GetVisualStates(matchingId)
            .Where(monster => monster.IsAlive && monster.AreaType == bot.CurrentArea)
            .ToList();

        // 혼잡 페널티 (#222): 이미 다른 봇이 몰린 캠프는 실효 거리를 늘려 순위를 낮춘다.
        // 1명까지는 경쟁 허용(선점 다툼도 재미), 2명째부터 뭉침으로 보고 흩어지게 한다.
        var otherBotPositions = _botPlayerManager.GetBots(matchingId)
            .Where(other => other.PlayerId != bot.PlayerId && !other.IsEliminated)
            .Select(other => other.Position)
            .ToList();

        var anchors = GameMonsterCampData.GetAllAnchors()
            .Where(anchor => !IsSwarmAreaOutside(matchingId, anchor.Area))
            .Select(anchor => (anchor.Area, anchor.CampIndex,
                World: BotPlayerManager.CellToWorldPosition(Config.SWARM_MATCH_MAP, anchor.Cell)))
            .OrderBy(anchor =>
            {
                float dx = anchor.World.X - bot.Position.X;
                float dy = anchor.World.Y - bot.Position.Y;
                int nearbyBots = otherBotPositions.Count(position =>
                {
                    float bx = position.X - anchor.World.X;
                    float by = position.Y - anchor.World.Y;
                    return bx * bx + by * by <=
                           SwarmBotCampCrowdRadius * SwarmBotCampCrowdRadius;
                });
                return (dx * dx + dy * dy) *
                       (1f + SwarmBotCampCrowdPenaltyPerBot * Math.Max(0, nearbyBots - 1));
            });

        foreach (var anchor in anchors)
        {
            var skipKey = (matchingId, bot.PlayerId, anchor.Area, anchor.CampIndex);
            if (GetSwarmMatchRuntime(matchingId).BotTactics.CampSkipUntilUtc.TryGetValue(skipKey, out var skipUntil) && nowUtc < skipUntil)
                continue;

            if (anchor.Area == bot.CurrentArea)
            {
                bool campAlive = visibleAliveMonsters.Any(monster =>
                {
                    float dx = monster.PositionX - anchor.World.X;
                    float dy = monster.PositionY - anchor.World.Y;
                    return dx * dx + dy * dy <=
                           SwarmBotCampAliveCheckRange * SwarmBotCampAliveCheckRange;
                });
                if (!campAlive)
                {
                    GetSwarmMatchRuntime(matchingId).BotTactics.CampSkipUntilUtc[skipKey] = nowUtc.AddSeconds(SwarmBotEmptyCampSkipSeconds);
                    continue;
                }
            }

            campArea = anchor.Area;
            campPosition = anchor.World;
            return true;
        }

        campArea = AreaType.None;
        campPosition = bot.Position;
        return false;
    }

    private bool HasSwarmMonsterInBasicRange(long matchingId, BotPlayerState bot)
    {
        float rangeSquared = SwarmArenaBasicRange * SwarmArenaBasicRange;
        foreach (var target in _swarmMonsterDirector.GetCombatTargets(matchingId))
        {
            if (target.Area != bot.CurrentArea)
                continue;
            float dx = target.Position.X - bot.Position.X;
            float dy = target.Position.Y - bot.Position.Y;
            if (dx * dx + dy * dy <= rangeSquared)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     봇 개봉 문턱 (#226 단계 C): 실지불은 상자 고정가(1)지만, 성장 카드 비용을 지키고도
    ///     남는 여유가 있을 때만 상자로 향한다 — 석을 하트에 다 태워 투자를 굶는 사고 방지.
    /// </summary>
    private int GetSwarmBotExploreCost(long matchingId, long botPlayerId) =>
        GetSwarmGrowthCostBreakdown(matchingId, botPlayerId).FinalCost + Config.SWARM_BOX_OPEN_COST;


    /// <summary>
    ///     봇 투자 정책 (#226 F 상황 판단): 큰 점수 열세(선두와 3+ 격차)는 생성 몰빵,
    ///     선두는 방어(절단 = 상대의 유일한 역전 수단), 처치각(같은 구역 약자)은 공격 강화,
    ///     그 외 기존 45/30/25. 무효 카드는 생성으로 대체. 초반(오브 5 미만)은 생성 고정 —
    ///     발사점·점수가 곧 생존이다.
    /// </summary>
    private int ChooseSwarmBotGrowthCard(
        long matchingId, BotPlayerState bot, SwarmGrowthOfferState offer, int orbCount,
        List<GameClientSession> aliveSessions, List<BotPlayerState> aliveBots)
    {
        if (orbCount < 5)
            return SwarmGrowthCardMultiply;

        int topOrbCount = GetSwarmTopOrbCount(matchingId);
        bool isLeader = orbCount >= topOrbCount;
        int leaderGap = topOrbCount - orbCount;
        bool hasPrey = HasSwarmPreyInArea(matchingId, bot, aliveSessions, aliveBots);

        var (multiply, enhance) = leaderGap >= 3 ? (70, 20) :
            isLeader ? (35, 20) :
            hasPrey ? (35, 45) : (45, 30);

        int roll = Random.Shared.Next(100);
        if (roll < multiply)
            return SwarmGrowthCardMultiply;
        if (roll < multiply + enhance)
            return offer.EnhanceTargetTier > 0 ? SwarmGrowthCardEnhance : SwarmGrowthCardMultiply;
        return offer.ArmorCount > 0 ? SwarmGrowthCardArmor : SwarmGrowthCardMultiply;
    }

    /// <summary>
    ///     비접촉 유예를 넘긴 봇의 오염을 1초 단위로 회복한다. 피격이 들어오면
    ///     유예가 리셋되므로, 스웜에 물려 있는 동안에는 회복되지 않는다.
    /// </summary>
    private void ProcessSwarmBotRecovery(long matchingId, List<BotPlayerState> aliveBots, DateTime nowUtc)
    {
        foreach (var bot in aliveBots)
        {
            if (bot.Corruption <= 0)
                continue;

            var key = (matchingId, bot.PlayerId);
            if (GetSwarmMatchRuntime(matchingId).BotTactics.LastDamagedAtUtc.TryGetValue(key, out var lastDamagedAtUtc) &&
                (nowUtc - lastDamagedAtUtc).TotalSeconds < SwarmBotRecoveryGraceSeconds)
                continue;
            if (GetSwarmMatchRuntime(matchingId).BotTactics.NextRecoveryAtUtc.TryGetValue(key, out var nextRecoveryAtUtc) &&
                nowUtc < nextRecoveryAtUtc)
                continue;

            GetSwarmMatchRuntime(matchingId).BotTactics.NextRecoveryAtUtc[key] = nowUtc.AddSeconds(1d);
            bot.Corruption = Math.Max(0, bot.Corruption - SwarmBotRecoveryPerSecond);
        }
    }
}
