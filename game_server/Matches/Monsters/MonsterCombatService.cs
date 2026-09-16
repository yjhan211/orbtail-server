using game_server.players;
using network.common;

namespace game_server.matches.monsters;

/// <summary>피해 적용 결과. 처치했으면 Monster가 죽은 개체이고 SummonStoneReward는 해당 개체에 설정된 드롭 수다.</summary>
internal readonly record struct MonsterDamageResult(bool Applied, bool Killed, Monster? Monster, int SummonStoneReward)
{
    public static MonsterDamageResult None => new(false, false, null, 0);
}

/// <summary>
///     몬스터의 접촉 공격·플레이어 면역과 피격 시 주변 표적 지정·처치 보상을 조율한다.
///     개체 상태는 Monster가 보관하며, 모든 호출은 매치 잠금 안에서 수행한다.
/// </summary>
internal sealed class MonsterCombatService
{
    public void CollectContactDamage(MatchRuntime runtime, Monster monster, IReadOnlyList<Player> players, DateTime now, List<MonsterContactDamage> contacts)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Monster combat service requires the match lock.");
        }
        if (now < monster.NextContactAtUtc)
        {
            return;
        }

        float attackRange = monster.AttackRangeValue > Monster.BaseContactRadius ? monster.AttackRangeValue : Monster.GetContactRadius(monster.Kind);
        const float verticalScale = 2f;
        foreach (var player in players)
        {
            var position = player.Position;
            if (position == null || player.GameInfo.ObjectInfo.Area != monster.Area)
            {
                continue;
            }
            float dx = monster.Position.X - position.X;
            float dy = (monster.Position.Y - position.Y) * verticalScale;
            if (dx * dx + dy * dy > attackRange * attackRange)
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
            contacts.Add(new MonsterContactDamage(monster.MonsterId, player.PlayerId, monster.Area, monster.ContactDamageValue));
            break;
        }
    }

    public MonsterDamageResult ApplyMonsterDamage(MatchRuntime runtime, long combatTargetId, long attackerPlayerId, int damage, DateTime nowUtc)
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

        var monster = state.FindByCombatTarget(combatTargetId);
        if (monster != null)
        {
            monster.ReleaseReservedDamage(damage);
        }

        if (monster is not { Alive: true })
        {
            return MonsterDamageResult.None;
        }
        monster.ChaseTargetPlayerId = attackerPlayerId;
        foreach (var mate in state.Entities.Values)
        {
            if (!mate.Alive || mate.ChaseTargetPlayerId != 0 || mate.Area != monster.Area)
            {
                continue;
            }
            mate.ChaseTargetPlayerId = attackerPlayerId;
        }
        bool killed = monster.ApplyDamage(damage, nowUtc);
        int summonStoneReward = 0;
        if (killed)
        {
            summonStoneReward = monster.SummonStoneReward;
            runtime.RemoveMonster(monster);
        }

        return new MonsterDamageResult(true, killed, monster, summonStoneReward);
    }
}
