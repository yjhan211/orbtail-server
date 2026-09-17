using game_server.matches.monsters;
using game_server.players;
using game_server.players.bots;
using game_server.sessions;
using MessagePack;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.matches;

/// <summary>
///     매치의 전투 틱을 조율한다.
///     오브·몬스터 공격, 상태 효과, 지연 타격을 순서대로 처리하고 피해 적용을 각 서비스에 위임한다.
/// </summary>
internal class MatchCombatService(
    PlayerHealthService healthService,
    MatchCombatDamageService combatDamage,
    MatchResultService matchResults,
    PlayerOrbService playerOrbs,
    PlayerOrbTrailService orbTrails,
    MatchTrailCutService trailCuts,
    MatchOrbAttackService orbAttacks,
    BotBehaviorService botBehavior,
    MonsterCombatService monsterCombat)
{
    internal List<MonsterContactDamage> CollectMonsterContactDamages(MatchRuntime runtime, IReadOnlyList<Player> players, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Match monster service requires the match lock.");
        }
        var monsterContactDamages = new List<MonsterContactDamage>();
        var state = runtime.Monsters;
        if (!state.IsInitialized)
        {
            return monsterContactDamages;
        }

        foreach (var monster in state.Entities.Values)
        {
            if (!monster.Alive)
            {
                continue;
            }

            monsterCombat.CollectContactDamage(runtime, monster, players, nowUtc, monsterContactDamages);
        }
        return monsterContactDamages;
    }

    public virtual void ProcessTick(MatchRuntime runtime)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat tick requires the match lock.");
        }
        if (runtime.IsEnded)
        {
            return;
        }

        if (runtime.Mode == MatchMode.SoloMapValidation)
        {
            return;
        }

        var sessions = runtime.GetSessions().Where(session => !session.IsGameEnded).ToList();
        var bots = runtime.Bots.GetBots().ToList();
        var players = runtime.GetAlivePlayers();
        if (players.Count == 0)
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;
        var aliveBots = bots.Where(bot => !bot.Player.IsEliminated).ToList();
        var monsterContactDamages = CollectMonsterContactDamages(runtime, players, nowUtc);
        if (!runtime.IsGameplayActive())
        {
            return;
        }

        orbTrails.UpdateTrails(runtime, players);
        trailCuts.ProcessTick(runtime, nowUtc, players, sessions);
        orbAttacks.ProcessWaveDetonations(runtime, nowUtc);
        foreach (var player in runtime.GetAlivePlayers())
        {
            playerOrbs.ActivateOrbs(runtime, player, nowUtc);
        }
        orbAttacks.ProcessSunBurns(runtime, nowUtc);
        foreach (var damage in monsterContactDamages)
        {
            ApplySwarmParticipantDamage(runtime, damage, sessions);
        }
        if (runtime.IsEnded)
        {
            return;
        }

        aliveBots.RemoveAll(bot => bot.Player.IsEliminated);
        botBehavior.UpdateSleep(runtime, aliveBots, nowUtc);
        if (runtime.IsEnded)
        {
            return;
        }
        players.RemoveAll(player => player.IsEliminated);
        healthService.ApplySleepRecovery(runtime, players, nowUtc);
        botBehavior.ProcessDoorInteractions(runtime, aliveBots, sessions, nowUtc);

        var orbVisuals = MatchOrbVisual.Build(runtime, players);
        foreach (var session in sessions)
        {
            session.SendOrbVisualStates(orbVisuals);
        }
        matchResults.BroadcastOrbRankings(runtime, sessions);
        botBehavior.ProcessOrbGrowth(runtime, aliveBots);
        if (matchResults.TryEndOnScoreTimeout(runtime, nowUtc))
        {
            return;
        }
        combatDamage.ProcessPendingMonsterHits(runtime, nowUtc, sessions);
        orbAttacks.ProcessSunCrossfires(runtime, nowUtc);
    }

    internal void ApplySwarmParticipantDamage(MatchRuntime runtime, MonsterContactDamage damage, List<GameClientSession> allSessions)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat tick requires the match lock.");
        }
        var victim = runtime.GetPlayer(damage.TargetPlayerId);
        if (runtime.IsEnded || victim == null || victim.IsEliminated)
        {
            return;
        }
        damage = damage with { Damage = Config.ScaleSwarmDamageTaken(damage.Damage) };
        if (runtime.Monsters.Entities.TryGetValue(damage.MonsterId, out var monster) && monster.Insignia == MonsterInsignia.Wave)
        {
            using var vfxPacket = Packet.Create((int)Protocol.G_TO_C_MONSTER_ATTACK_VFX);
            vfxPacket.SetBody(MessagePackSerializer.Serialize(new G_TO_C_MONSTER_ATTACK_VFX
            {
                MonsterId = damage.MonsterId,
                TargetPlayerId = damage.TargetPlayerId,
                AreaType = damage.Area
            }));
            foreach (var vfxSession in allSessions)
            {
                if (!vfxSession.Player.IsEliminated && vfxSession.Player.GameInfo.ObjectInfo.Area == damage.Area)
                {
                    vfxSession.TrySend(vfxPacket);
                }
            }
        }
        combatDamage.ApplySwarmAfterimageMonsterHit(runtime, healthService, victim, damage.MonsterId, damage.Damage);
    }
}
