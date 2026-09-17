using game_server.matches.monsters;
using game_server.players;
using game_server.players.bots;
using network.common;
using network.common.data.models;

namespace game_server.matches;

/// <summary>
///     매치의 전투 틱을 조율한다.
///     궤적·절단, 오브 공격, 몬스터 접촉, 봇 판단, 표시 갱신을 순서대로 부르고 피해 적용은 각 서비스에 위임한다.
/// </summary>
internal class MatchCombatService(
    PlayerHealthService healthService,
    MatchCombatDamageService combatDamage,
    MatchResultService matchResults,
    PlayerOrbTrailService orbTrails,
    MatchTrailCutService trailCuts,
    MatchOrbAttackService orbAttacks,
    BotBehaviorService botBehavior,
    MonsterCombatService monsterCombat)
{
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
        if (!runtime.IsGameplayActive())
        {
            return;
        }

        orbTrails.UpdateTrails(runtime, players);
        trailCuts.ProcessTick(runtime, nowUtc, players);
        orbAttacks.ProcessTick(runtime, nowUtc);
        ProcessMonsterContacts(runtime, players, nowUtc);
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
        matchResults.TryEndOnScoreTimeout(runtime, nowUtc);
    }

    /// <summary>
    ///     살아 있는 몬스터마다 접촉 공격을 판정하고, 성립하면 그 자리에서 피해를 적용한다.
    ///     피해는 받는 피해 배율을 거친다. 같은 구역에 보내는 피격 알림이 곧 몬스터의 공격 연출 신호다.
    /// </summary>
    internal void ProcessMonsterContacts(MatchRuntime runtime, IReadOnlyList<Player> players, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat tick requires the match lock.");
        }

        if (!runtime.Monsters.IsInitialized)
        {
            return;
        }

        foreach (var monster in runtime.Monsters.Entities.Values)
        {
            if (runtime.IsEnded)
            {
                return;
            }

            if (!monsterCombat.TryStartContactAttack(runtime, monster, players, nowUtc, out var contact))
            {
                continue;
            }

            var victim = runtime.GetPlayer(contact.TargetPlayerId);
            if (victim == null || victim.IsEliminated)
            {
                continue;
            }

            combatDamage.ApplyMonsterContactHit(runtime, victim, contact.MonsterId, Config.ScaleSwarmDamageTaken(contact.Damage), nowUtc);
        }
    }
}
