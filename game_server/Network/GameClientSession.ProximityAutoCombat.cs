using game_server.services;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.network;

public partial class GameClientSession
{
    internal const int ProximityCombatChalkPowderItemId = 201000015;
    internal const int ProximityCombatShortChalkItemId = 201000016;
    internal const int ProximityCombatLongChalkItemId = 201000017;
    internal const int ProximityAutoAttackDealtEventType = 17;

    internal static int ResolveProximityAutoCombatWeaponItemId(PlayerInGameInventory inventory)
    {
        if (inventory.GetItemCount(ProximityCombatLongChalkItemId) > 0)
            return ProximityCombatLongChalkItemId;
        if (inventory.GetItemCount(ProximityCombatShortChalkItemId) > 0)
            return ProximityCombatShortChalkItemId;
        if (inventory.GetItemCount(ProximityCombatChalkPowderItemId) > 0)
            return ProximityCombatChalkPowderItemId;

        return 0;
    }

    internal static int ResolveProximityAutoCombatDamage(int weaponItemId)
    {
        int damage = 0;
        foreach ((int buffId, int value, int _) in GameItemData.Get(weaponItemId).ConsumableBuffList)
        {
            var buffData = GameBuffData.Get(buffId);
            if (buffData.SubType == BuffSubType.CORRUPTION_ADD)
                damage += Math.Abs(value);
        }

        return damage;
    }

    internal void ApplyProximityAutoCombatHit(long sourcePlayerId, AreaType area, int weaponItemId)
    {
        if (!PlayerId.HasValue || IsEliminated)
            return;

        int damage = ResolveProximityAutoCombatDamage(weaponItemId);
        if (damage <= 0)
            return;

        ModifyStats(corruptionDelta: damage);

        // For this event type RevealDelayMs is used as lightweight weapon metadata.
        // It keeps the P0 on the existing encounter packet and avoids adding a new input surface.
        SendEncounterEvent(
            sourcePlayerId,
            area,
            EncounterRevealManager.RoomEncounterChalkHitEventType,
            EncounterRevealManager.PairCooldownSeconds,
            weaponItemId);
    }

    internal void SendProximityAutoCombatAttackFeedback(
        long targetPlayerId,
        AreaType area,
        int weaponItemId)
    {
        SendEncounterEvent(
            targetPlayerId,
            area,
            ProximityAutoAttackDealtEventType,
            0,
            weaponItemId);
    }
}
