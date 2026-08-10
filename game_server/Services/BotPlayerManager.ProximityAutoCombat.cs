using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

public partial class BotPlayerManager
{
    private static readonly TimeSpan BotCombatRepathInterval = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan BotCombatRetreatReturnDelay = TimeSpan.FromSeconds(10);
    private const float BotRetreatCorruptionRatio = 0.70f;
    private const float BotFinishTargetCorruptionRatio = 0.72f;

    public bool TryAutoPickupGroundItem(
        BotPlayerState bot,
        long matchingId,
        InGameInventoryManager inventoryManager,
        GroundItemManager groundItemManager,
        out BotGroundItemPickup? pickup) =>
        TryAutoPickupGroundItem(
            bot,
            matchingId,
            inventoryManager,
            groundItemManager,
            new SummonStoneManager(),
            out pickup);
    /// <summary>
    ///     봇 소환석 반응 지연 (#222): 갓 떨어진 돌은 이 창이 지나야 봇이 반응한다 —
    ///     사람의 눈·조작 시간을 흉내 내 낙수 선점권을 사람에게 준다.
    /// </summary>
    public static readonly TimeSpan SummonStoneBotReactionDelay = TimeSpan.FromSeconds(2.5);

    public bool TryAutoPickupGroundItem(
        BotPlayerState bot,
        long matchingId,
        InGameInventoryManager inventoryManager,
        GroundItemManager groundItemManager,
        SummonStoneManager summonStoneManager,
        out BotGroundItemPickup? pickup)
    {
        pickup = null;
        if (bot.IsEliminated || bot.CurrentArea == AreaType.None)
            return false;

        var inventory = inventoryManager.GetPlayerInventory(matchingId, bot.PlayerId);
        foreach (var candidate in groundItemManager.GetSnapshot(matchingId, bot.CurrentArea)
                     .OrderBy(item => DistanceSquared(bot.Position, item.PositionX, item.PositionY)))
        {
            GroundItemPickupDisposition disposition = GroundItemPickupDisposition.LeaveOnGround;
            int staminaRecovery = 0;
            int corruptionRecovery = 0;
            bool summonStonePickup = candidate.ItemId == Config.SUMMON_STONE_GROUND_ITEM_ID;
            bool jamPickup = candidate.ItemId == Config.JAM_GROUND_ITEM_ID;
            // 재화(소환석·잼)는 봇 반응 지연 공통 — 사람 선점권 (#222)
            if ((summonStonePickup || jamPickup) && groundItemManager.IsYoungerThan(
                    matchingId, candidate.GroundItemUid, SummonStoneBotReactionDelay))
                continue;
            bool canStore = inventory.GetAllItems().Count < Config.GetOrbCapacity();

            long discovererPlayerId = groundItemManager.GetDiscovererPlayerId(
                matchingId, candidate.GroundItemUid);
            var status = groundItemManager.TryClaim(
                matchingId,
                candidate.GroundItemUid,
                bot.PlayerId,
                bot.CurrentArea,
                bot.Position.X,
                bot.Position.Y,
                item =>
                {
                    if (item.ItemId is Config.SUMMON_STONE_GROUND_ITEM_ID or Config.JAM_GROUND_ITEM_ID)
                        return true;

                    disposition = GroundItemPickupPolicy.Resolve(
                        item.ItemId,
                        bot.Stamina,
                        100,
                        bot.Corruption,
                        out staminaRecovery,
                        out corruptionRecovery);
                    return disposition == GroundItemPickupDisposition.AutoUse ||
                           disposition == GroundItemPickupDisposition.Store && canStore;
                },
                out var claimedItem);
            if (status != GroundItemClaimStatus.Success || claimedItem == null)
                continue;

            bool autoUsed = disposition == GroundItemPickupDisposition.AutoUse;
            int requestedRecovery = staminaRecovery + corruptionRecovery;
            int effectiveRecovery = 0;
            InGameItemInfo? addedItem = null;
            SummonStoneSnapshot summonStoneState = default;
            if (jamPickup)
            {
                bot.JamCount += 1;
            }
            else if (summonStonePickup)
            {
                summonStoneState = summonStoneManager.AddStones(matchingId, bot.PlayerId, 1);
            }
            else if (autoUsed)
            {
                int effectiveStaminaRecovery = Math.Min(staminaRecovery, Math.Max(0, 100 - bot.Stamina));
                int effectiveCorruptionRecovery = Math.Min(corruptionRecovery, Math.Max(0, bot.Corruption));
                effectiveRecovery = effectiveStaminaRecovery + effectiveCorruptionRecovery;
                bot.Stamina = Math.Min(100, bot.Stamina + staminaRecovery);
                bot.Corruption = Math.Max(0, bot.Corruption - corruptionRecovery);
            }
            else if (!inventoryManager.TryAddItemWithCapacity(
                         matchingId,
                         bot.PlayerId,
                         claimedItem.ItemId,
                         Config.GetOrbCapacity(),
                         out addedItem))
            {
                _logger.LogWarning(
                    "Bot ground pickup inventory race: MatchingId={MatchingId}, BotId={BotId}, GroundItemUid={GroundItemUid}",
                    matchingId,
                    bot.PlayerId,
                    claimedItem.GroundItemUid);
                return false;
            }

            int autoEquippedItemId = 0;
            if (addedItem != null && inventory.GetEquippedBattleItem()?.ItemUid == addedItem.ItemUid)
            {
                bot.EquippedBattleItemId = addedItem.ItemId;
                autoEquippedItemId = addedItem.ItemId;
            }

            pickup = new BotGroundItemPickup(
                bot.PlayerId,
                claimedItem,
                autoUsed,
                corruptionRecovery,
                discovererPlayerId,
                autoEquippedItemId,
                requestedRecovery,
                effectiveRecovery,
                summonStonePickup ? 1 : 0,
                summonStonePickup ? summonStoneState.StoneCount : 0);
            return true;
        }

        return false;
    }

