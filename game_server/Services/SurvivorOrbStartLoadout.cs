namespace game_server.services;

/// <summary>
/// Compatibility boundary for older join paths. Survivor Royale now begins with
/// no attack orb; the opening economy is supplied by <see cref="SummonStoneManager"/>.
/// </summary>
public static class SurvivorOrbStartLoadout
{
    public static int EnsureStartingOrb(InGameInventoryManager inventoryManager, long matchingId, long playerId)
    {
        ArgumentNullException.ThrowIfNull(inventoryManager);
        return 0;
    }
}