using game_server.matches.monsters;
using game_server.players;
using game_server.players.bots;
using network.common.data.models;

namespace game_server.matches;

/// <summary>
///     매치의 전투 틱을 조율한다.
/// </summary>
internal class MatchCombatService(
    PlayerHealthService healthService,
    MatchResultService matchResults,
    PlayerOrbTrailService orbTrails,
    MatchTrailCutService trailCuts,
    MatchOrbAttackService orbAttacks,
    BotBehaviorService botBehavior,
    MonsterAttackService monsterAttacks)
{
    public virtual void ProcessTick(MatchRuntime runtime, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat tick requires the match lock.");
        }
        if (runtime.IsEnded || runtime.Mode == MatchMode.SoloMapValidation || !runtime.IsGameplayActive(nowUtc))
        {
            return;
        }

        var players = runtime.GetAlivePlayers();
        if (players.Count == 0)
        {
            return;
        }

        orbTrails.UpdateTrails(runtime, players);
        trailCuts.ProcessTick(runtime, nowUtc, players);
        orbAttacks.ProcessTick(runtime, nowUtc);
        monsterAttacks.ProcessTick(runtime, players, nowUtc);
        if (runtime.IsEnded)
        {
            return;
        }

        players.RemoveAll(player => player.IsEliminated);
        var bots = runtime.Bots.GetBots();
        botBehavior.UpdateSleep(runtime, bots, nowUtc);
        healthService.ApplySleepRecovery(runtime, players, nowUtc);
        botBehavior.ProcessDoorInteractions(runtime, bots, nowUtc);
        botBehavior.ProcessOrbGrowth(runtime, bots);
        matchResults.TryEndOnScoreTimeout(runtime, nowUtc);
    }
}