    public void UpdateCombatMovementIntent(
        BotPlayerState bot,
        long matchingId,
        AreaClosureManager closureManager,
        IReadOnlyCollection<BotCombatTargetSnapshot> combatTargets)
    {
        if (bot.IsEliminated || bot.IsInInteraction || bot.PendingRngInteractId != 0 ||
            bot.PendingChecklistTaskId != 0 ||
            DateTime.UtcNow < bot.NextCombatRepathAt)
        {
            return;
        }

        // Evacuation takes priority over combat. Outside evacuation windows,
        // corridor combat remains available during Survivor Royale phases.
        if (bot.EvacuationDestination != AreaType.None ||
            (IsAreaClosingOrClosed(closureManager, matchingId, bot.CurrentArea) &&
             HasOpenNonCorridorRefuge(matchingId, closureManager)))
        {
            return;
        }

        var ownSnapshot = combatTargets.FirstOrDefault(target => target.PlayerId == bot.PlayerId);
        var ownCombatData = BattleItemCombatData.Get(ownSnapshot.WeaponItemId);
        if (ownCombatData == null)
            return;

        var nearest = combatTargets
            .Where(target => target.PlayerId != bot.PlayerId && target.Area == bot.CurrentArea)
            .OrderBy(target => DistanceSquared(bot.Position, target.Position.X, target.Position.Y))
            .FirstOrDefault();
        if (nearest.PlayerId == 0)
            return;

        float distance = MathF.Sqrt(DistanceSquared(bot.Position, nearest.Position.X, nearest.Position.Y));
        var targetCombatData = BattleItemCombatData.Get(nearest.WeaponItemId);
        bool retreat = targetCombatData != null &&
                       (targetCombatData.Tier > ownCombatData.Tier ||
                        targetCombatData.Tier == ownCombatData.Tier &&
                        targetCombatData.AttackRange > ownCombatData.AttackRange);
        float preferredDistance = ownCombatData.AttackRange * 0.72f;

        bool survivalRisk = bot.Corruption >= Config.SURVIVOR_MAX_CORRUPTION * BotRetreatCorruptionRatio;
        bool targetNearElimination = nearest.Corruption >=
                                     Config.SURVIVOR_MAX_CORRUPTION * BotFinishTargetCorruptionRatio;

        // A planned loot route is the default. Immediate survival and a visible
        // finishing opportunity are the only combat reasons to abandon it.
        if (bot.MovementDestination != AreaType.None && !survivalRisk && !targetNearElimination)
            return;

        if (survivalRisk || targetNearElimination)
        {
            bot.MovementDestination = AreaType.None;
            bot.Path.Clear();
            bot.PathIndex = 0;
        }

        if ((retreat || survivalRisk) && distance < Math.Max(1.4f, preferredDistance))
            TryStartCombatRetreatPath(bot, matchingId, closureManager, nearest.Position, nearest.Area);
        else if (targetNearElimination && !retreat && !survivalRisk && distance > preferredDistance)
            TryStartCombatApproachPath(bot, matchingId, closureManager, nearest.Position);

        bot.NextCombatRepathAt = DateTime.UtcNow.Add(BotCombatRepathInterval);
    }

