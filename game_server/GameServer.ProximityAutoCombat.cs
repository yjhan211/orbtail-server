using game_server.network;
using game_server.services;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;

namespace game_server;

public partial class GameServer
{
    private const int ProximityAutoCombatTickIntervalMs = 50;

    private readonly ProximityAutoCombatResolver _proximityAutoCombatResolver = new();
    private Timer? _proximityAutoCombatTimer;
    private int _proximityAutoCombatProcessing;

    private void StartProximityAutoCombatTimer()
    {
        _proximityAutoCombatTimer = new Timer(
            ProcessProximityAutoCombatTick,
            null,
            TimeSpan.FromMilliseconds(ProximityAutoCombatTickIntervalMs),
            TimeSpan.FromMilliseconds(ProximityAutoCombatTickIntervalMs));
        logger.LogInformation(
            "Proximity auto combat timer started: TickMs={TickMs}, AimMilliseconds={AimMilliseconds}",
            ProximityAutoCombatTickIntervalMs,
            ProximityAutoCombatResolver.AimDuration.TotalMilliseconds);
    }

    private void ProcessProximityAutoCombatTick(object? state)
    {
        if (Interlocked.Exchange(ref _proximityAutoCombatProcessing, 1) != 0)
            return;

        try
        {
            var activeSessions = _clientSessions.Values
                .Where(session => session.PlayerId.HasValue && !session.IsEliminated)
                .ToList();

            foreach (long matchingId in GetActiveMatchingIds())
            {
                if (!GameClientSession.IsRoundActionPhase(matchingId))
                    continue;

                var matchingSessions = activeSessions
                    .Where(session => session.CurrentMapSubId == matchingId)
                    .ToList();
                var matchingBots = _botPlayerManager.GetBots(matchingId)
                    .Where(bot => !bot.IsEliminated)
                    .ToList();

                var actors = BuildProximityCombatActors(matchingId, matchingSessions, matchingBots);
                var attacks = _proximityAutoCombatResolver.Resolve(
                    matchingId,
                    actors,
                    DateTime.UtcNow);
                if (attacks.Count == 0)
                    continue;

                ApplyProximityCombatVolley(matchingId, attacks, matchingSessions, matchingBots, activeSessions);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Proximity auto combat tick failed");
        }
        finally
        {
            Volatile.Write(ref _proximityAutoCombatProcessing, 0);
        }
    }

    private List<ProximityCombatActor> BuildProximityCombatActors(
        long matchingId,
        IReadOnlyCollection<GameClientSession> matchingSessions,
        IReadOnlyCollection<BotPlayerState> matchingBots)
    {
        var actors = new List<ProximityCombatActor>(matchingSessions.Count + matchingBots.Count);

        foreach (var session in matchingSessions)
        {
            if (!session.PlayerId.HasValue || session.CurrentArea == AreaType.None ||
                session.LastValidatedPosition == null)
            {
                continue;
            }

            var inventory = _inGameInventoryManager.GetPlayerInventory(matchingId, session.PlayerId.Value);
            var equippedItem = inventory.GetEquippedBattleItem();
            var combatData = equippedItem == null ? null : BattleItemCombatData.Get(equippedItem.ItemId);
            actors.Add(new ProximityCombatActor(
                session.PlayerId.Value,
                session.CurrentArea,
                session.LastValidatedPosition,
                equippedItem?.ItemId ?? 0,
                combatData?.AttackRange ?? 0f,
                combatData?.Damage ?? 0,
                combatData?.AttackIntervalSeconds ?? 0f,
                combatData?.ProjectileWidth ?? 0f,
                combatData?.EffectDurationSeconds ?? 0f));
        }

        foreach (var bot in matchingBots)
        {
            if (bot.CurrentArea == AreaType.None)
                continue;

            var inventory = _inGameInventoryManager.GetPlayerInventory(matchingId, bot.PlayerId);
            var equippedItem = inventory.GetEquippedBattleItem();
            var combatData = equippedItem == null ? null : BattleItemCombatData.Get(equippedItem.ItemId);
            actors.Add(new ProximityCombatActor(
                bot.PlayerId,
                bot.CurrentArea,
                bot.Position,
                equippedItem?.ItemId ?? 0,
                combatData?.AttackRange ?? 0f,
                combatData?.Damage ?? 0,
                combatData?.AttackIntervalSeconds ?? 0f,
                combatData?.ProjectileWidth ?? 0f,
                combatData?.EffectDurationSeconds ?? 0f));
        }

        return actors;
    }

    private void ApplyProximityCombatVolley(
        long matchingId,
        IReadOnlyCollection<ProximityCombatAttack> attacks,
        IReadOnlyCollection<GameClientSession> matchingSessions,
        IReadOnlyCollection<BotPlayerState> matchingBots,
        List<GameClientSession> activeSessions)
    {
        foreach (var attack in attacks)
        {
            int damage = attack.Damage;
            if (damage <= 0)
                continue;

            var targetSession = matchingSessions.FirstOrDefault(session =>
                session.PlayerId == attack.TargetPlayerId &&
                !session.IsEliminated &&
                session.CurrentArea == attack.Area);
            if (targetSession != null)
            {
                targetSession.ApplyProximityAutoCombatHit(
                    attack.AttackerPlayerId,
                    attack.Area,
                    attack.WeaponItemId,
                    damage);
            }
            else
            {
                var targetBot = matchingBots.FirstOrDefault(bot =>
                    bot.PlayerId == attack.TargetPlayerId &&
                    !bot.IsEliminated &&
                    bot.CurrentArea == attack.Area);
                if (targetBot == null)
                    continue;

                _botPlayerManager.ApplyProximityAutoCombatDamage(targetBot, damage);
            }

            var attackerSession = matchingSessions.FirstOrDefault(session =>
                session.PlayerId == attack.AttackerPlayerId && !session.IsEliminated);
            attackerSession?.SendProximityAutoCombatAttackFeedback(
                attack.TargetPlayerId,
                attack.Area,
                attack.WeaponItemId);

            logger.LogDebug(
                "Proximity auto attack: MatchingId={MatchingId}, Attacker={Attacker}, Target={Target}, Area={Area}, WeaponItemId={WeaponItemId}, Damage={Damage}",
                matchingId,
                attack.AttackerPlayerId,
                attack.TargetPlayerId,
                attack.Area,
                attack.WeaponItemId,
                damage);
        }

        foreach (var bot in matchingBots)
        {
            if (!_botPlayerManager.TryFinalizeProximityAutoCombatElimination(bot, matchingId))
                continue;

            _gameEventLogManager.LogElimination(
                matchingId,
                bot.PlayerId,
                EliminationReason.MENTAL_ZERO.ToString(),
                isBot: true);
            ProcessBotElimination(matchingId, bot.PlayerId, EliminationReason.MENTAL_ZERO, activeSessions);
        }
    }
}
