using game_server.sessions;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

public partial class BotPlayerManager
{
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

        var inventory = inventoryManager.GetPlayerInventory(bot.PlayerId);
        foreach (var candidate in groundItemManager.GetSnapshot(bot.CurrentArea)
                     .OrderBy(item => DistanceSquared(bot.Position, item.PositionX, item.PositionY)))
        {
            GroundItemPickupDisposition disposition = GroundItemPickupDisposition.LeaveOnGround;
            int staminaRecovery = 0;
            int corruptionRecovery = 0;
            bool summonStonePickup = candidate.ItemId == Config.SUMMON_STONE_GROUND_ITEM_ID;
            bool jamPickup = candidate.ItemId == Config.JAM_GROUND_ITEM_ID;
            bool bootsPickup = candidate.ItemId == Config.BOOTS_GROUND_ITEM_ID;
            bool keyPickup = candidate.ItemId == Config.KEY_GROUND_ITEM_ID;
            // 재화류(소환석·잼·부츠·열쇠)는 봇 반응 지연 공통 — 사람 선점권 (#222)
            if ((summonStonePickup || jamPickup || bootsPickup || keyPickup) &&
                groundItemManager.IsYoungerThan(
                    candidate.GroundItemUid, SummonStoneBotReactionDelay))
                continue;
            bool canStore = inventory.GetAllItems().Count < Config.GetOrbCapacity();

            long discovererPlayerId = groundItemManager.GetDiscovererPlayerId(
                candidate.GroundItemUid);
            var status = groundItemManager.TryClaim(
                candidate.GroundItemUid,
                bot.PlayerId,
                bot.CurrentArea,
                bot.Position.X,
                bot.Position.Y,
                item =>
                {
                    if (item.ItemId is Config.SUMMON_STONE_GROUND_ITEM_ID or Config.JAM_GROUND_ITEM_ID
                        or Config.BOOTS_GROUND_ITEM_ID or Config.KEY_GROUND_ITEM_ID)
                        return true;

                    disposition = GroundItemPickupPolicy.Resolve(
                        item.ItemId,
                        bot.Stamina,
                        100,
                        bot.Corruption,
                        out staminaRecovery,
                        out corruptionRecovery,
                        matchingId,
                        bot.PlayerId);
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
            else if (bootsPickup)
            {
                bot.BootsSpeedUntilUtc =
                    DateTime.UtcNow.AddSeconds(Config.BOOTS_SPEED_DURATION_SECONDS);
            }
            else if (keyPickup)
            {
                bot.FreeSummonCharges += 1;
            }
            else if (summonStonePickup)
            {
                summonStoneState = summonStoneManager.AddStones(bot.PlayerId, 1);
            }
            else if (autoUsed)
            {
                int effectiveStaminaRecovery = Math.Min(staminaRecovery, Math.Max(0, 100 - bot.Stamina));
                int effectiveCorruptionRecovery = Math.Min(corruptionRecovery, Math.Max(0, bot.Corruption));
                effectiveRecovery = effectiveStaminaRecovery + effectiveCorruptionRecovery;
                bot.Stamina = Math.Min(100, bot.Stamina + staminaRecovery);
                bot.Corruption = Math.Max(0, bot.Corruption - corruptionRecovery);
                // 하트는 앞줄 오브 HP도 만충으로 (#222 M4) — 사람과 같은 규칙.
                if (claimedItem.ItemId == global::network.common.Config.HEART_GROUND_ITEM_ID)
                    game_server.sessions.GameClientSession.SwarmHeartPickupCallback?.Invoke(
                        matchingId, bot.PlayerId);
            }
            else if (!inventoryManager.TryAddItemWithCapacity(
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
        bot.Corruption = Math.Clamp(bot.Corruption + damage, 0, Config.MAX_CORRUPTION);
        if (previousCorruption < Config.MAX_CORRUPTION && bot.Corruption >= Config.MAX_CORRUPTION)
            bot.LastProximityAttackerPlayerId = attackerPlayerId;
    }

    public bool TryFinalizeProximityAutoCombatElimination(BotPlayerState bot, long matchingId)
    {
        if (bot.IsEliminated || bot.Corruption < Config.MAX_CORRUPTION)
            return false;

        bot.IsEliminated = true;
        bot.Path.Clear();
        bot.PathIndex = 0;
        bot.PendingRngInteractId = 0;
        bot.RngCollectProgressStartTime = DateTime.MinValue;
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
