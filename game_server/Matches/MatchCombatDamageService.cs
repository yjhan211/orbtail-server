using network.common.data;
using game_server.players;
using network.common;
using network.common.data.models;

namespace game_server.matches;

/// <summary>
///     플레이어·몬스터 피해를 적용하고, 피해에 따르는 부수 효과(피격 알림·교전 표시·처치 보상)를 한 곳에서 처리한다.
///     플레이어의 체력 변경·탈락 처리는 PlayerHealthService에 위임한다.
/// </summary>
internal sealed class MatchCombatDamageService(PlayerHealthService healthService, MatchSynchronizationService synchronization)
{
    private static bool RollCritical(MatchRuntime runtime, double chance) => runtime.CriticalRng.NextDouble() < chance;

    internal void MarkAttacked(MatchRuntime runtime, Player victim, long attackerId, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat damage requires the match lock.");
        }
        if (victim.Interactions.Cancel() is { } interactId)
        {
            victim.State = PlayerState.IDLE;
            victim.Session?.SendDoorOpenInterrupted(interactId);
        }

        var bot = runtime.Bots.GetBot(victim.PlayerId);
        if (bot == null)
        {
            return;
        }

        if (attackerId != 0)
        {
            bot.LastAttackerPlayerId = attackerId;
        }
        bot.LastDamagedAtUtc = nowUtc;
    }

    internal void ApplyPlayerHit(MatchRuntime runtime, Player victim, long sourcePlayerId, Player? attacker, AreaType area, int weaponItemId, int damage, DateTime nowUtc, bool isPeriodicDamage = false)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat damage requires the match lock.");
        }
        if (runtime.IsEnded || victim.IsEliminated || damage <= 0)
        {
            return;
        }

        MarkAttacked(runtime, victim, sourcePlayerId, nowUtc);
        if (attacker != null)
        {
            attacker.PvpDamageDealt += damage;
        }

        var session = victim.Session;
        healthService.ApplyDamage(runtime, victim, damage, sourcePlayerId);

        var hit = new G_TO_C_COMBAT_HIT
        {
            AttackerId = sourcePlayerId,
            TargetId = victim.PlayerId,
            AreaType = area,
            WeaponItemId = weaponItemId,
            Damage = damage,
            AttackerHealth = attacker?.Health ?? -1,
            TargetHealth = victim.Health,
            IsDot = isPeriodicDamage
        };
        synchronization.QueueCombatHit(runtime, session, hit);
    }

    public void ApplyMonsterContactHit(MatchRuntime runtime, Player victim, int monsterId, int damage, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat damage requires the match lock.");
        }
        if (runtime.IsEnded || victim.IsEliminated || monsterId <= 0 || damage <= 0)
        {
            return;
        }

        MarkAttacked(runtime, victim, 0, nowUtc);
        var victimSession = victim.Session;
        var area = GameMapData.GetCurrentArea(victim.GameInfo.ObjectInfo.MapId, victim.GameInfo.ObjectInfo.Cell);
        healthService.ApplyDamage(runtime, victim, damage);

        var hit = new G_TO_C_COMBAT_HIT
        {
            AttackerId = monsterId,
            AttackerKind = CombatEntityKind.Monster,
            TargetId = victim.PlayerId,
            AreaType = area,
            Damage = damage,
            TargetHealth = victim.Health
        };
        synchronization.QueueAreaCombatHit(runtime, area, hit, victimSession);
    }

    public bool ApplyMonsterHit(MatchRuntime runtime, int monsterId, long attackerId, int weaponItemId, AreaType area, int damage, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat damage requires the match lock.");
        }
        if (damage <= 0 || !runtime.Monsters.IsInitialized || runtime.Monsters.Find(monsterId) is not { Alive: true } monster)
        {
            return false;
        }

        bool critical = RollCritical(runtime, Config.SWARM_CRITICAL_CHANCE);
        if (critical)
        {
            damage = Math.Max(damage + 1, (int)MathF.Round(damage * Config.SWARM_CRITICAL_MULTIPLIER));
        }

        var monsterArea = GameMapData.GetCurrentArea(monster.Info.ObjectInfo.MapId, monster.Info.ObjectInfo.Cell);
        bool killed = monster.ApplyDamage(damage, nowUtc);
        synchronization.QueueMonsterHitForAttacker(runtime, runtime.GetPlayer(attackerId), monsterId, area, weaponItemId, damage, critical, showDamageOnly: true);
        if (!killed)
        {
            return true;
        }

        runtime.RemoveMonster(monster);
        int stoneCount = Math.Max(0, monster.SummonStoneReward);
        int heartCount = Math.Max(0, monster.HeartReward);
        if (stoneCount + heartCount > 0)
        {
            int[] itemIds = new int[stoneCount + heartCount];
            for (int index = 0; index < itemIds.Length; index++)
            {
                itemIds[index] = index < stoneCount ? Config.SUMMON_STONE_GROUND_ITEM_ID : Config.HEART_GROUND_ITEM_ID;
            }
            runtime.GroundItems.SpawnItems(monsterArea, monster.Position.X, monster.Position.Y, itemIds);
        }

        return true;
    }

    public void ApplyOrbShock(MatchRuntime runtime,
        long ownerId,
        int weaponItemId,
        AreaType area,
        Player victim,
        DateTime nowUtc,
        float damageScale = 1f,
        bool isPeriodicDamage = false)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat damage requires the match lock.");
        }
        if (runtime.IsEnded || victim.IsEliminated)
        {
            return;
        }

        int shock = Math.Max(1, (int)MathF.Round(Config.ScaleSwarmDamageTaken(Config.SWARM_ORB_SHOCK_DAMAGE) * damageScale));
        if (victim.StatusEffects.IsActive(PlayerStatusEffectKind.Wound, nowUtc) && RollCritical(runtime, Config.SWARM_WIND_WOUND_CRIT_CHANCE))
        {
            shock = Math.Max(shock + 1, (int)MathF.Round(shock * Config.SWARM_CRITICAL_MULTIPLIER));
        }

        var owner = runtime.GetPlayer(ownerId);
        ApplyPlayerHit(runtime, victim, ownerId, owner, area, weaponItemId, shock, nowUtc, isPeriodicDamage);
        synchronization.QueuePlayerHitForAttacker(runtime, owner, victim.PlayerId, area, weaponItemId, shock, victim.Health, isPeriodicDamage);
    }
}
