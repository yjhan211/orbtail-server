using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<long, DateTime> _nextMonsterPositionBroadcastAtUtc = new();
    private static readonly TimeSpan MonsterPositionBroadcastInterval = TimeSpan.FromMilliseconds(100);

    private IReadOnlyCollection<MonsterCombatTarget> AdvanceEmotionAfterimageMonsters(
        long matchingId,
        IReadOnlyCollection<GameClientSession> matchingSessions,
        IReadOnlyCollection<BotPlayerState> matchingBots,
        DateTime nowUtc)
    {
        var possibleTargets = BuildEmotionAfterimageMonsterTargets(matchingId, matchingSessions, matchingBots);

        var monsterTick = _emotionAfterimageMonsterManager.Tick(matchingId, possibleTargets, nowUtc);
        if (monsterTick.ChangedStates.Count > 0 && TryConsumeMonsterPositionBroadcastSlot(matchingId, nowUtc))
            BroadcastMonsterSnapshot(matchingId, matchingSessions, monsterTick.ChangedStates);
        if (monsterTick.SpawnedStates.Count > 0)
        {
            BroadcastMonsterMinimapSnapshot(matchingSessions, monsterTick.SpawnedStates);
            _gameEventLogManager.LogRewardAreaSnapshot(
                matchingId,
                _emotionAfterimageMonsterManager.GetRewardAreaSnapshot(matchingId),
                "wave_spawn");
        }
        foreach (var release in monsterTick.ReinforcementReleases)
            _gameEventLogManager.LogReinforcementReleased(matchingId, release);

        foreach (var monsterAttack in monsterTick.Attacks)
            ApplyMonsterAttack(matchingId, monsterAttack, matchingSessions, matchingBots);

        return _emotionAfterimageMonsterManager.GetAliveTargets(matchingId);
    }

    private IReadOnlyList<MonsterSpatialTarget> BuildEmotionAfterimageMonsterTargets(
        long matchingId,
        IReadOnlyCollection<GameClientSession> matchingSessions,
        IReadOnlyCollection<BotPlayerState> matchingBots)
    {
        var targets = new List<MonsterSpatialTarget>(matchingSessions.Count + matchingBots.Count);

        // Monsters see every player in their server-committed room. This must not
        // inherit combat's stricter tile validation or depend on equipped orbs.
        foreach (var session in matchingSessions)
        {
            if (!session.PlayerId.HasValue || session.CurrentArea == AreaType.None ||
                session.LastValidatedPosition == null)
                continue;

            targets.Add(new MonsterSpatialTarget(
                session.PlayerId.Value,
                session.CurrentMapId,
                session.CurrentArea,
                session.LastValidatedPosition));
        }

        MapId botMapId = _botPlayerManager.GetMatchingMapId(matchingId);
        foreach (var bot in matchingBots)
        {
            if (bot.CurrentArea == AreaType.None)
                continue;

            targets.Add(new MonsterSpatialTarget(
                bot.PlayerId,
                botMapId,
                bot.CurrentArea,
                bot.Position));
        }

        return targets;
    }
    private void ApplyPlayerOrbDamageToEmotionAfterimageMonsters(
        long matchingId,
        IReadOnlyCollection<ProximityCombatAttack> attacks,
        IReadOnlyCollection<MonsterCombatTarget> aliveMonsterTargets,
        DateTime nowUtc,
        IReadOnlyCollection<GameClientSession> matchingSessions)
    {
        if (attacks.Count == 0 || aliveMonsterTargets.Count == 0)
            return;

        var monsterTargetsById = aliveMonsterTargets.ToDictionary(target => target.MonsterId);
        var finalMonsterStates = new MonsterSnapshotAccumulator();
        foreach (var attack in attacks)
        {
            if (!_survivorPhaseManager.IsPveAllowed(matchingId, attack.Area))
                continue;

            int primaryMonsterId = checked((int)-attack.TargetPlayerId);
            if (!monsterTargetsById.TryGetValue(primaryMonsterId, out var primaryTarget))
                continue;

            bool primaryHit = ApplyPlayerOrbDamageToEmotionAfterimageMonster(
                matchingId,
                primaryTarget,
                attack,
                hitDamageMultiplier: 1f,
                isSplash: attack.IsWaveAreaSecondary || attack.IsWindAreaSecondary,
                nowUtc,
                matchingSessions,
                finalMonsterStates);
            if (primaryHit &&
                !attack.IsWaveAreaSecondary &&
                !attack.IsWindAreaSecondary)
                BroadcastObservedProximityAttackVfx(attack, matchingSessions);
        }

        if (finalMonsterStates.Count > 0)
        {
            var finalStates = finalMonsterStates.GetFinalStates();
            BroadcastMonsterSnapshot(matchingId, matchingSessions, finalStates);
            BroadcastMonsterMinimapSnapshot(matchingSessions, finalStates.Where(state => !state.IsAlive));
        }
    }
    private static ProximityCombatActor CreateMonsterTargetActor(MonsterCombatTarget monster)
    {
        var cell = ProximityCombatLineOfSight.WorldPositionToCell(monster.MapId, monster.Position);
        return new ProximityCombatActor(
            -monster.MonsterId, monster.Area, monster.Position, 0, 0f, 0, 0f, 0f, 0f, monster.MapId, cell,
            IsMonsterTarget: true,
            IsCoreMonsterTarget: monster.IsCore);
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
        SurvivorOrbColor affinityColor = ResolvePlayerPveAffinityColor(
            matchingId,
            attack.AttackerPlayerId);
        int damage = SurvivorOrbData.CalculatePveDamage(
            affinityColor,
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
            if (result.State.IsCore)
                _survivorPhaseManager.ReportCoreDefeated(
                    matchingId, attack.AttackerPlayerId, result.State.AreaType);

            _gameEventLogManager.LogEmotionAfterimageKilled(
                matchingId,
                result.State.MonsterId,
                result.State.AreaType.ToString(),
                result.State.IsCore,
                result.FirstAttackerPlayerId,
                result.LastAttackerPlayerId,
                result.DamageByPlayer ?? new Dictionary<long, int>(),
                result.IsReinforcement);
            AwardMonsterKill(
                matchingId,
                result.State,
                attack.AttackerPlayerId,
                result.SummonStoneReward,
                matchingSessions);
        }

        bool playProjectilePresentation = EmotionAfterimagePveCombatRules.ShouldEmitWaveProjectilePresentation(isSplash);
        matchingSessions.FirstOrDefault(session =>
                session.PlayerId == attack.AttackerPlayerId && !session.IsEliminated)
            ?.SendEmotionAfterimageMonsterAttackFeedback(
                playProjectilePresentation ? target.MonsterId : 0,
                target.Area,
                attack.WeaponItemId,
                damage);

        logger.LogInformation(
            "Emotion afterimage hit: MatchingId={MatchingId}, MonsterId={MonsterId}, Attacker={Attacker}, Damage={Damage}, Affinity={Affinity}, Splash={Splash}, RemainingHp={Health}, Killed={Killed}",
            matchingId,
            target.MonsterId,
            attack.AttackerPlayerId,
            damage,
            SurvivorOrbData.GetPveDamageMultiplier(affinityColor, target.RewardItemId),
            isSplash,
            result.State.CurrentHealth,
            result.Killed);
        return true;
    }

    private SurvivorOrbColor ResolvePlayerPveAffinityColor(long matchingId, long playerId)
    {
        var inventory = _inGameInventoryManager.GetPlayerInventory(matchingId, playerId);
        var boardItemIds = inventory.GetAllItems()
            .Where(item => item.Count > 0)
            .SelectMany(item => Enumerable.Repeat(item.ItemId, item.Count));

        // A board gains an elemental advantage only while one attack colour owns its majority.
        return SurvivorOrbData.TryGetDominantPveColor(boardItemIds, out SurvivorOrbColor dominantColor)
            ? dominantColor
            : SurvivorOrbColor.None;
    }
    private void ApplyMonsterAttack(long matchingId, MonsterAttack attack,
        IReadOnlyCollection<GameClientSession> matchingSessions, IReadOnlyCollection<BotPlayerState> matchingBots)
    {
        if (!_survivorPhaseManager.IsPveAllowed(matchingId, attack.Area))
            return;

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

        int corruptionBefore = targetBot.Corruption;
        int corruptionAfter = Math.Min(Config.SURVIVOR_MAX_CORRUPTION, corruptionBefore + attack.Damage);
        bool isLethal = corruptionBefore < Config.SURVIVOR_MAX_CORRUPTION &&
                        corruptionAfter >= Config.SURVIVOR_MAX_CORRUPTION;
        _gameEventLogManager.LogEmotionAfterimageHit(
            matchingId,
            attack.MonsterId,
            targetBot.PlayerId,
            attack.Area.ToString(),
            attack.Damage,
            corruptionBefore,
            corruptionAfter,
            isLethal,
            isBot: true,
            DateTimeOffset.UtcNow);
        logger.LogInformation(
            "Emotion afterimage attack: MatchingId={MatchingId}, MonsterId={MonsterId}, Target={Target}, TargetKind=Bot, Damage={Damage}, CorruptionBefore={CorruptionBefore}, CorruptionAfter={CorruptionAfter}, Killed={Killed}",
            matchingId,
            attack.MonsterId,
            targetBot.PlayerId,
            attack.Damage,
            corruptionBefore,
            corruptionAfter,
            isLethal);
        _botPlayerManager.ApplyProximityAutoCombatDamage(targetBot, attack.Damage);
    }

    private void AwardMonsterKill(long matchingId, MonsterRuntimeInfo monster, long killerPlayerId,
        int summonStoneReward, IReadOnlyCollection<GameClientSession> matchingSessions)
    {
        if (summonStoneReward <= 0)
            return;

        var state = _summonStoneManager.AddStones(matchingId, killerPlayerId, summonStoneReward);
        matchingSessions.FirstOrDefault(session => session.PlayerId == killerPlayerId)
            ?.SendSummonStoneState(summonStoneReward, monster.PositionX, monster.PositionY);
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

    private static void BroadcastMonsterMinimapSnapshot(
        IReadOnlyCollection<GameClientSession> sessions, IEnumerable<MonsterRuntimeInfo> states)
    {
        foreach (var monsterChunk in MonsterSnapshotBatcher.CreateAreaChunks(states))
        {
            using var packet = Packet.Create((int)Protocol.G_TO_C_MONSTER_SNAPSHOT);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_MONSTER_SNAPSHOT
            {
                Monsters = monsterChunk.Monsters
            }));

            foreach (var session in sessions)
                session.Send(packet);
        }
    }

    private void CleanupEmotionAfterimageMonsterRuntime(long matchingId)
    {

        _nextMonsterPositionBroadcastAtUtc.TryRemove(matchingId, out _);
    }
}
