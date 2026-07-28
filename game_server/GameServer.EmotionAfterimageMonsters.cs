using game_server.network;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server;

public partial class GameServer
{
    private readonly ProximityAutoCombatResolver _monsterAutoCombatResolver = new();
    private readonly Dictionary<long, DateTime> _nextMonsterPositionBroadcastAtUtc = new();
    private static readonly TimeSpan MonsterPositionBroadcastInterval = TimeSpan.FromMilliseconds(100);

    private void ProcessEmotionAfterimageMonsterCombat(
        long matchingId,
        IReadOnlyCollection<GameClientSession> matchingSessions,
        IReadOnlyCollection<BotPlayerState> matchingBots,
        IReadOnlyCollection<ProximityCombatActor> combatActors,
        DateTime nowUtc)
    {
        var spatialPlayers = combatActors
            .GroupBy(actor => actor.PlayerId)
            .Select(group => group.First())
            .ToList();
        var possibleTargets = spatialPlayers
            .Select(actor => new MonsterSpatialTarget(actor.PlayerId, actor.MapId, actor.Area, actor.Position))
            .ToList();

        var monsterTick = _emotionAfterimageMonsterManager.Tick(matchingId, possibleTargets, nowUtc);
        if (monsterTick.ChangedStates.Count > 0 && TryConsumeMonsterPositionBroadcastSlot(matchingId, nowUtc))
            BroadcastMonsterSnapshot(matchingId, matchingSessions, monsterTick.ChangedStates);

        foreach (var monsterAttack in monsterTick.Attacks)
            ApplyMonsterAttack(matchingId, monsterAttack, matchingSessions, matchingBots);

        var aliveMonsterTargets = _emotionAfterimageMonsterManager.GetAliveTargets(matchingId);
        if (aliveMonsterTargets.Count == 0)
        {
            _monsterAutoCombatResolver.Resolve(matchingId, [], nowUtc);
            return;
        }

        var monsterTargetActors = aliveMonsterTargets.Select(CreateMonsterTargetActor).ToList();
        var monsterAttackers = combatActors
            .Where(actor => actor.WeaponItemId > 0 && actor.AttackRange > 0f && actor.Damage > 0)
            // PvP remains the first priority. A monster is selected only when this weapon
            // has no real player target in its authoritative range/line of sight.
            .Where(actor => !HasEligiblePvpTarget(actor, spatialPlayers))
            .Concat(monsterTargetActors)
            .ToList();
        var playerMonsterAttacks = _monsterAutoCombatResolver.Resolve(
            matchingId, monsterAttackers, nowUtc, ProximityCombatLineOfSight.CanTarget);

        foreach (var attack in playerMonsterAttacks)
        {
            if (attack.TargetPlayerId >= 0)
                continue;

            int monsterId = checked((int)-attack.TargetPlayerId);
            var result = _emotionAfterimageMonsterManager.ApplyDamage(
                matchingId, monsterId, attack.AttackerPlayerId, attack.Damage, nowUtc);
            if (!result.StateChanged || result.State == null)
                continue;

            if (result.Killed)
                AwardMonsterKill(matchingId, result.State, attack.AttackerPlayerId, result.RewardItemId,
                    matchingSessions);

            BroadcastMonsterSnapshot(matchingId, matchingSessions, new[] { result.State });
            logger.LogInformation(
                "Emotion afterimage hit: MatchingId={MatchingId}, MonsterId={MonsterId}, Attacker={Attacker}, Damage={Damage}, RemainingHp={Health}, Killed={Killed}",
                matchingId, monsterId, attack.AttackerPlayerId, attack.Damage, result.State.CurrentHealth, result.Killed);
        }
    }

    private static ProximityCombatActor CreateMonsterTargetActor(MonsterCombatTarget monster)
    {
        var cell = ProximityCombatLineOfSight.WorldPositionToCell(monster.Position);
        return new ProximityCombatActor(
            -monster.MonsterId, monster.Area, monster.Position, 0, 0f, 0, 0f, 0f, 0f, monster.MapId, cell);
    }

    private static bool HasEligiblePvpTarget(ProximityCombatActor attacker,
        IReadOnlyCollection<ProximityCombatActor> spatialPlayers)
    {
        float attackRangeSquared = attacker.AttackRange * attacker.AttackRange;
        return spatialPlayers.Any(candidate =>
        {
            if (candidate.PlayerId == attacker.PlayerId || candidate.Area != attacker.Area)
                return false;
            float x = attacker.Position.X - candidate.Position.X;
            float y = attacker.Position.Y - candidate.Position.Y;
            return x * x + y * y <= attackRangeSquared &&
                   ProximityCombatLineOfSight.CanTarget(attacker, candidate);
        });
    }

