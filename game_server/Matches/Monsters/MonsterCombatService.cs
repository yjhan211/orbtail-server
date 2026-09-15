using network.common;

namespace game_server.matches.monsters;

/// <summary>피해 적용 결과. 처치했으면 Monster가 죽은 개체이고 SummonStoneReward는 예산에서 떼어낸 드롭 수다.</summary>
internal readonly record struct MonsterDamageResult(bool Applied, bool Killed, Monster? Monster, int SummonStoneReward)
{
    public static MonsterDamageResult None => new(false, false, null, 0);
}

/// <summary>
///     몬스터의 접촉 공격·플레이어 면역과 피격 시 주변 어그로·처치 보상을 조율한다.
///     개체 상태는 Monster가 보관하며, 모든 호출은 매치 잠금 안에서 수행한다.
/// </summary>
internal sealed class MonsterCombatService(MatchMonsterSpawnService spawns)
{
    public void CollectContactDamage(MatchRuntime runtime, Monster monster, IReadOnlyList<PlayerPositionSnapshot> snapshot, DateTime now, List<MonsterContactDamage> contacts)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Monster combat service requires the match lock.");
        }
        if (now < monster.NextContactAtUtc)
        {
            return;
        }

        float attackRange = monster.Aggro && monster.AttackRangeValue > Monster.BaseContactRadius ? monster.AttackRangeValue : Monster.GetContactRadius(monster.Kind);
        const float verticalScale = 2f;
        foreach (var participant in snapshot)
        {
            if (participant.Area != monster.Area)
            {
                continue;
            }
            float dx = monster.Position.X - participant.Position.X;
            float dy = (monster.Position.Y - participant.Position.Y) * verticalScale;
            if (dx * dx + dy * dy > attackRange * attackRange)
            {
                continue;
            }

            var player = runtime.GetParticipant(participant.PlayerId);
            if (player == null || now < player.MonsterContactImmuneUntilUtc)
            {
                continue;
            }

            monster.NextContactAtUtc = now.AddSeconds(monster.AttackCooldownValue);
            player.MonsterContactImmuneUntilUtc = now.AddSeconds(Config.SWARM_MONSTER_CONTACT_IMMUNITY_SECONDS);
            monster.Aggro = true;
            monster.ChaseTargetPlayerId = participant.PlayerId;
            contacts.Add(new MonsterContactDamage(monster.MonsterId, participant.PlayerId, monster.Area, monster.ContactDamageValue));
            bool waveInsignia = monster.Insignia == MonsterInsignia.Wave;
            if (waveInsignia || monster.Kind == MonsterKind.Bowler)
            {
                float splashRadius = waveInsignia ? Config.SWARM_MONSTER_WAVE_SPLASH_RADIUS : Config.SWARM_MONSTER_BOWLER_SPLASH_RADIUS;
                foreach (var splashed in snapshot)
                {
                    if (splashed.PlayerId == participant.PlayerId || splashed.Area != monster.Area)
                    {
                        continue;
                    }
                    float sx = splashed.Position.X - participant.Position.X;
                    float sy = splashed.Position.Y - participant.Position.Y;
                    if (sx * sx + sy * sy > splashRadius * splashRadius)
                    {
                        continue;
                    }

                    var splashedPlayer = runtime.GetParticipant(splashed.PlayerId);
                    if (splashedPlayer == null || now < splashedPlayer.MonsterContactImmuneUntilUtc)
                    {
                        continue;
                    }
                    splashedPlayer.MonsterContactImmuneUntilUtc = now.AddSeconds(Config.SWARM_MONSTER_CONTACT_IMMUNITY_SECONDS);
                    contacts.Add(new MonsterContactDamage(monster.MonsterId, splashed.PlayerId, monster.Area, monster.ContactDamageValue));
                }
            }
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

        monster.Aggro = true;
        if (monster.OwnerPlayerId == 0)
        {
            monster.ChaseTargetPlayerId = attackerPlayerId;
        }
        foreach (var mate in state.Entities.Values)
        {
            if (!mate.Alive || mate.Aggro || mate.Area != monster.Area)
            {
                continue;
            }
            mate.Aggro = true;
            if (mate.OwnerPlayerId == 0)
            {
                mate.ChaseTargetPlayerId = attackerPlayerId;
            }
        }
        bool killed = monster.ApplyDamage(damage, nowUtc);
        int summonStoneReward = 0;
        if (killed)
        {
            summonStoneReward = spawns.ConsumeSupplyStoneBudget(runtime, monster, nowUtc);
            runtime.RemoveMonster(monster);
        }

        return new MonsterDamageResult(true, killed, monster, summonStoneReward);
    }
}