    private bool TryStartCombatApproachPath(
        BotPlayerState bot,
        long matchingId,
        AreaClosureManager closureManager,
        Vector3f targetPosition)
    {
        bool hasOpenRefuge = HasOpenNonCorridorRefuge(matchingId, closureManager);
        var path = BotPathfinder.FindPath(
            GetMatchingMapId(matchingId),
            bot.CurrentArea,
            bot.Cell,
            bot.CurrentArea,
            WorldToCell(targetPosition),
            area => area != bot.CurrentArea && hasOpenRefuge &&
                    IsAreaClosingOrClosed(closureManager, matchingId, area));
        if (path == null || path.Count == 0)
            return false;

        bot.Path = path;
        bot.PathIndex = 0;
        bot.LoopWaitUntil = DateTime.MinValue;
        return true;
    }

    private bool TryStartCombatRetreatPath(
        BotPlayerState bot,
        long matchingId,
        AreaClosureManager closureManager,
        Vector3f threatPosition,
        AreaType threatArea)
    {
        var mapId = GetMatchingMapId(matchingId);
        var closure = closureManager.GetClientStateSnapshot(matchingId);
        var unavailableAreas = closure.ClosedAreas.Concat(closure.WarningAreas).ToHashSet();
        // 잠긴 방을 빼지 않으면 후퇴 경로가 잠긴 문 앞에서 걸음 게이트에 잘려,
        // 방 탈출도 못 하고 아래 지역 내 도주 폴백에도 못 가는 문가 정지가 된다.
        unavailableAreas.UnionWith(GetLockedRoomAreas(matchingId));
        var escape = GameMapData.GetAreas(mapId)
            .Select(region => region.AreaType)
            .Distinct()
            .Where(area => area != AreaType.None && !area.IsCorridor() &&
                           area != bot.CurrentArea && area != threatArea &&
                           !unavailableAreas.Contains(area))
            .Select(area => new
            {
                Area = area,
                Path = BotPathfinder.FindPath(
                    mapId, bot.CurrentArea, bot.Cell, area,
                    GameAreaConnectionData.GetSpawnCell(mapId, bot.CurrentArea, area)
                    ?? GameMapData.GetAreaSpawnCell(mapId, area),
                    unavailableAreas.Contains)
            })
            .Where(entry => entry.Path is { Count: > 0 })
            .OrderBy(entry => CountAreaPressure(matchingId, entry.Area))
            .ThenBy(entry => entry.Area == AreaType.Ground ? 1 : 0)
            .ThenBy(entry => entry.Path!.Count)
            .FirstOrDefault();
        if (escape?.Path != null)
        {
            // A low-health retreat must commit to one room. Replanning while the bot is
            // still on the doorway makes it alternate between adjacent exits.
            bot.RecentCombatRetreatOrigin = bot.CurrentArea;
            bot.CombatRetreatOriginBlockedUntil = DateTime.UtcNow.Add(BotCombatRetreatReturnDelay);
            bot.EvacuationDestination = escape.Area;
            bot.Path = escape.Path;
            bot.PathIndex = 0;
            bot.LoopWaitUntil = DateTime.MinValue;
            return true;
        }
        int directionX = Math.Sign(bot.Position.X - threatPosition.X);
        int directionY = Math.Sign(bot.Position.Y - threatPosition.Y);
        if (directionX == 0 && directionY == 0)
            directionX = bot.PlayerId % 2 == 0 ? 1 : -1;

        var candidates = new[]
        {
            new Cell(bot.Cell.X + directionX * 3, bot.Cell.Y + directionY * 3),
            new Cell(bot.Cell.X + directionX * 2, bot.Cell.Y + directionY * 2),
            new Cell(bot.Cell.X + directionX, bot.Cell.Y + directionY)
        };
        foreach (var candidate in candidates)
        {
            if (GameMapData.GetCurrentArea(mapId, candidate) != bot.CurrentArea ||
                !GameMapData.IsMoveablePosition(mapId, candidate))
            {
                continue;
            }

            bool hasOpenRefuge = HasOpenNonCorridorRefuge(matchingId, closureManager);
            var path = BotPathfinder.FindPath(
                mapId,
                bot.CurrentArea,
                bot.Cell,
                bot.CurrentArea,
                candidate,
                area => area != bot.CurrentArea && hasOpenRefuge &&
                        IsAreaClosingOrClosed(closureManager, matchingId, area));
            if (path == null || path.Count == 0)
                continue;

            bot.Path = path;
            bot.PathIndex = 0;
            bot.LoopWaitUntil = DateTime.MinValue;
            return true;
        }

        return false;
    }