    private void ApplyMonsterAttack(long matchingId, MonsterAttack attack,
        IReadOnlyCollection<GameClientSession> matchingSessions, IReadOnlyCollection<BotPlayerState> matchingBots)
    {
        BroadcastMonsterAttackVfx(attack, matchingSessions);

        var targetSession = matchingSessions.FirstOrDefault(session =>
            session.PlayerId == attack.TargetPlayerId && !session.IsEliminated && session.CurrentArea == attack.Area);
        if (targetSession != null)
        {
            targetSession.ApplyEmotionAfterimageMonsterHit(attack.Damage);
            return;
        }

        var targetBot = matchingBots.FirstOrDefault(bot =>
            bot.PlayerId == attack.TargetPlayerId && !bot.IsEliminated && bot.CurrentArea == attack.Area);
        if (targetBot == null)
            return;

        _botPlayerManager.ApplyProximityAutoCombatDamage(targetBot, attack.Damage);
        logger.LogDebug(
            "Emotion afterimage attack: MatchingId={MatchingId}, MonsterId={MonsterId}, Target={Target}, Damage={Damage}",
            matchingId, attack.MonsterId, attack.TargetPlayerId, attack.Damage);
    }

    private void AwardMonsterKill(long matchingId, MonsterRuntimeInfo monster, long killerPlayerId, int rewardItemId,
        IReadOnlyCollection<GameClientSession> matchingSessions)
    {
        if (rewardItemId <= 0)
            return;

        bool inventoryGranted = _inGameInventoryManager.TryAddItemWithCapacity(
            matchingId, killerPlayerId, rewardItemId, Config.SURVIVOR_INVENTORY_SLOT_COUNT, out var addedItem);
        var killerSession = matchingSessions.FirstOrDefault(session => session.PlayerId == killerPlayerId);
        if (inventoryGranted && addedItem != null)
        {
            killerSession?.SendInGameInventoryUpdate(addedItem);
            logger.LogInformation(
                "Emotion afterimage reward granted: MatchingId={MatchingId}, MonsterId={MonsterId}, PlayerId={PlayerId}, ItemId={ItemId}",
                matchingId, monster.MonsterId, killerPlayerId, rewardItemId);
            return;
        }

        // A full board does not delete a kill reward. It is placed at the monster and
        // briefly reserved for the final hitter through the existing pickup path.
        var spawned = _groundItemManager.SpawnItems(
            matchingId, monster.AreaType, monster.PositionX, monster.PositionY, [rewardItemId],
            mapId: MapId.School, discovererPlayerId: killerPlayerId,
            discovererPickupWindow: GroundItemManager.DiscovererPickupWindow);
        if (spawned.Count == 0)
            return;

        int remaining = _areaItemStockManager.GetRemainingCount(matchingId, (int)monster.AreaType);
        using var packet = PacketMaker.G_TO_C_GROUND_ITEM_SPAWN((int)monster.AreaType, remaining, spawned);
        foreach (var session in matchingSessions.Where(session => session.CurrentArea == monster.AreaType))
            session.Send(packet);
    }

    private static void BroadcastMonsterAttackVfx(MonsterAttack attack, IReadOnlyCollection<GameClientSession> sessions)
    {
        using var packet = Packet.Create((int)Protocol.G_TO_C_MONSTER_ATTACK_VFX);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_MONSTER_ATTACK_VFX
        {
            MonsterId = attack.MonsterId,
            TargetPlayerId = attack.TargetPlayerId,
            AreaType = attack.Area
        }));
        foreach (var session in sessions.Where(session => !session.IsEliminated && session.CurrentArea == attack.Area))
            session.Send(packet);
    }
    private bool TryConsumeMonsterPositionBroadcastSlot(long matchingId, DateTime nowUtc)
    {
        if (_nextMonsterPositionBroadcastAtUtc.TryGetValue(matchingId, out var nextAtUtc) && nowUtc < nextAtUtc)
            return false;

        _nextMonsterPositionBroadcastAtUtc[matchingId] = nowUtc + MonsterPositionBroadcastInterval;
        return true;
    }

    private void BroadcastMonsterSnapshot(long matchingId, IReadOnlyCollection<GameClientSession> sessions,
        IEnumerable<MonsterRuntimeInfo> states = null)
    {
        // Initial/closure syncs use all nodes; movement and damage send state deltas.
        // The client merges entries by MonsterId, and groups remain within the 2KB budget.
        var snapshotStates = states ?? _emotionAfterimageMonsterManager.GetSnapshot(matchingId);
        foreach (var monsterChunk in snapshotStates.Chunk(10))
        {
            using var packet = Packet.Create((int)Protocol.G_TO_C_MONSTER_SNAPSHOT);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_MONSTER_SNAPSHOT
            {
                Monsters = monsterChunk.ToList()
            }));
            foreach (var session in sessions)
                session.Send(packet);
        }
    }
}
