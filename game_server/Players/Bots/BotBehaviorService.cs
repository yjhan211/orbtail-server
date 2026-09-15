using game_server.matches;
using game_server.sessions;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;
using network.packets;

namespace game_server.players.bots;

/// <summary>
///     봇의 대피·반격·사냥·아이템 회수 방향을 결정하고, 문 열기·수면·오브 성장을 공통 Player 규칙으로 실행한다.
///     기억과 재사용 대기 시간은 매치가 소유하며 호출자는 매치 잠금을 보유한다.
///     이동 목표만 선택하며 경로 계획과 위치 갱신은 MatchMoveService가 담당한다.
///     MatchMoveService가 이동 명령을 실행하고 결과를 전송한다.
/// </summary>
internal class BotBehaviorService(
    PlayerOrbGrowthService growth,
    PlayerInteractionService interactions,
    ILogger<BotBehaviorService> logger)
{
    public void CompleteMovement(MatchRuntime runtime, Bot bot)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
            throw new InvalidOperationException("Bot movement completion requires the match lock.");
        var player = bot.Player;
        var velocity = player.GameInfo.ObjectInfo.Velocity;
        if (player.State == PlayerState.EXPLORE_1 && (velocity.X != 0f || velocity.Y != 0f))
        {
            player.ClearPendingInteractions();
            player.State = PlayerState.IDLE;
        }
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

    private bool TryUpgradePreferredOrb(MatchRuntime runtime, long playerId)
    {
        var player = runtime.GetParticipant(playerId)!;
        var distinctGroupIds = new List<int>();
        var countByGroupId = new Dictionary<int, int>();
        foreach (var item in player.Orbs.GetOrderedOrbs())
        {
            if (!OrbData.TryGetOrbGroupAndTier(item.ItemId, out int orbGroupId, out _) || orbGroupId == 0)
            {
                continue;
            }
            if (!countByGroupId.TryGetValue(orbGroupId, out int count))
            {
                distinctGroupIds.Add(orbGroupId);
            }
            countByGroupId[orbGroupId] = count + 1;
        }

        int preferredGroupId = 0;
        int preferredCount = 0;
        foreach (int orbGroupId in distinctGroupIds)
        {
            if (countByGroupId[orbGroupId] > preferredCount)
            {
                preferredGroupId = orbGroupId;
                preferredCount = countByGroupId[orbGroupId];
            }
        }

        if (preferredGroupId == 0)
        {
            return false;
        }

        if (growth.GetUpgradeCost(runtime, player, preferredGroupId) <= 0)
        {
            preferredGroupId = 0;
            foreach (int orbGroupId in distinctGroupIds)
            {
                if (growth.GetUpgradeCost(runtime, player, orbGroupId) > 0)
                {
                    preferredGroupId = orbGroupId;
                    break;
                }
            }
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

    public void ProcessDoorInteractions(MatchRuntime runtime, List<Bot> bots, List<GameClientSession> sessions, DateTime nowUtc)
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
            var previousState = player.State;
            if (player.PendingDoorInteractionId is { } interactId)
            {
                int doorId = GameInteractableData.Get(interactId)?.DoorId ?? 0;
                if (interactions.TryFinishDoor(runtime, player, interactId, doorId, now, out var error))
                {
                    using var openPacket = PacketMaker.G_TO_C_DOOR_STATE_UPDATE(doorId, true, ErrorCode.SUCCESS, bot.PlayerId);
                    foreach (var session in sessions)
                    {
                        session.TrySend(openPacket);
                    }
                    logger.LogInformation("Swarm bot unlocked door: MatchingId={MatchingId}, BotId={BotId}, DoorId={DoorId}", runtime.MatchingId, bot.PlayerId, doorId);
                    player.State = PlayerState.IDLE;
                }
                else if (error != ErrorCode.DOOR_OPEN_TOO_EARLY)
                {
                    interactions.CancelPendingInteractions(runtime, player);
                    player.State = PlayerState.IDLE;
                }
            }
            else if (player.State == PlayerState.EXPLORE_1)
            {
                player.State = PlayerState.IDLE;
            }
            else if (player.Velocity.X == 0f && player.Velocity.Y == 0f && TryFindDoorAtCurrentCell(runtime, bot, out var target) && interactions.StartDoor(runtime, player, target.Id, target.DoorId, now) == ErrorCode.SUCCESS)
            {
                player.State = PlayerState.EXPLORE_1;
            }

            if (player.State == previousState)
            {
                continue;
            }
            using var statePacket = PacketMaker.G_TO_C_PLAYER_STATE(player.PlayerId, player.State);
            foreach (var session in sessions)
            {
                if (!session.Player.IsEliminated && session.Player.CurrentArea == player.CurrentArea)
                {
                    session.TrySend(statePacket);
                }
            }
        }
    }

    private bool TryFindDoorAtCurrentCell(MatchRuntime runtime, Bot bot, out InteractableInfoData target)
    {
        var cell = bot.Player.Cell;
        foreach (var info in GameInteractableData.GetAll())
        {
            if (info.DoorId <= 0 || GameDoorData.Get(info.DoorId) == null || runtime.Doors.IsDoorOpen(info.DoorId) || info.ZoneId != (int)bot.Player.CurrentArea || cell.X != info.CellX || cell.Y != info.CellY)
            {
                continue;
            }
            target = info;
            return true;
        }
        target = null!;
        return false;
    }

    public virtual void SelectMovementTarget(MatchRuntime runtime, Bot bot)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Bot decisions require the match lock.");
        }
        var nowUtc = DateTime.UtcNow;

        // 자기장 대피
        if (TrySelectFieldEvacuationTarget(runtime, bot, nowUtc))
        {
            return;
        }
        // 주변 몬스터 회피
        if (TrySelectMonsterAvoidanceTarget(runtime, bot, nowUtc))
        {
            return;
        }
        // 전력 또는 피격 상황에 따른 도주
        if (TrySelectEscapeTarget(runtime, bot, nowUtc))
        {
            return;
        }
        // 소환석 획득
        if (TrySelectSummonStoneTarget(runtime, bot))
        {
            return;
        }
        // 최근 공격자에게 반격
        if (TrySelectRetaliationTarget(runtime, bot, nowUtc))
        {
            return;
        }
        // 몬스터 사냥 이동
        if (TrySelectMonsterHuntTarget(runtime, bot))
        {
            return;
        }
        // 안전하게 이동할 수 있는 주변 셀 배회
        SelectWanderTarget(runtime, bot, nowUtc);
    }

    private bool TrySelectFieldEvacuationTarget(MatchRuntime runtime, Bot bot, DateTime nowUtc)
    {
        double safeDistance = runtime.Closures.GetSafeDistance(nowUtc);
        if (safeDistance >= double.MaxValue)
        {
            return false;
        }
        var currentCell = bot.Player.Cell!;
        double margin = Config.SWARM_BOT_FIELD_EVACUATE_MARGIN_CELLS;
        if (SwarmPressureField.GetDistance(currentCell) < safeDistance - margin)
        {
            return false;
        }
        bot.MonsterAvoidanceTarget = null;
        double retreatThreshold = safeDistance - margin * 2;
        Cell? retreatCell = null;
        int nearestDistance = int.MaxValue;
        foreach (var entry in SwarmPressureField.GetAreaCellsByDistance(bot.Player.CurrentArea))
        {
            if (entry.Distance > retreatThreshold)
            {
                break;
            }
            int distance = currentCell.GetDistance(entry.Cell);
            if (distance >= nearestDistance)
            {
                continue;
            }
            nearestDistance = distance;
            retreatCell = entry.Cell;
        }
        if (retreatCell != null)
        {
            bot.SetMovementTarget(bot.Player.CurrentArea, retreatCell);
            return true;
        }

        // 안전 후보가 없을 때의 중앙 이동은 안전을 보장하는 목적지가 아니라 최후의 대안이다.
        var bestArea = AreaType.S2Corridor9;
        var bestCell = GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, bestArea);
        nearestDistance = int.MaxValue;
        foreach (var region in GameMapData.GetAreas(Config.SWARM_MATCH_MAP))
        {
            if (region.AreaType == AreaType.None)
            {
                continue;
            }
            var cells = SwarmPressureField.GetAreaCellsByDistance(region.AreaType);
            if (cells.Count == 0 || cells[0].Distance > retreatThreshold)
            {
                continue;
            }
            int distance = currentCell.GetDistance(cells[0].Cell);
            if (distance >= nearestDistance)
            {
                continue;
            }
            nearestDistance = distance;
            bestArea = region.AreaType;
            bestCell = cells[0].Cell;
        }
        bot.SetMovementTarget(bestArea, bestCell);
        return true;
    }

    private static bool TrySelectMonsterAvoidanceTarget(MatchRuntime runtime, Bot bot, DateTime nowUtc)
    {
        var currentCell = bot.Player.Cell!;
        var area = bot.Player.CurrentArea;
        long threatCellSumX = 0, threatCellSumY = 0;
        int threatCount = 0;
        foreach (var monster in runtime.Monsters.Entities.Values)
        {
            if (!monster.Alive || monster.Area != area || !monster.Aggro)
            {
                continue;
            }
            var monsterCell = monster.Info.ObjectInfo.Cell;
            if (currentCell.GetDistance(monsterCell) > Config.SWARM_BOT_MONSTER_DANGER_RADIUS_CELLS)
            {
                continue;
            }
            threatCellSumX += monsterCell.X;
            threatCellSumY += monsterCell.Y;
            threatCount++;
        }
        if (threatCount == 0)
        {
            bot.MonsterAvoidanceTarget = null;
            return false;
        }
        if (bot.MonsterAvoidanceTarget.HasValue)
        {
            var previous = bot.MonsterAvoidanceTarget.Value;
            bool committed = (nowUtc - previous.SelectedAtUtc).TotalSeconds < Config.SWARM_BOT_FLEE_COMMIT_SECONDS;
            if (committed && currentCell.GetDistance(previous.Destination) > 2)
            {
                bot.SetMovementTarget(area, previous.Destination);
                return true;
            }
        }
        var threatCell = new Cell((int)Math.Round(threatCellSumX / (double)threatCount), (int)Math.Round(threatCellSumY / (double)threatCount));
        SelectThreatEscapeTarget(runtime, bot, threatCell, nowUtc, stayInCurrentArea: true);
        bot.MonsterAvoidanceTarget = (bot.Movement.DestinationCell!.Clone(), nowUtc);
        return true;
    }

    private bool TrySelectEscapeTarget(MatchRuntime runtime, Bot bot, DateTime nowUtc)
    {
        float orbPower = bot.Player.Orbs.GetOrbPower();
        float myPower = bot.Wounded ? 0f : orbPower;
        int nearestDistance = Config.SWARM_BOT_RIVAL_SCAN_RADIUS_CELLS;
        Cell? threatCell = null;
        foreach (var player in runtime.GetAlivePlayers())
        {
            if (player.PlayerId == bot.PlayerId || player.Cell == null || player.Orbs.GetOrbPower() < myPower * Config.SWARM_BOT_FLEE_POWER_RATIO)
            {
                continue;
            }
            int distance = bot.Player.Cell!.GetDistance(player.Cell);
            if (distance >= nearestDistance)
            {
                continue;
            }
            nearestDistance = distance;
            threatCell = player.Cell;
        }
        if (!bot.Player.Orbs.HasAnyOrb())
        {
            foreach (var monster in runtime.Monsters.Entities.Values)
            {
                if (!monster.Alive)
                {
                    continue;
                }
                var cell = monster.Info.ObjectInfo.Cell;
                int distance = bot.Player.Cell!.GetDistance(cell);
                if (distance >= nearestDistance)
                {
                    continue;
                }
                nearestDistance = distance;
                threatCell = cell;
            }
        }
        if (threatCell == null && (nowUtc - bot.LastDamagedAtUtc).TotalSeconds <= Config.SWARM_BOT_DAMAGED_FLEE_SECONDS)
        {
            var attacker = runtime.GetParticipant(bot.LastProximityAttackerPlayerId);
            if (attacker != null && attacker.Cell != null && !attacker.IsEliminated && (bot.Wounded || attacker.Orbs.GetOrbPower() >= orbPower * Config.SWARM_BOT_FLEE_POWER_RATIO))
            {
                threatCell = attacker.Cell;
            }
        }
        if (threatCell == null)
        {
            return false;
        }
        bot.MonsterAvoidanceTarget = null;
        SelectThreatEscapeTarget(runtime, bot, threatCell, nowUtc, stayInCurrentArea: false);
        return true;
    }

    private bool TrySelectRetaliationTarget(MatchRuntime runtime, Bot bot, DateTime nowUtc)
    {
        if (bot.Wounded || (nowUtc - bot.LastDamagedAtUtc).TotalSeconds > Config.SWARM_BOT_DAMAGED_FLEE_SECONDS)
        {
            return false;
        }
        var attacker = runtime.GetParticipant(bot.LastProximityAttackerPlayerId);
        if (attacker == null || attacker.Cell == null || attacker.IsEliminated || attacker.PlayerId == bot.PlayerId)
        {
            return false;
        }
        if (attacker.Orbs.GetOrbPower() >= bot.Player.Orbs.GetOrbPower() * Config.SWARM_BOT_FLEE_POWER_RATIO)
        {
            return false;
        }
        if (bot.Player.Cell!.GetDistance(attacker.Cell) <= 3)
        {
            return false;
        }
        var area = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, attacker.Cell);
        if (area == AreaType.None || !GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, attacker.Cell))
        {
            return false;
        }
        bot.SetMovementTarget(area, attacker.Cell);
        return true;
    }

    private bool TrySelectSummonStoneTarget(MatchRuntime runtime, Bot bot)
    {
        Cell? cell = null;
        int nearestDistance = int.MaxValue;
        foreach (var item in runtime.GroundItems.GetItemsInArea(bot.Player.CurrentArea))
        {
            if (item.ItemId != Config.SUMMON_STONE_GROUND_ITEM_ID)
            {
                continue;
            }
            var candidate = item.ObjectInfo.Cell;
            int distance = bot.Player.Cell!.GetDistance(candidate);
            if (distance >= nearestDistance)
            {
                continue;
            }
            nearestDistance = distance;
            cell = candidate;
        }
        if (cell == null)
        {
            return false;
        }
        bot.SetMovementTarget(bot.Player.CurrentArea, cell);
        return true;
    }

    internal static void SelectWanderTarget(MatchRuntime runtime, Bot bot, DateTime nowUtc)
    {
        var mapId = Config.SWARM_MATCH_MAP;
        var currentArea = bot.Player.CurrentArea;
        var currentCell = bot.Player.Cell!;
        double safeDistance = runtime.Closures.GetSafeDistance(nowUtc);
        var candidates = new List<(AreaType Area, Cell Cell)>();
        if (bot.ExplorationTarget is { } previous)
        {
            if (currentArea == previous.Area && currentCell.GetDistance(previous.Cell) > 1)
            {
                candidates.Add(previous);
            }
        }
        bot.ExplorationTarget = null;

        int radius = Config.SWARM_BOT_MONSTER_ROAM_DISTANCE_CELLS;
        int minimumDistance = Math.Max(2, radius / 2);
        var nearbyCells = new List<Cell>();
        for (int x = currentCell.X - radius; x <= currentCell.X + radius; x++)
        {
            for (int y = currentCell.Y - radius; y <= currentCell.Y + radius; y++)
            {
                var cell = new Cell(x, y);
                if (currentCell.GetDistance(cell) < minimumDistance)
                {
                    continue;
                }
                if (GameMapData.GetCurrentArea(mapId, cell) != currentArea)
                {
                    continue;
                }
                if (!GameMapData.IsMoveablePosition(mapId, cell))
                {
                    continue;
                }
                nearbyCells.Add(cell);
            }
        }

        while (nearbyCells.Count > 0)
        {
            int index = Random.Shared.Next(nearbyCells.Count);
            candidates.Add((currentArea, nearbyCells[index]));
            nearbyCells.RemoveAt(index);
        }

        var targetCells = new List<Cell>();
        foreach (var candidate in candidates)
        {
            targetCells.Add(candidate.Cell);
        }
        bool found = MatchMoveService.TrySelectReachableCell(
            runtime, bot.Player.GameInfo.ObjectInfo, targetCells,
            cell => SwarmPressureField.GetDistance(cell) <= safeDistance,
            area => runtime.Closures.IsAreaClosed(area), out var targetCell);
        if (found)
        {
            bot.ExplorationTarget = (currentArea, targetCell.Clone());
            bot.SetMovementTarget(currentArea, targetCell);
            return;
        }
        bot.SetMovementTarget(currentArea, currentCell);
    }

    private bool TrySelectMonsterHuntTarget(MatchRuntime runtime, Bot bot)
    {
        if (!bot.Player.Orbs.HasAnyOrb())
        {
            return false;
        }
        int distanceCells = Config.SWARM_BOT_MONSTER_HUNT_STOP_DISTANCE_CELLS;
        foreach (var monster in runtime.Monsters.GetCombatTargets())
        {
            if (monster.Area != bot.Player.CurrentArea)
            {
                continue;
            }
            if (bot.Player.Cell!.GetDistance(monster.Info.ObjectInfo.Cell) > distanceCells)
            {
                continue;
            }
            bot.SetMovementTarget(bot.Player.CurrentArea, bot.Player.Cell!);
            return true;
        }
        var area = AreaType.None;
        Cell? cell = null;
        int nearestDistance = int.MaxValue;
        foreach (var monster in runtime.Monsters.Entities.Values)
        {
            if (!monster.Alive || runtime.Closures.IsAreaUnsafe(monster.Area))
            {
                continue;
            }
            var candidate = monster.Info.ObjectInfo.Cell;
            int distance = bot.Player.Cell!.GetDistance(candidate);
            if (distance >= nearestDistance)
            {
                continue;
            }
            nearestDistance = distance;
            area = monster.Area;
            cell = candidate;
        }
        if (cell == null)
        {
            return false;
        }
        bot.SetMovementTarget(area, cell);
        return true;
    }

    internal static void SelectThreatEscapeTarget(MatchRuntime runtime, Bot bot, Cell threatCell, DateTime nowUtc, bool stayInCurrentArea)
    {
        var mapId = Config.SWARM_MATCH_MAP;
        var currentCell = bot.Player.Cell!;
        var currentArea = bot.Player.CurrentArea;
        int currentThreatDistance = currentCell.GetDistance(threatCell);
        int radius = stayInCurrentArea ? Config.SWARM_BOT_MONSTER_FLEE_DISTANCE_CELLS : Config.SWARM_BOT_FLEE_PROBE_DISTANCE_CELLS;
        int minimumDistance = stayInCurrentArea ? Config.SWARM_BOT_MIN_THREAT_FLEE_DISTANCE_CELLS : Config.SWARM_BOT_MIN_FLEE_TARGET_DISTANCE_CELLS;
        var candidates = new List<Cell>();
        for (int x = currentCell.X - radius; x <= currentCell.X + radius; x++)
        {
            for (int y = currentCell.Y - radius; y <= currentCell.Y + radius; y++)
            {
                candidates.Add(new Cell(x, y));
            }
        }
        candidates.Sort((left, right) => right.GetDistance(threatCell).CompareTo(left.GetDistance(threatCell)));

        double safeDistance = double.MaxValue;
        if (!stayInCurrentArea)
        {
            safeDistance = runtime.Closures.GetSafeDistance(nowUtc);
        }
        var targetCells = new List<Cell>();
        foreach (var cell in candidates)
        {
            var area = GameMapData.GetCurrentArea(mapId, cell);
            if (area == AreaType.None)
            {
                continue;
            }
            if (!GameMapData.IsMoveablePosition(mapId, cell))
            {
                continue;
            }
            if (currentCell.GetDistance(cell) < minimumDistance)
            {
                continue;
            }
            if (cell.GetDistance(threatCell) <= currentThreatDistance)
            {
                continue;
            }

            if (stayInCurrentArea)
            {
                if (area != currentArea)
                {
                    continue;
                }
            }
            else if (runtime.Closures.IsAreaUnsafe(area) || SwarmPressureField.GetDistance(cell) > safeDistance)
            {
                continue;
            }

            targetCells.Add(cell);
        }
        bool found = MatchMoveService.TrySelectReachableCell(
            runtime, bot.Player.GameInfo.ObjectInfo, targetCells,
            cell => stayInCurrentArea ||
                (SwarmPressureField.GetDistance(cell) <= safeDistance &&
                 cell.GetDistance(threatCell) >= currentThreatDistance),
            area => !stayInCurrentArea && runtime.Closures.IsAreaUnsafe(area),
            out var targetCell);
        if (found)
        {
            var targetArea = GameMapData.GetCurrentArea(mapId, targetCell);
            bot.SetMovementTarget(targetArea, targetCell);
            return;
        }
        bot.SetMovementTarget(currentArea, currentCell);
    }

    public bool CanCutTrail(Bot bot, int healthBefore, DateTime nowUtc, int cutCost)
    {
        if (healthBefore - cutCost < Config.MAX_HEALTH * Config.SWARM_BOT_CUT_MIN_HEALTH_RATIO)
        {
            return false;
        }
        return bot.LastTrailCutAtUtc is not { } lastCutAtUtc || (nowUtc - lastCutAtUtc).TotalSeconds >= Config.SWARM_BOT_CUT_COOLDOWN_SECONDS;
    }

    public void UpdateSleep(MatchRuntime runtime, IReadOnlyList<Bot> bots, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Bot decisions require the match lock.");
        }
        var sessions = runtime.GetSessions();
        var players = runtime.GetAlivePlayers();
        var monsterTargets = runtime.Monsters.GetCombatTargets();
        float safeRadiusSquared = Config.SWARM_ORB_ATTACK_RANGE * Config.SWARM_ORB_ATTACK_RANGE;
        foreach (var bot in bots)
        {
            var player = bot.Player;
            bool unsafeToSleep = IsUnsafeToSleep(runtime, bot, players, monsterTargets, safeRadiusSquared, nowUtc);
            bool hasSummonStone = false;
            foreach (var item in runtime.GroundItems.GetItemsInArea(player.CurrentArea))
            {
                if (item.ItemId != Config.SUMMON_STONE_GROUND_ITEM_ID)
                {
                    continue;
                }
                hasSummonStone = true;
                break;
            }
            bool changed = unsafeToSleep || hasSummonStone || player.Health >= Config.MAX_HEALTH ? player.TryStopSleep() : player.TryStartSleep(nowUtc);
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

    private static bool IsUnsafeToSleep(MatchRuntime runtime, Bot bot, IReadOnlyList<Player> players, IReadOnlyList<matches.monsters.Monster> monsterTargets, float safeRadiusSquared, DateTime nowUtc)
    {
        var player = bot.Player;
        var position = player.Position!;

        if (player.IsEliminated || player.Health <= 0 || !player.CanSleep(nowUtc) || player.PendingDoorInteractionId.HasValue)
        {
            return true;
        }

        if (player.CurrentArea == AreaType.None || runtime.Closures.IsAreaClosed(player.CurrentArea))
        {
            return true;
        }
        if (MatchFieldService.GetDamagePerTick(runtime, position, nowUtc) > 0)
        {
            return true;
        }

        if (nowUtc < bot.SwarmDodgeHoldUntilUtc)
        {
            return true;
        }
        if (BotDodgeCalculator.CalculateDodge(runtime.SunCrossfireShapes, player.PlayerId, position, player.CurrentArea, nowUtc) != null)
        {
            return true;
        }

        foreach (var other in players)
        {
            if (other.PlayerId == player.PlayerId || other.IsEliminated || other.Position == null)
            {
                continue;
            }
            float dx = other.Position.X - position.X;
            float dy = other.Position.Y - position.Y;
            if (dx * dx + dy * dy <= safeRadiusSquared)
            {
                return true;
            }
        }
        foreach (var monster in monsterTargets)
        {
            float dx = monster.Position.X - position.X;
            float dy = monster.Position.Y - position.Y;
            if (dx * dx + dy * dy <= safeRadiusSquared)
            {
                return true;
            }
        }

        return false;
    }

    internal static float GetBotMovementSpeedMultiplier(Bot bot, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var orbs = bot.Player.Orbs.GetAllItems();
        bool bootsActive = now < bot.BootsSpeedUntilUtc;
        bool bareSpeedActive = !bot.Player.Orbs.HasAnyOrb() && now < bot.SwarmBareSpeedUntilUtc;
        bool waveSlowActive = now < bot.Player.WaveSlowUntilUtc;
        return MovementSpeed.GetMultiplier(orbs, bootsActive, bareSpeedActive, waveSlowActive);
    }

    public virtual MovementRequest CreateMovementRequest(MatchRuntime runtime, Bot bot, DateTime now)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Bot decisions require the match lock.");
        }
        var movement = bot.Movement;
        if (runtime.IsEnded || bot.Player.IsEliminated || bot.Player.IsSleeping)
        {
            return new MovementRequest(null, 0f, HoldPosition: true);
        }
        var previousDestination = movement.DestinationCell;
        var previousArea = movement.DestinationArea;
        movement.DestinationCell = null;
        movement.DestinationArea = AreaType.None;
        SelectMovementTarget(runtime, bot);
        var requestedCell = movement.DestinationCell;
        movement.DestinationCell = previousDestination;
        movement.DestinationArea = previousArea;
        float speed = Config.SWARM_BOT_WALK_SPEED * GetBotMovementSpeedMultiplier(bot, now);
        var advice = BotDodgeCalculator.CalculateDodge(runtime.SunCrossfireShapes,
            bot.PlayerId, bot.Player.Position!, bot.Player.CurrentArea, now);
        if (advice != null || now < bot.SwarmDodgeHoldUntilUtc)
        {
            if (BotDodgeCalculator.TrySelectDodgeCell(runtime, bot, now, out var dodgeCell))
            {
                bot.DodgeTargetCell = dodgeCell.Clone();
                if (advice != null)
                {
                    bot.SwarmDodgeHoldUntilUtc = now.AddSeconds(advice.Value.HoldSeconds);
                }
                return new MovementRequest(dodgeCell, speed);
            }
            bot.DodgeTargetCell = null;
            bot.SwarmDodgeHoldUntilUtc = DateTime.MinValue;
            // 안전한 경로를 찾지 못하면 검증되지 않은 방향으로 움직이지 않는다.
            return new MovementRequest(null, 0f, HoldPosition: true);
        }
        bot.DodgeTargetCell = null;
        if (requestedCell == null || requestedCell.Equals(bot.Player.Cell))
        {
            return new MovementRequest(requestedCell, 0f, HoldPosition: true);
        }
        return new MovementRequest(requestedCell.Clone(), speed, HoldPosition: now < bot.LoopWaitUntil);
    }
}
