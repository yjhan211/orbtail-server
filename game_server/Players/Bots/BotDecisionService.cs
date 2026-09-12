using game_server.matches;
using game_server.matches.combat;
using game_server.matches.logging;
using game_server.players;
using game_server.sessions;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;
using network.packets;

namespace game_server.players.bots;

/// <summary>
///     봇의 대피·추격·아이템 회수·문 열기·회복·오브 성장과 절단 가능 여부를 판단한다.
///     기억과 재사용 대기 시간은 매치가 소유하며 호출자는 매치 잠금을 보유한다.
///     이동 지시의 실제 실행은 BotMovementService가 맡는다.
/// </summary>
internal sealed class BotDecisionService(
    GameEventLogManager eventLogs,
    PlayerOrbGrowthService growth,
    PlayerOrbTrailService orbTrails,
    PlayerInteractionService interactions,
    ILogger<BotDecisionService> logger)
{
    private bool TryUpgradeForBot(MatchRuntime runtime, long playerId)
    {
        var player = runtime.GetParticipant(playerId)!;
        var orbGroupIds = player.Orbs.GetOrderedOrbs()
            .Select(item => OrbData.TryGetOrbGroupAndTier(item.ItemId, out int orbGroupId, out _)
                ? orbGroupId
                : 0)
            .Where(orbGroupId => orbGroupId != 0)
            .ToList();

        int preferredGroupId = orbGroupIds
            .GroupBy(orbGroupId => orbGroupId)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstOrDefault();
        if (preferredGroupId == 0)
        {
            return false;
        }

        if (growth.GetUpgradeCost(runtime, player, preferredGroupId) <= 0)
        {
            preferredGroupId = orbGroupIds.Distinct().FirstOrDefault(orbGroupId => growth.GetUpgradeCost(runtime, player, orbGroupId) > 0);
            if (preferredGroupId == 0)
            {
                return false;
            }
        }

        if (!OrbData.TryGetOrbItemId(preferredGroupId, 1, out int targetItemId))
        {
            return false;
        }
        return growth.UpgradeOrb(runtime, player, Config.ORB_UPGRADE_GROUP, targetItemId).Success;
    }

    public void ProcessBotOrbGrowth(MatchRuntime runtime, IReadOnlyList<BotPlayerState> aliveBots)
    {
        foreach (var bot in aliveBots)
        {
            if (bot.Player.IsEliminated)
            {
                continue;
            }

            if (bot.Player.SummonStones.StoneCount < growth.GetNextOrbGrowthCost(runtime, bot.Player))
            {
                continue;
            }
            int orbCount = bot.Player.Orbs.GetOrbScore().OrbCount;
            bool preferUpgrade = orbCount >= Config.SWARM_ORB_CAPACITY || (orbCount >= 4 && Random.Shared.Next(3) == 0);
            if (preferUpgrade && TryUpgradeForBot(runtime, bot.PlayerId))
            {
                continue;
            }

            if (orbCount < Config.SWARM_ORB_CAPACITY && growth.Summon(runtime, bot.Player).Success)
            {
                continue;
            }
            TryUpgradeForBot(runtime, bot.PlayerId);
        }
    }

    // 봇은 매치 참가자다. 각 처리 단계의 호출 순서는 MatchCombatService가 정한다.

    // 봇 문 잠금해제 (#229). 사람과 같은 규칙을 봇에도 건다 — 봇만 잠긴 문을 통과하면
    // 폐쇄 압력이 봇에게만 무의미해지고, 봇 매치로 이 메카닉을 검증할 수도 없다.
    // #272: 채널 길이는 사람 게이지와 같은 Config 문 등급 값을 쓴다 (합류 문 = 듀얼 관문 12초).
    private const float SwarmBotDoorUnlockRange = 1.6f;

    public void ProcessSwarmBotDoorUnlocks(
        MatchRuntime runtime,
        List<BotPlayerState> bots,
        List<GameClientSession> sessions,
        DateTime nowUtc)
    {
        long now = nowUtc.Ticks / TimeSpan.TicksPerMillisecond;
        foreach (var bot in bots)
        {
            var player = bot.Player;
            if (runtime.IsEnded || player.IsEliminated || player.IsSleeping) continue;
            // 봇은 패킷 대신 틱에서 완료를 요청한다. 진행 시간과 완료 검증은 사람과 같다.
            if (player.PendingDoorInteractionId is { } doorId)
            {
                if (!interactions.TryFinishDoor(runtime, player, doorId, doorId, now, out _)) continue;
                using var openPacket = PacketMaker.G_TO_C_DOOR_STATE_UPDATE(doorId, true, ErrorCode.SUCCESS, bot.PlayerId);
                foreach (var session in sessions) session.TrySend(openPacket);
                logger.LogInformation("Swarm bot unlocked door: MatchingId={MatchingId}, BotId={BotId}, DoorId={DoorId}",
                    runtime.MatchingId, bot.PlayerId, doorId);
                continue;
            }
            if (!TryFindNearestLockedGaugeDoor(runtime, bot, out int targetDoorId)) continue;
            // 봇은 클라이언트 상호작용 ID가 없어 문 ID를 진행 식별자로 사용한다.
            interactions.StartDoor(runtime, player, targetDoorId, targetDoorId, now);
        }
    }

    private bool TryFindNearestLockedGaugeDoor(MatchRuntime runtime, BotPlayerState bot, out int doorId)
    {
        doorId = 0;
        float best = float.MaxValue;
        // 폐쇄 문 규칙은 사람과 같다: 밖에서 폐쇄 구역으로 들어가는 문은 못 따고,
        // 내가 폐쇄 구역 안이면 어느 문이든 따서 나간다.
        bool insideClosed = runtime.Closures.IsAreaClosed(bot.Player.CurrentArea);
        foreach (var door in GameDoorData.GetByAreaType(bot.Player.CurrentArea))
        {
            if (!GameInteractableData.IsGaugeGatedDoor(door.DoorId)) continue;
            if (runtime?.Doors.IsDoorOpen(door.DoorId) == true) continue;
            // 단방향 문("봇이 바깥에서 문을 따고 들어온다" 제보): 게이지가
            // 놓인 쪽(안쪽)에서만 딴다 — 사람은 게이지 노출 규칙이 이미 막고 있고, 봇도 같은
            // 표를 따른다. 폐쇄 구역 탈출은 예외 (사람 규칙과 동일).
            if (!insideClosed &&
                !GameInteractableData.IsGaugeDoorOperableFrom(door.DoorId, (int)bot.Player.CurrentArea))
                continue;
            if (!insideClosed &&
                (runtime.Closures.IsAreaClosed(door.AreaType) ||
                 runtime.Closures.IsAreaClosed(door.AreaTypeB)))
                continue;

            // door_info의 좌표는 셀 단위다 — 봇 위치(월드)와 직접 비교하면 절대 닿지 않는다.
            var doorWorld = BotPlayerManager.CellToWorldPosition(
                Config.SWARM_MATCH_MAP, new Cell((int)door.PositionX, (int)door.PositionY));
            float dx = doorWorld.X - bot.Player.Position!.X;
            float dy = doorWorld.Y - bot.Player.Position!.Y;
            float distanceSquared = dx * dx + dy * dy;
            if (distanceSquared > SwarmBotDoorUnlockRange * SwarmBotDoorUnlockRange) continue;
            if (distanceSquared >= best) continue;

            best = distanceSquared;
            doorId = door.DoorId;
        }

        return doorId > 0;
    }

    // 시작방 팩이 마르면 봇이 이주할 무한 스폰 사냥터.
    // #272 School2: 합류 구역 4곳 + 운동장 — 순례 목적지가 곧 수렴 동선이다.
    private static readonly AreaType[] SwarmHuntingAreas =
    [
        Config.SWARM_MATCH_GROUND_AREA, AreaType.S2Library1, AreaType.S2Library2,
        AreaType.S2Gym1, AreaType.S2Gym2
    ];

    /// <summary>
    ///     봇 이동 지시 라우팅: 도주(생존) > 바닥 소환석 줍기 >
    ///     마른 방 탈출(사냥터 이주) > 스웜 디렉터 배회.
    /// </summary>
    // 왕복 억제 (#226 F): 방금 떠난 구역으로 수 초 내 복귀하는 지시는 판단 떨림이다 —
    // 봇 매치 계측에서 2초 내 직전 구역 복귀가 112회 나왔다. 피격 도주·경계 대피는 예외.
    private const double SwarmBotAreaReturnCooldownSeconds = 5d;

    private const double SwarmBotPostCutLootSeconds = 5d;

    // 폐쇄 조기 철수 (#226 F): 경고 잔여가 (기본 + 오브당 가산) 이하로 내려오면 나간다 —
    // 긴 꼬리는 문 통과가 느리고, 폐쇄 잔류 꼬리는 무보상 파괴된다.

    // 자기장 대피 여유 (셀, #272): 경계에 이만큼 다가서면 미리 물러나고, 두 배 안쪽까지 들어간다.
    // 5셀 = 약 18초 여유. School2는 경계가 초당 약 0.28셀로 조여 3셀이면 11초뿐이라 합류에서 통로로 이송하는
    // 중에 오염사한다. 재발동 간격도 같은 비율이라 와리가리하지 않는다.
    private const int SwarmBotFieldEvacuateMarginCells = 5;

    // 방 마감 선제 탈출 리드 (초): 문 잠금 전에 방을 비우는 여유 — 큰 방 횡단 + 문 경유 시간.
    private const double SwarmBotAreaExitLeadSeconds = 25d;

    /// <summary>
    ///     자기장 안쪽 대피 목적지 (#272, 매치 3030 실측 수리): 옛 후보(SwarmHuntingAreas =
    ///     외곽 사냥방)는 원형 자기장에서 다음 희생양이라, 대피한 봇들이 바깥 방으로 몰려가
    ///     절반이 자기장에 죽었다. 여유(6셀)까지 안전한 셀을 가진 구역 중 안쪽 셀이 봇에서
    ///     가장 가까운 곳으로 보낸다 — 목적지 셀은 그 구역의 가장 안쪽 셀이다.
    /// </summary>
    private (AreaType Area, Cell Cell) ResolveSwarmFieldEvacuationTarget(MatchRuntime runtime, Vector3f botPosition)
    {
        double safeDistance = runtime.Closures.GetSafeDistance(DateTime.UtcNow);
        AreaType bestArea = Config.SWARM_MATCH_GROUND_AREA;
        Cell bestCell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, Config.SWARM_MATCH_GROUND_AREA);
        float bestSq = float.MaxValue;
        foreach (var region in GameMapData.GetAreas(Config.SWARM_MATCH_MAP))
        {
            var area = region.AreaType;
            if (area == AreaType.None) continue;
            var cells = SwarmPressureField.GetAreaCellsByDistance(area);
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

    public SwarmBotDirective DecideMovement(MatchRuntime runtime, long botPlayerId)
    {
        var directive = DecideMovementCore(runtime, botPlayerId);
        var bot = runtime.Bots.GetBots()
            .FirstOrDefault(candidate => candidate.PlayerId == botPlayerId);
        if (bot == null || bot.Player.IsEliminated || bot.Player.CurrentArea == AreaType.None)
            return directive;

        if (bot.AreaMemory is not { } memory)
        {
            bot.AreaMemory = (bot.Player.CurrentArea, AreaType.None, DateTime.MinValue);
            return directive;
        }

        if (memory.Area != bot.Player.CurrentArea)
        {
            memory = (bot.Player.CurrentArea, memory.Area, DateTime.UtcNow);
            bot.AreaMemory = memory;
        }

        if (directive.Mode != SwarmBotMode.Escort)
            return directive;

        // 도주·대피 지시는 어디로든 즉시 — 억제는 경제·추격 지시의 판단 떨림에만 건다.
        if (bot.FleeDirective)
            return directive;

        if (directive.DestinationArea == memory.PreviousArea &&
            directive.DestinationArea != bot.Player.CurrentArea &&
            (DateTime.UtcNow - memory.LeftAtUtc).TotalSeconds < SwarmBotAreaReturnCooldownSeconds)
        {
            // 복귀 지시 강등: 쿨다운 동안 현 구역 제자리 — 자동 전투·픽업은 계속 돈다.
            return new SwarmBotDirective(
                SwarmBotMode.Escort,
                bot.Player.CurrentArea,
                ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, bot.Player.Position!),
                bot.Player.Position!);
        }

        return directive;
    }

    private SwarmBotDirective DecideMovementCore(MatchRuntime runtime, long botPlayerId)
    {
        var directive = runtime.Monsters.GetBotDirective(botPlayerId);

        var bot = runtime.Bots.GetBots()
            .FirstOrDefault(candidate => candidate.PlayerId == botPlayerId);
        if (bot == null || bot.Player.IsEliminated)
            return directive;
        bot.FleeDirective = false;

        // 폐쇄·경계 탈출은 위협 판정보다 위다 (#229 8단계). GetBotDirective는 내 구역에 깨어난
        // 몹이 하나라도 있으면 Return을 준다. 밀도 램프 이후 구역당 7~15마리라 이 조건이 상시
        // 참이 됐고, 아래의 대피·전력 비교·추격이 통째로 죽어 있었다. 봇 매치 9772501에서
        // 10명 중 9명이 스폰 방을 한 번도 안 나가고 그 방이 닫힐 때 죽었다 —
        // 폐쇄가 수렴 장치가 아니라 타이머 처형으로 동작했다.
        // 몹은 도망칠 수 있고 폐쇄는 못 도망친다. 순서가 그대로 우선순위다.

        // 0) 경계 밖 탈출 최우선 — 자기장에서는 안쪽으로 걷는 것 자체가 대피 경로다.
        //    목적지는 자기장 안쪽 대피 구역 (#272 수리: 옛 사냥터 후보는 외곽 방이라 다음 희생양).
        if (IsSwarmAreaOutside(runtime, bot.Player.CurrentArea))
        {
            bot.FleeDirective = true;
            var (evacuationArea, evacuationCell) =
                ResolveSwarmFieldEvacuationTarget(runtime, bot.Player.Position!);
            return new SwarmBotDirective(
                SwarmBotMode.Escort,
                evacuationArea,
                evacuationCell,
                BotPlayerManager.CellToWorldPosition(Config.SWARM_MATCH_MAP, evacuationCell));
        }

        // 0.2) 자기장 셀 대피 (#272): 구역 단위
        //      신호(완전-밖·경고)만 보면 경계가 방을 관통하는 동안 빨간 쪽에 선 봇이 오염을
        //      그대로 마신다. 내 셀이 경계 밖이거나 여유(3셀) 안이면 같은 구역의 안쪽 셀로
        //      물러나고, 구역에 안전 셀이 없으면 경계 안 이웃 구역으로 나간다.
        double fieldSafeDistance = runtime.Closures.GetSafeDistance(DateTime.UtcNow);
        if (fieldSafeDistance < double.MaxValue)
        {
            // 0.15) 방 마감 선제 탈출 (#272, 봇 매치 9831482 실측: 3-2교실 폐쇄 39초 뒤에도 봇이
            //       남아 420 사망): 경고(15초 전) 기반 철수는 큰 방·문 경유 이동에 너무 늦다 —
            //       내 구역이 잠기기까지 25초 안이면 지금 나간다. 수축은 선형이라 시각이 정확하다.
            double shrinkRatePerSecond = SwarmPressureField.MaxDistance / SwarmPressureField.ShrinkSeconds;
            int currentAreaMinDistance = SwarmPressureField.GetAreaMinDistance(bot.Player.CurrentArea);
            double secondsUntilAreaOutside =
                (fieldSafeDistance - currentAreaMinDistance) / shrinkRatePerSecond;
            if (secondsUntilAreaOutside < SwarmBotAreaExitLeadSeconds)
            {
                bot.FleeDirective = true;
                var (exitArea, exitCell) = ResolveSwarmFieldEvacuationTarget(runtime, bot.Player.Position!);
                return new SwarmBotDirective(
                    SwarmBotMode.Escort,
                    exitArea,
                    exitCell,
                    BotPlayerManager.CellToWorldPosition(Config.SWARM_MATCH_MAP, exitCell));
            }

            var botCell = ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, bot.Player.Position!);
            if (SwarmPressureField.GetDistance(botCell) >
                fieldSafeDistance - SwarmBotFieldEvacuateMarginCells)
            {
                // 대피는 도주 예외 — 왕복 억제를 우회해 즉시 물러난다.
                bot.FleeDirective = true;

                // 같은 구역에서 여유 두 배(6셀)까지 안전한 셀 중 가장 가까운 곳으로.
                Cell? retreatCell = null;
                float retreatBestSq = float.MaxValue;
                foreach (var entry in SwarmPressureField.GetAreaCellsByDistance(bot.Player.CurrentArea))
                {
                    if (entry.Distance > fieldSafeDistance - SwarmBotFieldEvacuateMarginCells * 2)
                        break;
                    var candidate = BotPlayerManager.CellToWorldPosition(Config.SWARM_MATCH_MAP, entry.Cell);
                    float candidateDx = candidate.X - bot.Player.Position!.X;
                    float candidateDy = candidate.Y - bot.Player.Position!.Y;
                    float candidateSq = candidateDx * candidateDx + candidateDy * candidateDy;
                    if (candidateSq >= retreatBestSq) continue;
                    retreatBestSq = candidateSq;
                    retreatCell = entry.Cell;
                }

                if (retreatCell != null)
                    return new SwarmBotDirective(
                        SwarmBotMode.Escort,
                        bot.Player.CurrentArea,
                        retreatCell,
                        BotPlayerManager.CellToWorldPosition(Config.SWARM_MATCH_MAP, retreatCell));

                // 이 방엔 이제 설 자리가 없다 — 자기장 안쪽 대피 구역으로.
                var (fieldEvacuationArea, fieldEvacuationCell) =
                    ResolveSwarmFieldEvacuationTarget(runtime, bot.Player.Position!);
                return new SwarmBotDirective(
                    SwarmBotMode.Escort,
                    fieldEvacuationArea,
                    fieldEvacuationCell,
                    BotPlayerManager.CellToWorldPosition(Config.SWARM_MATCH_MAP, fieldEvacuationCell));
            }
        }

        // 여기부터는 대피가 필요 없는 상태 — 위협이 있으면 원래 지시(Return)를 따른다.
        if (directive.Mode != SwarmBotMode.Escort)
            return directive;

        // 0.5) 상대 전력 비교 (#222): 티어 가중 전력(1/1.75/4)으로 비교한다.
        //      "싸움을 건다 = 유리하다" — 확실히 우세(×1.25 이상)일 때만 추격하고,
        //      동수 포함 그 이하는 회피한다. 동수 대치(뭉쳐서 수동 오브 소모전)가 성립하지
        //      않게 하는 규칙. 임계 사이 구간(1.0~1.25)은 중립 밴드 = 판단 떨림 방지.
        //      빈손은 화력이 0이라 몹도 강자로 취급해 피한다.
        float squadPower = GetSwarmSquadPower(runtime, botPlayerId);
        bool hasSquadOrbs = squadPower > 0f;
        // 빈손 이속 (#223): 이동 배율이 읽는 플래그 — 판단 틱이 단일 갱신 지점이다.
        // 빈손으로 막 전이한 순간에만 가속 유예를 연다 (#229 12단계).
        if (!hasSquadOrbs && !bot.IsSwarmBareHanded)
            bot.SwarmBareSpeedUntilUtc =
                DateTime.UtcNow.AddSeconds(Config.SWARM_BARE_MOVE_SPEED_SECONDS);
        bot.IsSwarmBareHanded = !hasSquadOrbs;
        // 치명상 이탈: 체력이 40% 이하인 봇은 전력 비교 없이 모든 상대를 강자로 보고 물러나며(추격도 압박도 없음),
        // 45% 아래로 회복해야 다시 싸운다 — 다친 쪽이 등을 보이고 성한 쪽이 쫓는 그림이 서야 하고, 후반까지
        // 살아 있는 봇이 있어야 폐쇄 수렴전이 선다.
        bool wounded = UpdateSwarmBotWoundedState(runtime, bot);
        FindNearbySwarmRivals(runtime, bot, wounded ? 0f : squadPower,
            includeMonstersAsStronger: !hasSquadOrbs,
            out Vector3f? strongerPosition,
            out (Vector3f Position, AreaType Area, long PlayerId)? weakerRival);

        // 피격 반응 (#222, 매치 2379 -131 · 2386 -182): 맞는 동안은 절대 서 있지 않는다.
        // 열세·비등이면 그 방향에서 이탈(위협 승격), 우세면 싸우되 좌우 와리가리(스트레이프) —
        // 이동 중 공격이 허용되므로 화력 손실 없이 피격 정지 현상만 사라진다.
        bool recentlyDamaged =
            (DateTime.UtcNow - bot.LastDamagedAtUtc).TotalSeconds <= SwarmBotDamagedFleeSeconds;
        Vector3f? recentAttackerPosition = null;
        if (recentlyDamaged && bot.LastProximityAttackerPlayerId != 0)
            TryGetSwarmParticipantPosition(
                runtime, bot.LastProximityAttackerPlayerId, out recentAttackerPosition);
        if (strongerPosition == null && recentAttackerPosition != null)
        {
            float attackerPower = GetSwarmSquadPower(runtime, bot.LastProximityAttackerPlayerId);
            // 확실한 강자(×1.5 이상)에게 맞았을 때만 이탈 — 동수·소폭 열세 공격자에게는 압박 전진한다
            // (SwarmBotFleePowerRatio 주석).
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
                float pressDx = recentAttackerPosition.X - bot.Player.Position!.X;
                float pressDy = recentAttackerPosition.Y - bot.Player.Position!.Y;
                if (pressDx * pressDx + pressDy * pressDy > 2.25f)
                {
                    Cell pressCell = ProximityCombatLineOfSight.WorldPositionToCell(
                        Config.SWARM_MATCH_MAP, recentAttackerPosition);
                    if (GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, pressCell) &&
                        GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, pressCell) is var pressArea &&
                        pressArea != AreaType.None)
                    {
                        return new SwarmBotDirective(
                            SwarmBotMode.Escort,
                            pressArea,
                            pressCell,
                            BotPlayerManager.CellToWorldPosition(Config.SWARM_MATCH_MAP, pressCell));
                    }
                }
            }
        }
        if (strongerPosition != null)
        {
            // 더 강한 상대를 만나면 도주 지시를 우선한다.
            bot.FleeDirective = true;
            float fleeDx = bot.Player.Position!.X - strongerPosition.X;
            float fleeDy = bot.Player.Position!.Y - strongerPosition.Y;
            float fleeLength = MathF.Sqrt(fleeDx * fleeDx + fleeDy * fleeDy);
            if (fleeLength < 0.001f)
            {
                fleeDx = 1f;
                fleeDy = 0f;
                fleeLength = 1f;
            }

            // 위협 반대 방향의 이동 가능한 셀로 도주한다.
            var fleeProbe = new Vector3f(
                bot.Player.Position!.X + fleeDx / fleeLength * SwarmBotFleeProbeDistance,
                bot.Player.Position!.Y + fleeDy / fleeLength * SwarmBotFleeProbeDistance,
                0f);

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
                    return new SwarmBotDirective(
                        SwarmBotMode.Escort, fleeFallbackArea, fleeFallbackCell, fleeFallbackWorld);
            }

            // 벽 방향이거나 도주지가 제자리면 위협 반대편에서 가장 가까운 열린 사냥 구역
            // 스폰으로 물러난다 — 구역을 아예 벗어나야 진짜 도주다 (#223 구석 정지 수리).
            AreaType fleeRetreatArea = SwarmHuntingAreas
                .Where(area => !IsSwarmAreaOutside(runtime, area))
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
            return new SwarmBotDirective(
                SwarmBotMode.Escort,
                fleeRetreatArea,
                fleeRetreatCell,
                BotPlayerManager.CellToWorldPosition(Config.SWARM_MATCH_MAP, fleeRetreatCell));
        }

        // 절단 직후 회수 (#226 F): 방금 끊은 전리품부터 줍는다 — 추격은 그 다음이다.
        if (bot.LastTrailCutAtUtc is { } lastCutAtUtc &&
            (DateTime.UtcNow - lastCutAtUtc).TotalSeconds < SwarmBotPostCutLootSeconds &&
            TryFindNearestSwarmGroundStone(runtime, bot, out Vector3f lootPosition))
        {
            return new SwarmBotDirective(
                SwarmBotMode.Escort,
                bot.Player.CurrentArea,
                ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, lootPosition),
                lootPosition);
        }

        // 선두 점수 보존 (#226 F): 오브 선두는 약자 추격을 자제한다 — 이기고 있을 때
        // 싸움은 절단(상대의 유일한 역전 수단)에 점수를 노출하는 행동이다.
        if (hasSquadOrbs && weakerRival.HasValue &&
            !IsSwarmOrbLeader(runtime, botPlayerId))
        {
            // 약자 추격은 본체가 아니라 오브열을 겨눈다 (#229 8단계). 코어 동사가 "몸으로 상대
            // 오브열을 자른다"인데 본체로 직진하면 꼬리를 지나칠 수 있다 — 봇 매치 9774851에서
            // 조우 29건에 절단 0건이었다. 꼬리 중간을 목표로 삼으면 접근 경로가 열을 가로지른다.
            var chaseTarget = ResolveSwarmTrailChasePoint(
                runtime, weakerRival.Value.PlayerId, weakerRival.Value.Position);
            // 추격 계측 (#229 8단계): 조우는 나는데 절단이 0건인 원인을 가르려면 "추격이
            // 발동은 했는가"와 "발동하고도 못 잘랐는가"를 구분해야 한다. 매치 요약에 남긴다.
            LogSwarmChaseIssued(runtime, botPlayerId, weakerRival.Value.PlayerId, chaseTarget);
            var chaseCell = ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, chaseTarget);
            var chaseArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, chaseCell);
            bool chaseCellUsable = chaseArea != AreaType.None &&
                                   GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, chaseCell);
            return new SwarmBotDirective(
                SwarmBotMode.Escort,
                chaseCellUsable ? chaseArea : weakerRival.Value.Area,
                chaseCellUsable
                    ? chaseCell
                    : ProximityCombatLineOfSight.WorldPositionToCell(
                        Config.SWARM_MATCH_MAP, weakerRival.Value.Position),
                chaseCellUsable ? chaseTarget : weakerRival.Value.Position);
        }

        // 같은 구역 바닥 소환석 — 걸어가면 자동 픽업 반경이 줍는다.
        if (TryFindNearestSwarmGroundStone(runtime, bot, out Vector3f stonePosition))
        {
            return new SwarmBotDirective(
                SwarmBotMode.Escort,
                bot.Player.CurrentArea,
                ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, stonePosition),
                stonePosition);
        }

        // 사냥 정지: 도주·줍기 용무가 없고 사거리 안에 몹이 있으면 제자리에 선다.
        //    정지 공격 규칙에서 서야 쏘고, 잠든 공급 무리 옆이 안전 사격 지점이다.
        //    빈손은 제외 — 화력 없이 몹 옆에 서는 건 자살이다 (#222).
        if (hasSquadOrbs && HasSwarmMonsterInBasicRange(runtime, bot))
        {
            return new SwarmBotDirective(
                SwarmBotMode.Escort,
                bot.Player.CurrentArea,
                ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, bot.Player.Position!),
                bot.Player.Position!);
        }

        // 다음 오브 성장 비용이 부족하면 사냥을 나간다.
        //      지역 공급 (#226 단계 B): 몹이 남은 가장 가까운 공급 무리로 향한다 — 몹은
        //      찾아가는 공유 자원이고, 미니맵 스냅샷으로 사람에게도 같은 정보가 보인다.

        if (bot.Player.SummonStones.StoneCount <
            growth.GetNextOrbGrowthCost(runtime, bot.Player) &&
            hasSquadOrbs)
        {
            if (TryFindNearestSwarmSupplyMonster(runtime, bot, out var supplyArea,
                    out var supplyPosition))
            {
                return new SwarmBotDirective(
                    SwarmBotMode.Escort,
                    supplyArea,
                    ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, supplyPosition),
                    supplyPosition);
            }
        }

        // 마른 방 탈출: 현재 구역에 살아있는 몹이 없으면
        //    몹이 남은 공급 구역으로 이주 — 스폰이 멈춘 종반에는 지시 없이 배회(디렉터 몫).
        bool currentAreaHasSupply = runtime.Monsters.GetVisualStates()
            .Any(monster => monster.IsAlive && monster.AreaType == bot.Player.CurrentArea);
        if (!currentAreaHasSupply &&
            TryFindNearestSwarmSupplyMonster(runtime, bot, out var migrateArea,
                out var migratePosition))
        {
            return new SwarmBotDirective(
                SwarmBotMode.Escort,
                migrateArea,
                ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, migratePosition),
                migratePosition);
        }

        return directive;
    }

    /// <summary>
    ///     지역 공급 사냥 목적지 (#226 단계 B): 폐쇄·경계 밖을 제외하고 살아있는 공급 몹 중
    ///     가장 가까운 개체의 위치. 봇의 파밍 이동은 항상 Escort 모드로 나가야 개봉 채널
    ///     완료 로직이 산다 (Return 단락 사고).
    /// </summary>
    private bool TryFindNearestSwarmSupplyMonster(
        MatchRuntime runtime, BotPlayerState bot, out AreaType area, out Vector3f position)
    {
        area = AreaType.None;
        position = null!;
        float bestSquared = float.MaxValue;
        foreach (var monster in runtime.Monsters.GetVisualStates())
        {
            if (!monster.IsAlive || IsSwarmAreaOutside(runtime, monster.AreaType))
                continue;
            float dx = monster.PositionX - bot.Player.Position!.X;
            float dy = monster.PositionY - bot.Player.Position!.Y;
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
        float dx = target.X - bot.Player.Position!.X;
        float dy = target.Y - bot.Player.Position!.Y;
        return dx * dx + dy * dy >=
               SwarmBotMinFleeTargetDistance * SwarmBotMinFleeTargetDistance;
    }

    /// <summary>이 봇의 스쿼드 오브 총 개수 — 저성장(파밍 부족) 판정용.</summary>

    // 추격 우위 임계: 내 전력이 상대의 이 배수 이상일 때만 붙는다.
    private const float SwarmBotChasePowerAdvantage = 1.25f;

    // 도주 임계: 상대 전력이 내 전력의 이 배수 이상일 때만 피한다. 그 사이(동수·소폭 열세, 1/1.5 ~ 1.25배)는
    // 중립 — 피하지도 붙지도 않고 하던 일을 한다. 오브 손실이 절단 전용이 된 뒤로 동수 대치는 서로의 꼬리 주위를
    // 도는 코어 동사라 동수를 강자로 보지 않는다. 빈손(전력 0)은 여전히 모두를 강자로 본다.
    private const float SwarmBotFleePowerRatio = 1.5f;

    // 치명상 이탈: 체력이 최대의 40% 이하이면 치명상, 55% 이상으로 회복해야
    // 해제 — 회복 1틱에 상태가 뒤집혀 "도망↔복귀"가 떨리지 않게 히스테리시스를 둔다.
    private const float SwarmBotWoundedEnterRatio = 0.4f;
    private const float SwarmBotWoundedExitRatio = 0.55f;

    /// <summary>봇의 치명상 상태를 갱신하고 돌려준다 — 남은 체력 40% 이하에서 진입, 55% 이상에서 해제.</summary>
    private bool UpdateSwarmBotWoundedState(MatchRuntime runtime, BotPlayerState bot)
    {
        bool wounded = bot.Wounded;
        float ratio = bot.Player.Health / (float)Config.MAX_HEALTH;
        if (!wounded && ratio <= SwarmBotWoundedEnterRatio)
        {
            bot.Wounded = true;
            return true;
        }

        if (wounded && ratio >= SwarmBotWoundedExitRatio)
        {
            bot.Wounded = false;
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

    // 봇 절단 자제: 절단 뒤 체력이 이 비율(최대 체력의 절반)보다 적으면 봇은 자르지
    // 않고, 자른 뒤 이 시간 동안은 다시 자르지 않는다. 사람에게는 적용하지 않는다.
    private const float SwarmBotCutMinHealthRatio = 0.5f;
    private const double SwarmBotCutCooldownSeconds = 6d;

    /// <summary>
    ///     봇이 지금 절단을 질러도 되는가 — 비용을 내고도 체력이 절반 이상이고, 직전 절단에서 쿨다운이 지났는가.
    ///     사람 판정이 아니다: 사람의 절단은 체력이 0이 되지만 않으면 언제나 성립한다.
    /// </summary>
    public bool IsSwarmBotCutAllowed(BotPlayerState bot, int healthBefore, DateTime nowUtc, int cutCost)
    {
        if (healthBefore - cutCost <
            Config.MAX_HEALTH * SwarmBotCutMinHealthRatio)
            return false;
        return bot.LastTrailCutAtUtc is not { } lastCutAtUtc ||
               (nowUtc - lastCutAtUtc).TotalSeconds >= SwarmBotCutCooldownSeconds;
    }

    /// <summary>
    ///     추격 조준점 (#229 8단계): 상대 오브열의 중간 지점. 열이 없으면 본체를 그대로 돌려준다.
    ///     본체를 겨누면 꼬리를 지나치지 않고 옆으로 붙어 서기만 한다 — 절단이 성립하지 않는다.
    /// </summary>
    private Vector3f ResolveSwarmTrailChasePoint(MatchRuntime runtime, long targetPlayerId, Vector3f targetPosition)
    {
        var targetPlayer = runtime.GetParticipant(targetPlayerId)!;
        int orbCount = orbTrails.CountOrbs(runtime, targetPlayer);
        if (orbCount <= 0)
            return targetPosition;

        // 중간 순번을 노린다 — 꼬리 끝은 손실이 적고, 머리 바로 뒤는 도달 전에 흔들린다.
        int aimOrdinal = Math.Max(1, orbCount / 2);
        return orbTrails.GetOrbPosition(runtime, targetPlayer, aimOrdinal, targetPosition)
               ?? targetPosition;
    }

    private void FindNearbySwarmRivals(
        MatchRuntime runtime,
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
            float dx = position.X - bot.Player.Position!.X;
            float dy = position.Y - bot.Player.Position!.Y;
            float distanceSquared = dx * dx + dy * dy;
            if (distanceSquared >= radiusSquared) return;

            float rivalPower = GetSwarmSquadPower(runtime, rivalPlayerId);
            // 확실한 강자(×1.5 이상)만 피한다 — 동수·소폭 열세는 중립 (SwarmBotFleePowerRatio 주석).
            // 빈손(myPower 0)은 전력 있는 모두가 강자다.
            if (rivalPower >= myPower * SwarmBotFleePowerRatio && distanceSquared < bestStrongerDistanceSquared)
            {
                bestStrongerDistanceSquared = distanceSquared;
                nearestStronger = position;
            }
            else if (myPower >= rivalPower * SwarmBotChasePowerAdvantage &&
                     distanceSquared < bestWeakerDistanceSquared &&
                     !IsSwarmAreaOutside(runtime, area))
            {
                bestWeakerDistanceSquared = distanceSquared;
                nearestWeaker = (position, area, rivalPlayerId);
            }
        }

        foreach (var player in runtime.GetAlivePlayers())
        {
            if (player.PlayerId == bot.PlayerId || player.Position == null) continue;
            Consider(player.PlayerId, player.Position, player.CurrentArea);
        }

        if (includeMonstersAsStronger)
        {
            foreach (var monster in runtime.Monsters.GetVisualStates())
            {
                if (!monster.IsAlive) continue;
                float dx = monster.PositionX - bot.Player.Position!.X;
                float dy = monster.PositionY - bot.Player.Position!.Y;
                float distanceSquared = dx * dx + dy * dy;
                if (distanceSquared >= bestStrongerDistanceSquared) continue;
                bestStrongerDistanceSquared = distanceSquared;
                nearestStronger = new Vector3f(monster.PositionX, monster.PositionY, 0f);
            }
        }

        strongerPosition = nearestStronger;
        weakerRival = nearestWeaker;
    }

    private bool HasSwarmMonsterInBasicRange(MatchRuntime runtime, BotPlayerState bot)
    {
        float rangeSquared = Config.SWARM_ORB_ATTACK_RANGE * Config.SWARM_ORB_ATTACK_RANGE;
        foreach (var target in runtime.Monsters.GetCombatTargets())
        {
            if (target.Area != bot.Player.CurrentArea)
                continue;
            float dx = target.Position.X - bot.Player.Position!.X;
            float dy = target.Position.Y - bot.Player.Position!.Y;
            if (dx * dx + dy * dy <= rangeSquared)
                return true;
        }

        return false;
    }

    /// <summary>안전하고 체력이 부족하면 수면을 선택한다. 회복은 공통 Player 규칙으로 처리한다.</summary>
    public void UpdateSleep(MatchRuntime runtime, IReadOnlyList<BotPlayerState> bots, DateTime nowUtc)
    {
        var sessions = runtime.GetSessions();
        var players = runtime.GetAlivePlayers();
        var monsters = runtime.Monsters.GetCombatTargets();
        float safeRadiusSquared = Config.SWARM_ORB_ATTACK_RANGE * Config.SWARM_ORB_ATTACK_RANGE;
        foreach (var bot in bots)
        {
            var player = bot.Player;
            var position = player.Position!;
            bool unsafeToSleep = player.IsEliminated || player.Health <= 0 || !player.CanSleep(nowUtc) ||
                player.CurrentArea == AreaType.None || player.PendingDoorInteractionId.HasValue ||
                runtime.Closures.IsAreaClosed(player.CurrentArea) ||
                MatchFieldService.GetDamagePerTick(runtime, position, nowUtc) > 0 ||
                nowUtc < bot.SwarmDodgeHoldUntilUtc ||
                SwarmBotDodgePolicy.ResolveSwarmBotDodgeDirection(runtime.SunOrbAttacks.DodgeSnapshot,
                    player.PlayerId, position, player.CurrentArea, nowUtc) != null;
            foreach (var other in players)
            {
                if (unsafeToSleep) break;
                if (other.PlayerId == player.PlayerId || other.IsEliminated || other.Position == null) continue;
                float dx = other.Position.X - position.X;
                float dy = other.Position.Y - position.Y;
                unsafeToSleep = dx * dx + dy * dy <= safeRadiusSquared;
            }
            foreach (var monster in monsters)
            {
                if (unsafeToSleep) break;
                float dx = monster.Position.X - position.X;
                float dy = monster.Position.Y - position.Y;
                unsafeToSleep = dx * dx + dy * dy <= safeRadiusSquared;
            }
            bool changed = unsafeToSleep || player.Health >= Config.MAX_HEALTH
                ? player.TryStopSleep()
                : player.TryStartSleep(nowUtc);
            if (!changed) continue;
            using var packet = PacketMaker.G_TO_C_PLAYER_STATE(player.PlayerId, player.State);
            foreach (var session in sessions)
            {
                if (!session.Player.IsEliminated && session.Player.CurrentArea == player.CurrentArea)
                    session.TrySend(packet);
            }
        }
    }
    /// <summary>
    ///     같은 구역의 반응 지연 지난 최근접 바닥 소환석 — 봇 회수 지시의 목적지.
    ///     반응 지연 (#222): 갓 떨어진 돌은 무시 — 사람이 먼저 주울 시간을 준다.
    /// </summary>
    private bool TryFindNearestSwarmGroundStone(
        MatchRuntime runtime, BotPlayerState bot, out Vector3f position)
    {
        position = null!;
        float bestDistanceSquared = float.MaxValue;
        foreach (var item in runtime.GroundItems.GetItemsInArea(bot.Player.CurrentArea))
        {
            if (item.ItemId != Config.SUMMON_STONE_GROUND_ITEM_ID ||
                runtime.GroundItems.WasSpawnedWithin(
                    item.GroundItemUid, BotPlayerManager.SummonStoneBotReactionDelay))
                continue;

            float dx = item.PositionX - bot.Player.Position!.X;
            float dy = item.PositionY - bot.Player.Position!.Y;
            float distanceSquared = dx * dx + dy * dy;
            if (distanceSquared >= bestDistanceSquared)
                continue;

            bestDistanceSquared = distanceSquared;
            position = new Vector3f(item.PositionX, item.PositionY, 0f);
        }

        return position != null;
    }

    /// <summary>오브 선두 판독 (#226 F): 내 오브 수가 생존자 최다와 같거나 크면 선두다.</summary>
    private bool IsSwarmOrbLeader(MatchRuntime runtime, long playerId)
    {
        int myOrbCount = runtime.GetOrbs(playerId).GetOrbScore().OrbCount;
        return myOrbCount > 0 && myOrbCount >= growth.GetTopOrbCount(runtime);
    }

    /// <summary>참가자(사람·봇) 위치 조회 — 피격 반응의 도주 기준점.</summary>
    private bool TryGetSwarmParticipantPosition(MatchRuntime runtime, long playerId, out Vector3f position)
    {
        position = null!;
        var player = runtime.GetParticipant(playerId);
        if (player == null || player.IsEliminated || player.Position == null)
            return false;

        position = player.Position;
        return true;
    }

    private void LogSwarmChaseIssued(MatchRuntime runtime, long chaserId, long targetId, Vector3f aimPoint)
    {
        var target = runtime.GetParticipant(targetId);
        if (target == null)
            return;

        var chaser = runtime.Bots.GetBot(chaserId);
        if (chaser == null)
            return;

        var now = DateTime.UtcNow;
        if (chaser.ChaseLogThrottle.TryGetValue(targetId, out var lastAtUtc) &&
            (now - lastAtUtc).TotalSeconds < 3d)
            return;

        chaser.ChaseLogThrottle[targetId] = now;
        eventLogs.LogSystem(runtime.MatchingId,
            $"swarm_chase chaser={chaserId} target={targetId} " +
            $"targetOrbs={orbTrails.CountOrbs(runtime, target)} " +
            $"aim=({aimPoint.X:F1},{aimPoint.Y:F1})");
    }

    /// <summary>구역 전체가 현재 경계 밖(폐쇄·자기장)인가 — 봇 대피·스팟 필터의 기준.</summary>
    private bool IsSwarmAreaOutside(MatchRuntime runtime, AreaType area) =>
        runtime.Closures.IsAreaClosed(area) ||
        SwarmPressureField.GetAreaMinDistance(area) >
        runtime.Closures.GetSafeDistance(DateTime.UtcNow);

    /// <summary>
    ///     티어 가중 전력(1/1.75/4 합) — 개수 비교의 왜곡(T3 1개 = T1 1개 취급) 방지.
    ///     상자 시간 등급 도입 후 회피/추격 판단의 단일 기준.
    /// </summary>
    private float GetSwarmSquadPower(MatchRuntime runtime, long playerId) =>
        runtime.GetOrbs(playerId).GetOrbPower();

}
