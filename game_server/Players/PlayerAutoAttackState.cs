namespace game_server.players;

internal enum AutoAttackPhase
{
    Engaged,
    Suspended
}

internal readonly record struct AutoAttackEngagement(
    AutoAttackPhase Phase,
    long TargetPlayerId,
    int WeaponItemId,
    DateTime AimReadyAtUtc,
    DateTime NextAttackAtUtc,
    int RemainingInitialBurstAttacks,
    DateTime LostAtUtc = default);

internal sealed class PlayerAutoAttackState
{
    public Dictionary<(long ItemUid, int StackIndex), AutoAttackEngagement> Engagements { get; } = new();

    public Dictionary<(long ItemUid, int StackIndex), DateTime> BurstRechargeReadyAtUtc { get; } = new();

    public void ResetAttackCooldown(long itemUid, DateTime nowUtc)
    {
        foreach (var key in Engagements.Keys.ToArray())
        {
            var engagement = Engagements[key];
            if (key.ItemUid != itemUid || engagement.Phase != AutoAttackPhase.Engaged)
            {
                continue;
            }

            Engagements[key] = engagement with { NextAttackAtUtc = nowUtc };
        }
    }
}
