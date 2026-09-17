using network.common.data;
using game_server.players;
using network.common;

namespace game_server.matches.monsters;

internal readonly record struct MonsterDamageResult(Monster? Monster, bool Killed)
{
    public static MonsterDamageResult None => new(null, false);

    public bool Applied => Monster != null;
}

/// <summary>
///     몬스터의 접촉 공격·플레이어 면역과 피격 시 주변 표적 지정·처치 보상을 조율한다.
///     개체 상태는 Monster가 보관하며, 모든 호출은 매치 잠금 안에서 수행한다.
/// </summary>
internal sealed class MonsterCombatService
{
    public bool TryStartContactAttack(MatchRuntime runtime, Monster monster, IReadOnlyList<Player> players, DateTime now, out Player victim)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Monster combat service requires the match lock.");
        }

        victim = null!;
        if (!runtime.Monsters.IsInitialized || !monster.Alive || now < monster.NextContactAtUtc)
        {
            return false;
        }

        var monsterArea = GameMapData.GetCurrentArea(monster.Info.ObjectInfo.MapId, monster.Info.ObjectInfo.Cell);
        float attackRange = monster.AttackRangeValue > Monster.BaseContactRadius ? monster.AttackRangeValue : Monster.GetContactRadius(monster.Kind);
        foreach (var player in players)
        {
            var position = player.Position;
            if (position == null || player.IsEliminated)
            {
                continue;
            }
            if (GameMapData.GetCurrentArea(player.GameInfo.ObjectInfo.MapId, player.GameInfo.ObjectInfo.Cell) != monsterArea)
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
            monster.ChaseTargetPlayerId = player.PlayerId;
            victim = player;
            return true;
        }

        return false;
    }

    public MonsterDamageResult ApplyMonsterDamage(MatchRuntime runtime, int monsterId, long attackerPlayerId, int damage, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Monster combat service requires the match lock.");
        }
        var state = runtime.Monsters;
        if (damage <= 0 || !state.IsInitialized)
        {
            return MonsterDamageResult.None;
        }

        var monster = state.Find(monsterId);
        if (monster is not { Alive: true })
        {
            return MonsterDamageResult.None;
        }
        monster.ChaseTargetPlayerId = attackerPlayerId;
        foreach (var mate in state.Entities.Values)
        {
            if (!mate.Alive || mate.ChaseTargetPlayerId != 0 || GameMapData.GetCurrentArea(mate.Info.ObjectInfo.MapId, mate.Info.ObjectInfo.Cell) != GameMapData.GetCurrentArea(monster.Info.ObjectInfo.MapId, monster.Info.ObjectInfo.Cell))
            {
                continue;
            }
            mate.ChaseTargetPlayerId = attackerPlayerId;
        }
        bool killed = monster.ApplyDamage(damage, nowUtc);
        if (killed)
        {
            runtime.RemoveMonster(monster);
        }

        return new MonsterDamageResult(monster, killed);
    }
}
