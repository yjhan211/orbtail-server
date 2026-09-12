using game_server.matches;
using game_server.sessions;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.players.bots;

/// <summary>
///     봇의 대피·추격·아이템 회수 방향을 결정하고, 문 열기·수면·오브 성장을 공통 Player 규칙으로 실행한다.
///     기억과 재사용 대기 시간은 매치가 소유하며 호출자는 매치 잠금을 보유한다.
///     이동 지시의 실제 실행은 BotMovementService가 맡는다.
/// </summary>
internal sealed class BotBehaviorService(
    PlayerOrbGrowthService growth,
    PlayerOrbTrailService orbTrails,
    PlayerInteractionService interactions,
    ILogger<BotBehaviorService> logger)
{
    // 문 상호작용
    private const float SwarmBotDoorUnlockRange = 1.6f;

    // 구역 이동·대피·전리품 회수
    private const double SwarmBotAreaReturnCooldownSeconds = 5d;
    private const double SwarmBotPostCutLootSeconds = 5d;
    private const int SwarmBotFieldEvacuateMarginCells = 5;
    private const double SwarmBotAreaExitLeadSeconds = 25d;

    // 몬스터 위협 회피
    private const float MonsterDangerRadius = 5f;
    private const float MonsterFleeDistance = 5f;
    private const float MonsterRoamDistance = 3f;
    private const float MinThreatFleeDistance = 2f;
    private const double FleeCommitSeconds = 2d;

    // 플레이어 추격·도주
    private const float SwarmBotRivalScanRadius = 11f;
    private const float SwarmBotFleeProbeDistance = 8f;
    private const float SwarmBotMinFleeTargetDistance = 3f;
    private const float SwarmBotChasePowerAdvantage = 1.25f;
    private const float SwarmBotFleePowerRatio = 1.5f;
    private const double SwarmBotDamagedFleeSeconds = 6d;

    // 부상·절단 판단
    private const float SwarmBotWoundedEnterRatio = 0.4f;
    private const float SwarmBotWoundedExitRatio = 0.55f;
    private const float SwarmBotCutMinHealthRatio = 0.5f;
    private const double SwarmBotCutCooldownSeconds = 6d;

    // 사냥 대상 구역
    private static readonly AreaType[] SwarmHuntingAreas =
    [
        Config.SWARM_MATCH_GROUND_AREA, AreaType.S2Library1, AreaType.S2Library2,
        AreaType.S2Gym1, AreaType.S2Gym2
    ];

    private bool TryUpgradePreferredOrb(MatchRuntime runtime, long playerId)
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

    public void ProcessOrbGrowth(MatchRuntime runtime, IReadOnlyList<Bot> aliveBots)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Bot decisions require the match lock.");
        }
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
            if (preferUpgrade && TryUpgradePreferredOrb(runtime, bot.PlayerId))
            {
                continue;
            }

            if (orbCount < Config.SWARM_ORB_CAPACITY && growth.Summon(runtime, bot.Player).Success)
            {
                continue;
            }
            TryUpgradePreferredOrb(runtime, bot.PlayerId);
        }
    }

    public void ProcessDoorInteractions(
        MatchRuntime runtime,
        List<Bot> bots,
        List<GameClientSession> sessions,
        DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Bot decisions require the match lock.");
        }
        long now = nowUtc.Ticks / TimeSpan.TicksPerMillisecond;
        foreach (var bot in bots)
        {
            var player = bot.Player;
            if (runtime.IsEnded || player.IsEliminated || player.IsSleeping)
            {
                continue;
            }
            if (player.PendingDoorInteractionId is { } doorId)
            {
                if (!interactions.TryFinishDoor(runtime, player, doorId, doorId, now, out _))
                {
                    continue;
                }
                using var openPacket = PacketMaker.G_TO_C_DOOR_STATE_UPDATE(doorId, true, ErrorCode.SUCCESS, bot.PlayerId);
                foreach (var session in sessions)
                {
                    session.TrySend(openPacket);
                }
                logger.LogInformation("Swarm bot unlocked door: MatchingId={MatchingId}, BotId={BotId}, DoorId={DoorId}",
                    runtime.MatchingId, bot.PlayerId, doorId);
                continue;
            }
            if (!TryFindNearestClosedDoor(runtime, bot, out int targetDoorId))
            {
                continue;
            }
            interactions.StartDoor(runtime, player, targetDoorId, targetDoorId, now);
        }
    }

    private bool TryFindNearestClosedDoor(MatchRuntime runtime, Bot bot, out int doorId)
    {
        doorId = 0;
        float best = float.MaxValue;
        bool insideClosed = runtime.Closures.IsAreaClosed(bot.Player.CurrentArea);
        foreach (var door in GameDoorData.GetByAreaType(bot.Player.CurrentArea))
        {
            if (!GameInteractableData.IsGaugeGatedDoor(door.DoorId))
            {
                continue;
            }
            if (runtime?.Doors.IsDoorOpen(door.DoorId) == true)
            {
                continue;
            }
            if (!insideClosed &&
                !GameInteractableData.IsGaugeDoorOperableFrom(door.DoorId, (int)bot.Player.CurrentArea))
            {
                continue;
            }
            if (!insideClosed &&
                (runtime.Closures.IsAreaClosed(door.AreaType) ||
                 runtime.Closures.IsAreaClosed(door.AreaTypeB)))
            {
                continue;
            }

            var doorWorld = MatchBots.CellToWorldPosition(
                Config.SWARM_MATCH_MAP, new Cell((int)door.PositionX, (int)door.PositionY));
            float dx = doorWorld.X - bot.Player.Position!.X;
            float dy = doorWorld.Y - bot.Player.Position!.Y;
            float distanceSquared = dx * dx + dy * dy;
            if (distanceSquared > SwarmBotDoorUnlockRange * SwarmBotDoorUnlockRange)
            {
                continue;
            }
            if (distanceSquared >= best)
            {
                continue;
            }

            best = distanceSquared;
            doorId = door.DoorId;
        }

        return doorId > 0;
    }


    private (AreaType Area, Cell Cell) FindEvacuationTarget(MatchRuntime runtime, Vector3f botPosition)
    {
        double safeDistance = runtime.Closures.GetSafeDistance(DateTime.UtcNow);
        AreaType bestArea = Config.SWARM_MATCH_GROUND_AREA;
        Cell bestCell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, Config.SWARM_MATCH_GROUND_AREA);
        float bestDistanceSquared = float.MaxValue;
        foreach (var region in GameMapData.GetAreas(Config.SWARM_MATCH_MAP))
        {
            var area = region.AreaType;
            if (area == AreaType.None)
            {
                continue;
            }
            var cells = SwarmPressureField.GetAreaCellsByDistance(area);
            if (cells.Count == 0 ||
                cells[0].Distance > safeDistance - SwarmBotFieldEvacuateMarginCells * 2)
            {
                continue;
            }

            var innermost = MatchBots.CellToWorldPosition(Config.SWARM_MATCH_MAP, cells[0].Cell);
            float dx = innermost.X - botPosition.X;
            float dy = innermost.Y - botPosition.Y;
            float distanceSquared = dx * dx + dy * dy;
            if (distanceSquared >= bestDistanceSquared)
            {
                continue;
            }
            bestDistanceSquared = distanceSquared;
            bestArea = area;
            bestCell = cells[0].Cell;
        }

        return (bestArea, bestCell);
    }

    public void DecideMovement(MatchRuntime runtime, long botPlayerId)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Bot decisions require the match lock.");
        }
        var bot = runtime.Bots.GetBot(botPlayerId);
        if (bot == null)
        {
            return;
        }
        bot.SetMovementTarget(BotMovementMode.None, AreaType.None, new Cell(0, 0), new Vector3f(0f, 0f, 0f));
        ChooseMovementTarget(runtime, bot);
        if (bot == null || bot.Player.IsEliminated || bot.Player.CurrentArea == AreaType.None)
        {
            return;
        }

        if (bot.AreaMemory is not { } memory)
        {
            bot.AreaMemory = (bot.Player.CurrentArea, AreaType.None, DateTime.MinValue);
            return;
        }

        if (memory.Area != bot.Player.CurrentArea)
        {
            memory = (bot.Player.CurrentArea, memory.Area, DateTime.UtcNow);
            bot.AreaMemory = memory;
        }

        if (bot.DesiredMovementMode != BotMovementMode.Escort)
        {
            return;
        }

        if (bot.FleeDirective)
        {
            return;
        }

        if (bot.DesiredMovementArea == memory.PreviousArea &&
            bot.DesiredMovementArea != bot.Player.CurrentArea &&
            (DateTime.UtcNow - memory.LeftAtUtc).TotalSeconds < SwarmBotAreaReturnCooldownSeconds)
        {
            bot.SetMovementTarget(
                BotMovementMode.Escort,
                bot.Player.CurrentArea,
                MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, bot.Player.Position!),
                bot.Player.Position!);
            return;
        }

        return;
    }


    private static void DecideMonsterAvoidance(MatchRuntime runtime, Bot bot, DateTime nowUtc)
    {
        var position = bot.Player.Position!;
        var area = bot.Player.CurrentArea;
        float threatPositionSumX = 0f, threatPositionSumY = 0f;
        int threatCount = 0;
        foreach (var monster in runtime.Monsters.Entities.Values)
        {
            if (!monster.Alive || nowUtc < monster.ActivatesAtUtc || monster.Area != area || !monster.Aggro)
            {
                continue;
            }
            float dx = monster.Position.X - position.X;
            float dy = monster.Position.Y - position.Y;
            if (dx * dx + dy * dy > MonsterDangerRadius * MonsterDangerRadius)
            {
                continue;
            }
            threatPositionSumX += monster.Position.X;
            threatPositionSumY += monster.Position.Y;
            threatCount++;
        }

        Vector3f destination;
        if (threatCount > 0)
        {
            if (bot.FleeCommitment is { } commitment && (nowUtc - commitment.CommittedAtUtc).TotalSeconds < FleeCommitSeconds)
            {
                float commitDx = commitment.Destination.X - position.X;
                float commitDy = commitment.Destination.Y - position.Y;
                if (commitDx * commitDx + commitDy * commitDy > 1f)
                {
                    bot.SetMovementTarget(BotMovementMode.Return, area,
                        MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, commitment.Destination), commitment.Destination);
                    return;
                }
            }

            float centroidX = threatPositionSumX / threatCount;
            float centroidY = threatPositionSumY / threatCount;
            float awayX = position.X - centroidX;
            float awayY = position.Y - centroidY;
            float length = MathF.Sqrt(awayX * awayX + awayY * awayY);
            if (length < 0.01f)
            {
                awayX = 1f;
                awayY = 0f;
                length = 1f;
            }

            destination = FindMonsterEscapePosition(position, area, awayX / length, awayY / length);
            bot.FleeCommitment = (destination, nowUtc);
        }
        else
        {
            bot.FleeCommitment = null;
            float angle = (float)(Random.Shared.NextDouble() * Math.PI * 2d);
            destination = new Vector3f(
                position.X + MathF.Cos(angle) * MonsterRoamDistance,
                position.Y + MathF.Sin(angle) * MonsterRoamDistance,
                0f);
            destination = MapPathfinder.ClampToAreaWalkable(Config.SWARM_MATCH_MAP, destination, position, area);
        }

        var mode = threatCount > 0 ? BotMovementMode.Return : BotMovementMode.Escort;
        bot.SetMovementTarget(mode, area, MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, destination), destination);
        return;
    }

    private static Vector3f FindMonsterEscapePosition(Vector3f position, AreaType area, float directionX, float directionY)
    {
        ReadOnlySpan<float> angleOffsets = [0f, 45f, -45f, 90f, -90f, 135f, -135f, 180f];
        foreach (float angleDegrees in angleOffsets)
        {
            float radians = angleDegrees * MathF.PI / 180f;
            float cos = MathF.Cos(radians);
            float sin = MathF.Sin(radians);
            float rotatedX = directionX * cos - directionY * sin;
            float rotatedY = directionX * sin + directionY * cos;
            var candidate = MapPathfinder.ClampToAreaWalkable(Config.SWARM_MATCH_MAP, new Vector3f(
                position.X + rotatedX * MonsterFleeDistance,
                position.Y + rotatedY * MonsterFleeDistance,
                0f), position, area);
            float dx = candidate.X - position.X;
            float dy = candidate.Y - position.Y;
            if (dx * dx + dy * dy >= MinThreatFleeDistance * MinThreatFleeDistance)
            {
                return candidate;
            }
        }

        return MapPathfinder.ClampToAreaWalkable(Config.SWARM_MATCH_MAP,
            MatchBots.CellToWorldPosition(Config.SWARM_MATCH_MAP, GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, area)),
            position, area);
    }

    private void ChooseMovementTarget(MatchRuntime runtime, Bot bot)
    {
        long botPlayerId = bot.PlayerId;
        if (bot == null || bot.Player.IsEliminated || bot.Player.Position == null)
        {
            return;
        }
        DecideMonsterAvoidance(runtime, bot, DateTime.UtcNow);
        bot.FleeDirective = false;

        if (IsAreaUnsafe(runtime, bot.Player.CurrentArea))
        {
            bot.FleeDirective = true;
            var (evacuationArea, evacuationCell) =
                FindEvacuationTarget(runtime, bot.Player.Position!);
            bot.SetMovementTarget(
                BotMovementMode.Escort,
                evacuationArea,
                evacuationCell,
                MatchBots.CellToWorldPosition(Config.SWARM_MATCH_MAP, evacuationCell));
            return;
        }

        double fieldSafeDistance = runtime.Closures.GetSafeDistance(DateTime.UtcNow);
        if (fieldSafeDistance < double.MaxValue)
        {
            double shrinkRatePerSecond = SwarmPressureField.MaxDistance / SwarmPressureField.ShrinkSeconds;
            int currentAreaMinDistance = SwarmPressureField.GetAreaMinDistance(bot.Player.CurrentArea);
            double secondsUntilAreaOutside =
                (fieldSafeDistance - currentAreaMinDistance) / shrinkRatePerSecond;
            if (secondsUntilAreaOutside < SwarmBotAreaExitLeadSeconds)
            {
                bot.FleeDirective = true;
                var (exitArea, exitCell) = FindEvacuationTarget(runtime, bot.Player.Position!);
                bot.SetMovementTarget(
                    BotMovementMode.Escort,
                    exitArea,
                    exitCell,
                    MatchBots.CellToWorldPosition(Config.SWARM_MATCH_MAP, exitCell));
                return;
            }

            var botCell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, bot.Player.Position!);
            if (SwarmPressureField.GetDistance(botCell) >
                fieldSafeDistance - SwarmBotFieldEvacuateMarginCells)
            {
                bot.FleeDirective = true;

                Cell? retreatCell = null;
                float bestRetreatDistanceSquared = float.MaxValue;
                foreach (var entry in SwarmPressureField.GetAreaCellsByDistance(bot.Player.CurrentArea))
                {
                    if (entry.Distance > fieldSafeDistance - SwarmBotFieldEvacuateMarginCells * 2)
                    {
                        break;
                    }
                    var candidate = MatchBots.CellToWorldPosition(Config.SWARM_MATCH_MAP, entry.Cell);
                    float candidateDx = candidate.X - bot.Player.Position!.X;
                    float candidateDy = candidate.Y - bot.Player.Position!.Y;
                    float candidateDistanceSquared = candidateDx * candidateDx + candidateDy * candidateDy;
                    if (candidateDistanceSquared >= bestRetreatDistanceSquared)
                    {
                        continue;
                    }
                    bestRetreatDistanceSquared = candidateDistanceSquared;
                    retreatCell = entry.Cell;
                }

                if (retreatCell != null)
                {
                    bot.SetMovementTarget(
                        BotMovementMode.Escort,
                        bot.Player.CurrentArea,
                        retreatCell,
                        MatchBots.CellToWorldPosition(Config.SWARM_MATCH_MAP, retreatCell));
                    return;
                }

                var (fieldEvacuationArea, fieldEvacuationCell) =
                    FindEvacuationTarget(runtime, bot.Player.Position!);
                bot.SetMovementTarget(
                    BotMovementMode.Escort,
                    fieldEvacuationArea,
                    fieldEvacuationCell,
                    MatchBots.CellToWorldPosition(Config.SWARM_MATCH_MAP, fieldEvacuationCell));
                return;
            }
        }

        if (bot.DesiredMovementMode != BotMovementMode.Escort)
        {
            return;
        }

        float squadPower = GetOrbPower(runtime, botPlayerId);
        bool hasSquadOrbs = squadPower > 0f;
        if (!hasSquadOrbs && !bot.IsSwarmBareHanded)
        {
            bot.SwarmBareSpeedUntilUtc =
                DateTime.UtcNow.AddSeconds(Config.SWARM_BARE_MOVE_SPEED_SECONDS);
        }
        bot.IsSwarmBareHanded = !hasSquadOrbs;
        bool wounded = UpdateWoundedState(runtime, bot);
        FindNearbyThreatAndChaseTarget(runtime, bot, wounded ? 0f : squadPower,
            includeMonstersAsStronger: !hasSquadOrbs,
            out Vector3f? strongerPosition,
            out (Vector3f Position, AreaType Area, long PlayerId)? weakerRival);
        bool recentlyDamaged =
            (DateTime.UtcNow - bot.LastDamagedAtUtc).TotalSeconds <= SwarmBotDamagedFleeSeconds;
        Vector3f? recentAttackerPosition = null;
        if (recentlyDamaged && bot.LastProximityAttackerPlayerId != 0)
        {
            TryGetAlivePlayerPosition(
                runtime, bot.LastProximityAttackerPlayerId, out recentAttackerPosition);
        }
        if (strongerPosition == null && recentAttackerPosition != null)
        {
            float attackerPower = GetOrbPower(runtime, bot.LastProximityAttackerPlayerId);
            if (wounded || attackerPower >= squadPower * SwarmBotFleePowerRatio)
            {
                strongerPosition = recentAttackerPosition;
            }
            else
            {
                float pressDx = recentAttackerPosition.X - bot.Player.Position!.X;
                float pressDy = recentAttackerPosition.Y - bot.Player.Position!.Y;
                if (pressDx * pressDx + pressDy * pressDy > 2.25f)
                {
                    Cell pressCell = MapCoordinateConverter.WorldToCell(
                        Config.SWARM_MATCH_MAP, recentAttackerPosition);
                    if (GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, pressCell) &&
                        GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, pressCell) is var pressArea &&
                        pressArea != AreaType.None)
                    {
                        bot.SetMovementTarget(
                            BotMovementMode.Escort,
                            pressArea,
                            pressCell,
                            MatchBots.CellToWorldPosition(Config.SWARM_MATCH_MAP, pressCell));
                        return;
                    }
                }
            }
        }
        if (strongerPosition != null)
        {
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

            var fleeProbe = new Vector3f(
                bot.Player.Position!.X + fleeDx / fleeLength * SwarmBotFleeProbeDistance,
                bot.Player.Position!.Y + fleeDy / fleeLength * SwarmBotFleeProbeDistance,
                0f);
            Cell? fleeFallbackCell =
                MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, fleeProbe);
            if (!GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, fleeFallbackCell))
            {
                fleeFallbackCell = fleeFallbackCell.GetAdjacentCells()
                    .FirstOrDefault(cell => GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, cell));
            }

            if (fleeFallbackCell != null &&
                GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, fleeFallbackCell) is var fleeFallbackArea &&
                fleeFallbackArea != AreaType.None)
            {
                var fleeFallbackWorld = MatchBots.CellToWorldPosition(Config.SWARM_MATCH_MAP, fleeFallbackCell);
                if (IsEscapeTargetFarEnough(bot, fleeFallbackWorld))
                {
                    bot.SetMovementTarget(
                        BotMovementMode.Escort, fleeFallbackArea, fleeFallbackCell, fleeFallbackWorld);
                    return;
                }
            }

            AreaType fleeRetreatArea = SwarmHuntingAreas
                .Where(area => !IsAreaUnsafe(runtime, area))
                .OrderBy(area =>
                {
                    var center = MatchBots.CellToWorldPosition(
                        Config.SWARM_MATCH_MAP, GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, area));
                    float dx = center.X - fleeProbe.X;
                    float dy = center.Y - fleeProbe.Y;
                    return dx * dx + dy * dy;
                })
                .DefaultIfEmpty(Config.SWARM_MATCH_GROUND_AREA)
                .First();
            Cell fleeRetreatCell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, fleeRetreatArea);
            bot.SetMovementTarget(
                BotMovementMode.Escort,
                fleeRetreatArea,
                fleeRetreatCell,
                MatchBots.CellToWorldPosition(Config.SWARM_MATCH_MAP, fleeRetreatCell));
            return;
        }

        if (bot.LastTrailCutAtUtc is { } lastCutAtUtc &&
            (DateTime.UtcNow - lastCutAtUtc).TotalSeconds < SwarmBotPostCutLootSeconds &&
            TryFindNearestSummonStone(runtime, bot, out Vector3f lootPosition))
        {
            bot.SetMovementTarget(
                BotMovementMode.Escort,
                bot.Player.CurrentArea,
                MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, lootPosition),
                lootPosition);
            return;
        }

        if (hasSquadOrbs && weakerRival.HasValue &&
            !HasMostOrbs(runtime, botPlayerId))
        {
            var chaseTarget = GetTrailChasePosition(
                runtime, weakerRival.Value.PlayerId, weakerRival.Value.Position);
            var chaseCell = MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, chaseTarget);
            var chaseArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, chaseCell);
            bool chaseCellUsable = chaseArea != AreaType.None &&
                                   GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, chaseCell);
            bot.SetMovementTarget(
                BotMovementMode.Escort,
                chaseCellUsable ? chaseArea : weakerRival.Value.Area,
                chaseCellUsable
                    ? chaseCell
                    : MapCoordinateConverter.WorldToCell(
                        Config.SWARM_MATCH_MAP, weakerRival.Value.Position),
                chaseCellUsable ? chaseTarget : weakerRival.Value.Position);
            return;
        }

        if (TryFindNearestSummonStone(runtime, bot, out Vector3f stonePosition))
        {
            bot.SetMovementTarget(
                BotMovementMode.Escort,
                bot.Player.CurrentArea,
                MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, stonePosition),
                stonePosition);
            return;
        }

        if (hasSquadOrbs && HasMonsterInAttackRange(runtime, bot))
        {
            bot.SetMovementTarget(
                BotMovementMode.Escort,
                bot.Player.CurrentArea,
                MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, bot.Player.Position!),
                bot.Player.Position!);
            return;
        }

        if (bot.Player.SummonStones.StoneCount <
            growth.GetNextOrbGrowthCost(runtime, bot.Player) &&
            hasSquadOrbs)
        {
            if (TryFindNearestHuntTarget(runtime, bot, out var supplyArea,
                    out var supplyPosition))
            {
                bot.SetMovementTarget(
                    BotMovementMode.Escort,
                    supplyArea,
                    MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, supplyPosition),
                    supplyPosition);
                return;
            }
        }

        bool currentAreaHasSupply = false;
        foreach (var monster in runtime.Monsters.Entities.Values)
        {
            if (monster.Alive && monster.Area == bot.Player.CurrentArea)
            {
                currentAreaHasSupply = true;
                break;
            }
        }
        if (!currentAreaHasSupply &&
            TryFindNearestHuntTarget(runtime, bot, out var migrateArea,
                out var migratePosition))
        {
            bot.SetMovementTarget(
                BotMovementMode.Escort,
                migrateArea,
                MapCoordinateConverter.WorldToCell(Config.SWARM_MATCH_MAP, migratePosition),
                migratePosition);
            return;
        }

        return;
    }

    private bool TryFindNearestHuntTarget(
        MatchRuntime runtime, Bot bot, out AreaType area, out Vector3f position)
    {
        area = AreaType.None;
        position = null!;
        float bestDistanceSquared = float.MaxValue;
        foreach (var monster in runtime.Monsters.Entities.Values)
        {
            if (!monster.Alive || IsAreaUnsafe(runtime, monster.Area))
            {
                continue;
            }
            float dx = monster.Position.X - bot.Player.Position!.X;
            float dy = monster.Position.Y - bot.Player.Position!.Y;
            float distanceSquared = dx * dx + dy * dy;
            if (distanceSquared >= bestDistanceSquared)
            {
                continue;
            }
            bestDistanceSquared = distanceSquared;
            area = monster.Area;
            position = new Vector3f(monster.Position.X, monster.Position.Y, 0f);
        }

        return position != null;
    }


    private static bool IsEscapeTargetFarEnough(Bot bot, Vector3f target)
    {
        float dx = target.X - bot.Player.Position!.X;
        float dy = target.Y - bot.Player.Position!.Y;
        return dx * dx + dy * dy >=
               SwarmBotMinFleeTargetDistance * SwarmBotMinFleeTargetDistance;
    }


    private bool UpdateWoundedState(MatchRuntime runtime, Bot bot)
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
    public bool CanCutTrail(Bot bot, int healthBefore, DateTime nowUtc, int cutCost)
    {
        if (healthBefore - cutCost <
            Config.MAX_HEALTH * SwarmBotCutMinHealthRatio)
        {
            return false;
        }
        return bot.LastTrailCutAtUtc is not { } lastCutAtUtc ||
               (nowUtc - lastCutAtUtc).TotalSeconds >= SwarmBotCutCooldownSeconds;
    }

    private Vector3f GetTrailChasePosition(MatchRuntime runtime, long targetPlayerId, Vector3f targetPosition)
    {
        var targetPlayer = runtime.GetParticipant(targetPlayerId)!;
        int orbCount = orbTrails.CountOrbs(runtime, targetPlayer);
        if (orbCount <= 0)
        {
            return targetPosition;
        }

        int aimOrdinal = Math.Max(1, orbCount / 2);
        return orbTrails.GetOrbPosition(runtime, targetPlayer, aimOrdinal, targetPosition)
               ?? targetPosition;
    }

    private void FindNearbyThreatAndChaseTarget(
        MatchRuntime runtime,
        Bot bot,
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
            if (distanceSquared >= radiusSquared)
            {
                return;
            }

            float rivalPower = GetOrbPower(runtime, rivalPlayerId);
            if (rivalPower >= myPower * SwarmBotFleePowerRatio && distanceSquared < bestStrongerDistanceSquared)
            {
                bestStrongerDistanceSquared = distanceSquared;
                nearestStronger = position;
            }
            else if (myPower >= rivalPower * SwarmBotChasePowerAdvantage &&
                     distanceSquared < bestWeakerDistanceSquared &&
                     !IsAreaUnsafe(runtime, area))
            {
                bestWeakerDistanceSquared = distanceSquared;
                nearestWeaker = (position, area, rivalPlayerId);
            }
        }

        foreach (var player in runtime.GetAlivePlayers())
        {
            if (player.PlayerId == bot.PlayerId || player.Position == null)
            {
                continue;
            }
            Consider(player.PlayerId, player.Position, player.CurrentArea);
        }

        if (includeMonstersAsStronger)
        {
            foreach (var monster in runtime.Monsters.Entities.Values)
            {
                if (!monster.Alive)
                {
                    continue;
                }
                float dx = monster.Position.X - bot.Player.Position!.X;
                float dy = monster.Position.Y - bot.Player.Position!.Y;
                float distanceSquared = dx * dx + dy * dy;
                if (distanceSquared >= bestStrongerDistanceSquared)
                {
                    continue;
                }
                bestStrongerDistanceSquared = distanceSquared;
                nearestStronger = new Vector3f(monster.Position.X, monster.Position.Y, 0f);
            }
        }

        strongerPosition = nearestStronger;
        weakerRival = nearestWeaker;
    }

    private bool HasMonsterInAttackRange(MatchRuntime runtime, Bot bot)
    {
        float rangeSquared = Config.SWARM_ORB_ATTACK_RANGE * Config.SWARM_ORB_ATTACK_RANGE;
        foreach (var target in runtime.Monsters.GetCombatTargets(DateTime.UtcNow))
        {
            if (target.Area != bot.Player.CurrentArea)
            {
                continue;
            }
            float dx = target.Position.X - bot.Player.Position!.X;
            float dy = target.Position.Y - bot.Player.Position!.Y;
            if (dx * dx + dy * dy <= rangeSquared)
            {
                return true;
            }
        }

        return false;
    }

    public void UpdateSleep(MatchRuntime runtime, IReadOnlyList<Bot> bots, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Bot decisions require the match lock.");
        }
        var sessions = runtime.GetSessions();
        var players = runtime.GetAlivePlayers();
        var monsterTargets = runtime.Monsters.GetCombatTargets(nowUtc);
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
                BotDodgeCalculator.CalculateDodge(runtime.SunCrossfireShapes,
                    player.PlayerId, position, player.CurrentArea, nowUtc) != null;
            foreach (var other in players)
            {
                if (unsafeToSleep)
                {
                    break;
                }
                if (other.PlayerId == player.PlayerId || other.IsEliminated || other.Position == null)
                {
                    continue;
                }
                float dx = other.Position.X - position.X;
                float dy = other.Position.Y - position.Y;
                unsafeToSleep = dx * dx + dy * dy <= safeRadiusSquared;
            }
            foreach (var monster in monsterTargets)
            {
                if (unsafeToSleep)
                {
                    break;
                }
                float dx = monster.Position.X - position.X;
                float dy = monster.Position.Y - position.Y;
                unsafeToSleep = dx * dx + dy * dy <= safeRadiusSquared;
            }
            bool changed = unsafeToSleep || player.Health >= Config.MAX_HEALTH
                ? player.TryStopSleep()
                : player.TryStartSleep(nowUtc);
            if (!changed)
            {
                continue;
            }
            using var packet = PacketMaker.G_TO_C_PLAYER_STATE(player.PlayerId, player.State);
            foreach (var session in sessions)
            {
                if (!session.Player.IsEliminated && session.Player.CurrentArea == player.CurrentArea)
                {
                    session.TrySend(packet);
                }
            }
        }
    }

    private bool TryFindNearestSummonStone(
        MatchRuntime runtime, Bot bot, out Vector3f position)
    {
        position = null!;
        float bestDistanceSquared = float.MaxValue;
        foreach (var item in runtime.GroundItems.GetItemsInArea(bot.Player.CurrentArea))
        {
            if (item.ItemId != Config.SUMMON_STONE_GROUND_ITEM_ID ||
                runtime.GroundItems.WasSpawnedWithin(
                    item.GroundItemUid, BotMovementService.SummonStoneBotReactionDelay))
            {
                continue;
            }

            float dx = item.PositionX - bot.Player.Position!.X;
            float dy = item.PositionY - bot.Player.Position!.Y;
            float distanceSquared = dx * dx + dy * dy;
            if (distanceSquared >= bestDistanceSquared)
            {
                continue;
            }

            bestDistanceSquared = distanceSquared;
            position = new Vector3f(item.PositionX, item.PositionY, 0f);
        }

        return position != null;
    }

    private bool HasMostOrbs(MatchRuntime runtime, long playerId)
    {
        int myOrbCount = runtime.GetOrbs(playerId).GetOrbScore().OrbCount;
        return myOrbCount > 0 && myOrbCount >= growth.GetTopOrbCount(runtime);
    }

    private bool TryGetAlivePlayerPosition(MatchRuntime runtime, long playerId, out Vector3f position)
    {
        position = null!;
        var player = runtime.GetParticipant(playerId);
        if (player == null || player.IsEliminated || player.Position == null)
        {
            return false;
        }

        position = player.Position;
        return true;
    }
    private bool IsAreaUnsafe(MatchRuntime runtime, AreaType area) =>
        runtime.Closures.IsAreaClosed(area) ||
        SwarmPressureField.GetAreaMinDistance(area) >
        runtime.Closures.GetSafeDistance(DateTime.UtcNow);

    private float GetOrbPower(MatchRuntime runtime, long playerId) =>
        runtime.GetOrbs(playerId).GetOrbPower();

}
