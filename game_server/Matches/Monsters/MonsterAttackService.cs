using network.common.data;
using game_server.players;
using network.common;

namespace game_server.matches.monsters;

/// <summary>
///     매치 틱마다 몬스터의 플레이어 공격을 실행
/// </summary>
internal sealed class MonsterAttackService(MatchCombatDamageService combatDamage)
{
    public void ProcessTick(MatchRuntime runtime, IReadOnlyList<Player> players, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Monster attacks require the match lock.");
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
            if (TryStartContactAttack(runtime, monster, players, nowUtc, out var victim))
            {
                combatDamage.ApplyMonsterContactHit(runtime, victim, monster.MonsterId, Config.ScaleSwarmDamageTaken(monster.ContactDamageValue), nowUtc);
            }
        }
    }

    internal bool TryStartContactAttack(MatchRuntime runtime, Monster monster, IReadOnlyList<Player> players, DateTime now, out Player victim)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Monster attacks require the match lock.");
        }

        victim = null!;
        if (!monster.Alive || now < monster.NextContactAtUtc)
        {
            return false;
        }

        var monsterArea = monster.CurrentArea;
        float attackRange = monster.AttackRangeValue;
        foreach (var player in players)
        {
            var position = player.Position;
            if (position == null || player.IsEliminated)
            {
                continue;
            }
            if (player.CurrentArea != monsterArea)
            {
                continue;
            }
            if (!GroundGeometry.IsWithinGroundRadius(monster.Position, position, attackRange))
            {
                continue;
            }
            if (player.StatusEffects.IsActive(PlayerStatusEffectKind.MonsterContactImmunity, now))
            {
                continue;
            }

            monster.NextContactAtUtc = now.AddSeconds(monster.AttackCooldownValue);
            player.StatusEffects.Apply(PlayerStatusEffectKind.MonsterContactImmunity, now.AddSeconds(Config.SWARM_MONSTER_CONTACT_IMMUNITY_SECONDS));
            victim = player;
            return true;
        }

        return false;
    }
}
