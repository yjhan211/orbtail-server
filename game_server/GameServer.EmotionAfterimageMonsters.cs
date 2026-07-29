using game_server.network;
using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<long, DateTime> _nextMonsterPositionBroadcastAtUtc = new();
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

        var monsterTargetsById = aliveMonsterTargets.ToDictionary(target => target.MonsterId);
        var finalMonsterStates = new MonsterSnapshotAccumulator();
        foreach (var attack in playerMonsterAttacks)
        {
            if (attack.TargetPlayerId >= 0)
                continue;

            int primaryMonsterId = checked((int)-attack.TargetPlayerId);
            if (!monsterTargetsById.TryGetValue(primaryMonsterId, out var primaryTarget))
                continue;

            bool primaryHit = ApplyPlayerOrbDamageToEmotionAfterimageMonster(
                matchingId,
                primaryTarget,
                attack,
                hitDamageMultiplier: 1f,
                isSplash: false,
                nowUtc,
                matchingSessions,
                finalMonsterStates);
            if (!primaryHit || !EmotionAfterimagePveCombatRules.ShouldApplyWaveSplash(
                    attack.WeaponItemId, attack.TargetPlayerId, attack.IsResonanceProc))
                continue;

            foreach (var secondaryTarget in EmotionAfterimagePveCombatRules.FindWaveSplashTargets(
                         aliveMonsterTargets, primaryMonsterId))
            {
                ApplyPlayerOrbDamageToEmotionAfterimageMonster(
                    matchingId,
                    secondaryTarget,
                    attack,
                    SurvivorOrbData.WaveSplashSecondaryDamageMultiplier,
                    isSplash: true,
                    nowUtc,
                    matchingSessions,
                    finalMonsterStates);
            }
        }

        if (finalMonsterStates.Count > 0)
            BroadcastMonsterSnapshot(matchingId, matchingSessions, finalMonsterStates.GetFinalStates());
    }

    private static ProximityCombatActor CreateMonsterTargetActor(MonsterCombatTarget monster)
    {
        var cell = ProximityCombatLineOfSight.WorldPositionToCell(monster.Position);
        return new ProximityCombatActor(
            -monster.MonsterId, monster.Area, monster.Position, 0, 0f, 0, 0f, 0f, 0f, monster.MapId, cell);
    }

    private bool ApplyPlayerOrbDamageToEmotionAfterimageMonster(
        long matchingId,
        MonsterCombatTarget target,
        ProximityCombatAttack attack,
        float hitDamageMultiplier,
        bool isSplash,
        DateTime nowUtc,
        IReadOnlyCollection<GameClientSession> matchingSessions,
        MonsterSnapshotAccumulator finalMonsterStates)
    {
        int damage = SurvivorOrbData.CalculatePveDamage(
            attack.WeaponItemId,
            target.RewardItemId,
            attack.Damage,
            hitDamageMultiplier);
        if (damage <= 0)
            return false;

        var result = _emotionAfterimageMonsterManager.ApplyDamage(
            matchingId, target.MonsterId, attack.AttackerPlayerId, damage, nowUtc);
        if (!result.StateChanged || result.State == null)
            return false;

        finalMonsterStates.Record(result.State);
        if (result.Killed)
        {
            AwardMonsterKill(
                matchingId,
                result.State,
                attack.AttackerPlayerId,
                result.SummonStoneReward,
                matchingSessions);
        }

        matchingSessions.FirstOrDefault(session =>
                session.PlayerId == attack.AttackerPlayerId && !session.IsEliminated)
            ?.SendEmotionAfterimageMonsterAttackFeedback(
                target.MonsterId,
                target.Area,
                attack.WeaponItemId,
                damage);

        logger.LogInformation(
            "Emotion afterimage hit: MatchingId={MatchingId}, MonsterId={MonsterId}, Attacker={Attacker}, Damage={Damage}, Affinity={Affinity}, Splash={Splash}, RemainingHp={Health}, Killed={Killed}",
            matchingId,
            target.MonsterId,
            attack.AttackerPlayerId,
            damage,
            SurvivorOrbData.GetPveDamageMultiplier(attack.WeaponItemId, target.RewardItemId),
            isSplash,
            result.State.CurrentHealth,
            result.Killed);
        return true;
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
            targetSession.ApplyEmotionAfterimageMonsterHit(attack.MonsterId, attack.Damage);
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

    private void AwardMonsterKill(long matchingId, MonsterRuntimeInfo monster, long killerPlayerId,
        int summonStoneReward, IReadOnlyCollection<GameClientSession> matchingSessions)
    {
        if (summonStoneReward <= 0)
            return;

        var state = _summonStoneManager.AddStones(matchingId, killerPlayerId, summonStoneReward);
        matchingSessions.FirstOrDefault(session => session.PlayerId == killerPlayerId)
            ?.SendSummonStoneState();
        _gameEventLogManager.LogSummonStoneAward(
            matchingId,
            killerPlayerId,
            monster.MonsterId,
            summonStoneReward,
            state.StoneCount,
            monster.AreaType.ToString(),
            monster.IsCore,
            BotPlayerManager.IsBotPlayerId(killerPlayerId));

        logger.LogInformation(
            "Emotion afterimage summon stones granted: MatchingId={MatchingId}, MonsterId={MonsterId}, PlayerId={PlayerId}, Reward={Reward}, Balance={Balance}",
            matchingId, monster.MonsterId, killerPlayerId, summonStoneReward, state.StoneCount);
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
        IEnumerable<MonsterRuntimeInfo>? states = null)
    {
        // Initial/area-entry syncs are sent directly to one session. Runtime deltas and
        // closure syncs are routed only to observers currently occupying each monster area.
        var snapshotStates = states ?? _emotionAfterimageMonsterManager.GetSnapshot(matchingId);
        foreach (var monsterChunk in MonsterSnapshotBatcher.CreateAreaChunks(snapshotStates))
        {
            var observers = sessions.Where(session => session.CurrentArea == monsterChunk.Area).ToList();
            if (observers.Count == 0)
                continue;

            using var packet = Packet.Create((int)Protocol.G_TO_C_MONSTER_SNAPSHOT);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_MONSTER_SNAPSHOT
            {
                Monsters = monsterChunk.Monsters
            }));
            foreach (var session in observers)
                session.Send(packet);
        }
    }

    private void CleanupEmotionAfterimageMonsterRuntime(long matchingId)
    {
        _monsterAutoCombatResolver.RemoveMatching(matchingId);
        _nextMonsterPositionBroadcastAtUtc.TryRemove(matchingId, out _);
    }
}