    private static float DistanceSquared(Vector3f position, float x, float y)
    {
        float dx = position.X - x;
        float dy = position.Y - y;
        return dx * dx + dy * dy;
    }

    private static Cell WorldToCell(Vector3f position) =>
        new((int)MathF.Floor(position.X + 2f * position.Y),
            (int)MathF.Floor(2f * position.Y - position.X));
    public void ApplyProximityAutoCombatDamage(BotPlayerState bot, int damage, long attackerPlayerId = 0)
    {
        if (bot.IsEliminated || damage <= 0)
            return;

        int previousCorruption = bot.Corruption;
        bot.Corruption = Math.Clamp(bot.Corruption + damage, 0, Config.SURVIVOR_MAX_CORRUPTION);
        if (previousCorruption < Config.SURVIVOR_MAX_CORRUPTION && bot.Corruption >= Config.SURVIVOR_MAX_CORRUPTION)
            bot.LastProximityAttackerPlayerId = attackerPlayerId;
    }

    public bool TryFinalizeProximityAutoCombatElimination(BotPlayerState bot, long matchingId)
    {
        if (bot.IsEliminated || bot.Corruption < Config.SURVIVOR_MAX_CORRUPTION)
            return false;

        bot.IsEliminated = true;
        bot.IsForcedFollowActive = false;
        bot.Path.Clear();
        bot.PathIndex = 0;
        bot.PendingRngInteractId = 0;
        bot.PendingChecklistTaskId = 0;
        bot.PendingChecklistInteractId = 0;
        bot.ChecklistActivityProgressStartTime = DateTime.MinValue;
        bot.RngCollectProgressStartTime = DateTime.MinValue;
        ClearBotRoomExplorePlan(bot);
        bot.LoopWaitUntil = DateTime.MinValue;

        _logger.LogInformation(
            "Bot eliminated by proximity auto combat: MatchingId={MatchingId}, BotId={BotId}, Corruption={Corruption}",
            matchingId,
            bot.PlayerId,
            bot.Corruption);
        return true;
    }
}
public readonly record struct BotGroundItemPickup(
    long BotPlayerId,
    GroundItemInfo Item,
    bool AutoUsed,
    int CorruptionRecovery,
    long DiscovererPlayerId,
    int AutoEquippedItemId = 0,
    int RequestedRecovery = 0,
    int EffectiveRecovery = 0,
    int SummonStoneAmount = 0,
    int SummonStoneBalance = 0);

public readonly record struct BotCombatTargetSnapshot(
    long PlayerId,
    AreaType Area,
    Vector3f Position,
    int WeaponItemId,
    int Corruption = 0);
