using network.common.data;
using game_server.matches.monsters;
using game_server.players;
using game_server.sessions;
using MessagePack;
using network.common;
using network.common.data.models;

namespace game_server.matches;

/// <summary>
///     플레이어·몬스터 피해를 적용하고, 틱 끝에 보낼 피격 알림과 상태 효과 알림을 모은다.
///     플레이어의 체력 변경·탈락 처리는 PlayerHealthService에 위임한다.
/// </summary>
internal sealed class MatchCombatDamageService(MonsterCombatService monsters, PlayerHealthService healthService)
{
    private static bool RollCritical(MatchRuntime runtime, double chance) => runtime.CriticalRng.NextDouble() < chance;
    internal void QueuePlayerHitNotification(MatchRuntime runtime, Player? attacker, long targetPlayerId, AreaType area, int weaponItemId, int damage, int targetHealth, bool isPeriodicDamage = false)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat damage requires the match lock.");
        }
        if (attacker == null || attacker.PlayerId == 0 || targetPlayerId == 0)
        {
            return;
        }

        var hit = new G_TO_C_COMBAT_HIT
        {
            AttackerId = attacker.PlayerId,
            TargetId = targetPlayerId,
            AreaType = area,
            WeaponItemId = weaponItemId,
            Damage = damage,
            AttackerHealth = attacker.Health,
            TargetHealth = targetHealth,
            IsDot = isPeriodicDamage
        };
        if (attacker.Session != null)
        {
            runtime.PendingCombatHits.Enqueue((attacker.Session, hit));
        }
    }

    internal void QueueMonsterHitNotification(MatchRuntime runtime, Player? attacker, int monsterId, AreaType area,
        int weaponItemId, int damage, bool critical = false, bool showDamageOnly = false)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat damage requires the match lock.");
        }

        if (attacker == null || attacker.PlayerId == 0 || attacker.IsEliminated || monsterId < 0 || weaponItemId <= 0 || damage <= 0)
        {
            return;
        }
        var hit = new G_TO_C_COMBAT_HIT
        {
            AttackerId = attacker.PlayerId,
            TargetId = monsterId,
            TargetKind = CombatEntityKind.Monster,
            AreaType = area,
            WeaponItemId = weaponItemId,
            Damage = damage,
            AttackerHealth = attacker.Health,
            IsCritical = critical,
            ShowDamageOnly = showDamageOnly
        };
        if (attacker.Session != null)
        {
            runtime.PendingCombatHits.Enqueue((attacker.Session, hit));
        }
    }

    public void ApplyPlayerHit(MatchRuntime runtime, Player victim, long sourcePlayerId, AreaType area, int weaponItemId, int damage, DateTime nowUtc, bool isPeriodicDamage = false, int sourceHealth = -1)
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
        if (runtime.GetPlayer(sourcePlayerId) is { } attackerPlayer)
        {
            attackerPlayer.PvpDamageDealt += damage;
        }

        var session = victim.Session;
        healthService.ApplyDamage(runtime, victim, damage, sourcePlayerId);

        if (session == null)
        {
            return;
        }

        var hit = new G_TO_C_COMBAT_HIT
        {
            AttackerId = sourcePlayerId,
            TargetId = victim.PlayerId,
            AreaType = area,
            WeaponItemId = weaponItemId,
            Damage = damage,
            AttackerHealth = sourcePlayerId == victim.PlayerId ? victim.Health : sourceHealth,
            TargetHealth = victim.Health,
            IsDot = isPeriodicDamage
        };
        runtime.PendingCombatHits.Enqueue((session, hit));
    }

    /// <summary>
    ///     공격당한 플레이어의 반응을 처리한다. 진행 중인 문 열기를 끊고, 봇이면 도주 판단에 쓰는 피격 시각과 공격자를 남긴다.
    ///     몬스터에게 맞았을 때는 attackerId를 0으로 넘기며, 이때 봇의 마지막 공격자는 바꾸지 않는다.
    /// </summary>
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
        var session = victim.Session;
        healthService.ApplyDamage(runtime, victim, damage);

        if (session == null)
        {
            return;
        }

        var hit = new G_TO_C_COMBAT_HIT
        {
            AttackerId = monsterId,
            AttackerKind = CombatEntityKind.Monster,
            TargetId = victim.PlayerId,
            AreaType = GameMapData.GetCurrentArea(victim.GameInfo.ObjectInfo.MapId, victim.GameInfo.ObjectInfo.Cell),
            Damage = damage,
            TargetHealth = victim.Health
        };
        runtime.PendingCombatHits.Enqueue((session, hit));
    }

    public int RollCriticalDamage(MatchRuntime runtime, int damage, out bool critical)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat damage requires the match lock.");
        }
        critical = RollCritical(runtime, Config.SWARM_CRITICAL_CHANCE);
        return critical ? Math.Max(damage + 1, (int)MathF.Round(damage * Config.SWARM_CRITICAL_MULTIPLIER)) : damage;
    }

    public void ApplyMonsterHit(MatchRuntime runtime, int monsterId, long attackerId, int weaponItemId, AreaType area, int damage, bool critical, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat damage requires the match lock.");
        }
        var damageResult = monsters.ApplyMonsterDamage(runtime, monsterId, attackerId, damage, nowUtc);
        if (!damageResult.Applied)
        {
            return;
        }

        var attacker = runtime.GetPlayer(attackerId);
        QueueMonsterHitNotification(runtime, attacker, monsterId, area, weaponItemId, damage, critical, showDamageOnly: true);
        if (!damageResult.Killed || damageResult.Monster is not { } defeated)
        {
            return;
        }

        int stoneCount = Math.Max(0, damageResult.SummonStoneReward);
        int heartCount = Math.Max(0, defeated.HeartReward);
        if (stoneCount + heartCount == 0)
        {
            return;
        }

        int[] itemIds = new int[stoneCount + heartCount];
        for (int index = 0; index < itemIds.Length; index++)
        {
            itemIds[index] = index < stoneCount ? Config.SUMMON_STONE_GROUND_ITEM_ID : Config.HEART_GROUND_ITEM_ID;
        }
        runtime.GroundItems.SpawnItems(GameMapData.GetCurrentArea(defeated.Info.ObjectInfo.MapId, defeated.Info.ObjectInfo.Cell), defeated.Position.X, defeated.Position.Y, itemIds);
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
        int ownerHealth = owner?.Health ?? -1;
        ApplyPlayerHit(runtime, victim, ownerId, area, weaponItemId, shock, nowUtc, isPeriodicDamage, ownerHealth);
        QueuePlayerHitNotification(runtime, owner, victim.PlayerId, area, weaponItemId, shock, victim.Health, isPeriodicDamage);
    }

    /// <summary>오브 공격이 건 상태 효과를 당한 플레이어의 세션에 알린다. 세션이 없는 봇은 건너뛴다.</summary>
    public void QueueStatusEffect(MatchRuntime runtime, Player target, long sourcePlayerId, AreaType area, CombatStatusEffectKind effect, float seconds)
    {
        if (target.Session is not { PlayerId: not null } session)
        {
            return;
        }

        QueueSessionEffect(runtime, session, Protocol.G_TO_C_STATUS_EFFECT, new G_TO_C_STATUS_EFFECT
        {
            SourcePlayerId = sourcePlayerId,
            TargetPlayerId = target.PlayerId,
            AreaType = area,
            Effect = effect,
            DurationMs = (int)(seconds * 1000f)
        });
    }

    /// <summary>같은 구역에 있는 세션 전원에게 틱 끝에 보낼 패킷을 넣는다. 본문은 한 번만 직렬화한다.</summary>
    public void QueueAreaEffect<T>(MatchRuntime runtime, AreaType area, Protocol protocol, T body) where T : IMessagePackObject
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat damage requires the match lock.");
        }

        byte[]? serialized = null;
        foreach (var session in runtime.GetSessions())
        {
            if (session.IsGameEnded || !session.PlayerId.HasValue)
            {
                continue;
            }
            if (GameMapData.GetCurrentArea(session.Player.GameInfo.ObjectInfo.MapId, session.Player.GameInfo.ObjectInfo.Cell) != area)
            {
                continue;
            }

            serialized ??= MessagePackSerializer.Serialize(body);
            runtime.PendingCombatEffects.Enqueue((session, protocol, serialized));
        }
    }

    public void QueueSessionEffect<T>(MatchRuntime runtime, GameClientSession session, Protocol protocol, T body) where T : IMessagePackObject
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Combat damage requires the match lock.");
        }
        runtime.PendingCombatEffects.Enqueue((session, protocol, MessagePackSerializer.Serialize(body)));
    }
}
