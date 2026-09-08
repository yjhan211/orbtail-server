using game_server.services;
using game_server.sessions;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.matches;

/// <summary>
///     오브 공격의 치명타·몬스터 피해·처치 보상과 플레이어 충격을 적용한다.
///     상태는 각 매치가 소유하며 호출자는 매치 잠금을 보유한다.
///     상태 변경 뒤 피격·드롭 패킷을 보내며 전송 실패로 적용한 피해를 되돌리지 않는다.
/// </summary>
internal sealed class MatchCombatDamageService(
    MatchRuntimeStore matchRuntimes,
    GameEventLogManager eventLogs,
    ILogger<MatchCombatDamageService> logger)
{
    public void SendPlayerHitNotification(GameClientSession? attackerSession, long targetPlayerId, AreaType area, int weaponItemId, int damage, int targetHealth, bool isPeriodicDamage = false)
    {
        if (attackerSession == null || !attackerSession.PlayerId.HasValue || targetPlayerId == 0)
        {
            return;
        }
        using var packet = PacketMaker.G_TO_C_COMBAT_HIT(new G_TO_C_COMBAT_HIT
        {
            AttackerId = attackerSession.PlayerId.Value,
            TargetId = targetPlayerId,
            AreaType = area,
            WeaponItemId = weaponItemId,
            Damage = damage,
            AttackerHealth = attackerSession.CurrentHealth,
            TargetHealth = targetHealth,
            IsDot = isPeriodicDamage
        });
        attackerSession.TrySend(packet);
    }

    public void SendMonsterHitNotification(GameClientSession? attackerSession, int monsterId, AreaType area, int weaponItemId, int damage, bool critical = false, bool showDamageOnly = false)
    {
        if (attackerSession == null || !attackerSession.PlayerId.HasValue || attackerSession.IsEliminated || monsterId < 0 || weaponItemId <= 0 || damage <= 0) return;
        using var packet = PacketMaker.G_TO_C_COMBAT_HIT(new G_TO_C_COMBAT_HIT
        {
            AttackerId = attackerSession.PlayerId.Value,
            TargetId = monsterId,
            TargetKind = CombatEntityKind.Monster,
            AreaType = area,
            WeaponItemId = weaponItemId,
            Damage = damage,
            AttackerHealth = attackerSession.CurrentHealth,
            IsCritical = critical,
            ShowDamageOnly = showDamageOnly
        });
        attackerSession.TrySend(packet);
    }

    /// <summary>일반 피격의 로그·체력 변경·결과 전송을 매치 잠금 안에서 처리한다.</summary>
    public void ApplyProximityAutoCombatHit(
        GameClientSession victimSession, long sourcePlayerId, AreaType area, int weaponItemId,
        int damage, bool isPeriodicDamage = false, int sourceHealth = -1)
    {
        if (!victimSession.PlayerId.HasValue || victimSession.IsEliminated || damage <= 0)
        {
            return;
        }
        eventLogs.LogHit(victimSession.MatchingId, sourcePlayerId, victimSession.PlayerId.Value, weaponItemId, damage,
            victimSession.CurrentHealth > 0 && victimSession.CurrentHealth - damage <= 0,
            BotPlayerManager.IsBotPlayerId(sourcePlayerId), DateTimeOffset.UtcNow);

        var change = victimSession.Condition.ApplyDamage(damage);
        victimSession.HealthChanges.Handle(change, attackerPlayerId: sourcePlayerId);
        using var packet = PacketMaker.G_TO_C_COMBAT_HIT(new G_TO_C_COMBAT_HIT
        {
            AttackerId = sourcePlayerId,
            TargetId = victimSession.PlayerId.Value,
            AreaType = area,
            WeaponItemId = weaponItemId,
            Damage = damage,
            AttackerHealth = sourcePlayerId == victimSession.PlayerId.Value ? victimSession.CurrentHealth : sourceHealth,
            TargetHealth = victimSession.CurrentHealth,
            IsDot = isPeriodicDamage
        });
        victimSession.TrySend(packet);
    }


    /// <summary>몬스터 피해를 적용하고 같은 피해량을 클라이언트에 알린다.</summary>
    public void ApplySwarmAfterimageMonsterHit(
        GameClientSession victimSession, int monsterId, int damage)
    {
        if (!victimSession.PlayerId.HasValue || victimSession.IsEliminated || monsterId <= 0 || damage <= 0) return;
        int healthBefore = victimSession.CurrentHealth;
        int healthAfter = Math.Max(0, healthBefore - damage);
        bool isLethal = healthBefore > 0 && healthAfter <= 0;
        eventLogs.LogSwarmAfterimageHit(
            victimSession.MatchingId, monsterId, victimSession.PlayerId.Value, victimSession.CurrentArea.ToString(), damage,
            healthBefore, healthAfter, isLethal, isBot: false, DateTimeOffset.UtcNow);
        logger.LogInformation(
            "Emotion afterimage attack: MatchingId={MatchingId}, MonsterId={MonsterId}, Target={Target}, TargetKind=Human, Damage={Damage}, HealthBefore={HealthBefore}, HealthAfter={HealthAfter}, Killed={Killed}",
            victimSession.MatchingId, monsterId, victimSession.PlayerId.Value, damage, healthBefore, healthAfter, isLethal);
        var change = victimSession.Condition.ApplyDamage(damage);
        victimSession.HealthChanges.Handle(change);
        using var packet = PacketMaker.G_TO_C_COMBAT_HIT(new G_TO_C_COMBAT_HIT
        {
            AttackerId = monsterId,
            AttackerKind = CombatEntityKind.Monster,
            TargetId = victimSession.PlayerId.Value,
            AreaType = victimSession.CurrentArea,
            Damage = damage,
            TargetHealth = victimSession.CurrentHealth
        });
        victimSession.TrySend(packet);
    }

    private static double SwarmCriticalChance => SwarmConfigData.GetDouble("SWARM_CRITICAL_CHANCE", 0.15d);
    private static float SwarmCriticalMultiplier => SwarmConfigData.GetFloat("SWARM_CRITICAL_MULTIPLIER", 2f);

    /// <summary>PvE 치명타 굴림 — 적중이면 배율을 적용한 피해를 돌려준다.</summary>
    public int RollSwarmCriticalDamage(long matchingId, int damage, out bool critical)
    {
        critical = matchRuntimes.GetRequired(matchingId).Swarm.Pacing.RollCritical(SwarmCriticalChance);
        return critical
            ? Math.Max(damage + 1, (int)MathF.Round(damage * SwarmCriticalMultiplier))
            : damage;
    }

    private void SpawnSwarmSummonStone(
        long matchingId,
        MonsterRuntimeInfo defeatedWave,
        IReadOnlyCollection<GameClientSession> sessions,
        int heartReward = 0,
        int bootsReward = 0)
    {
        if (defeatedWave.SummonStoneReward <= 0 && heartReward <= 0 &&
            bootsReward <= 0)
            return;

        // #229 5단계: 스웜에서는 이동속도(부츠)·열쇠를 떨구지 않는다. 기동력은 바람 오브가
        // 맡고, 폐쇄 문은 시간이 여닫는 것이라 열쇠로 뚫는 예외가 없다 — 몹이 떨구면 바닥에
        // 쓰지 못하는 아이템만 쌓인다.
        // 하트는 떨군다(회복이 수면밖에 없다는 결정): 상자 탐색을 끈 뒤로 즉시 회복 공급처가 통째로 사라지므로,
        // 일반 몹 3% 드롭을 이 게이트가 스폰 직전에 지우면 안 된다.
        if (Config.IsSwarmExploreDisabled())
        {
            bootsReward = 0;
        }

        // 소환석은 바닥에 떨어진다 (즉시 귀속 철회): 처치자도 다른 플레이어와 같은 픽업 경쟁 규칙으로 줍는다.
        // 클라는 재화를 자석 반경에서 몸으로 끌어와 픽업을 요청하므로 동선 부담은 작다.
        int groundStoneReward = defeatedWave.SummonStoneReward;

        if (groundStoneReward <= 0 && heartReward <= 0 &&
            bootsReward <= 0)
            return;

        // 하트·부츠 (#222 M4): 소환석과 함께 흩어진다 — 픽업 경쟁 규칙 공유.
        // 잼 낙수는 잼 승점 퇴역과 함께 제거 (#226 D).
        var itemIds = Enumerable.Repeat(
                Config.SUMMON_STONE_GROUND_ITEM_ID, Math.Max(0, groundStoneReward))
            .Concat(Enumerable.Repeat(Config.HEART_GROUND_ITEM_ID, Math.Max(0, heartReward)))
            .Concat(Enumerable.Repeat(Config.BOOTS_GROUND_ITEM_ID, Math.Max(0, bootsReward)))
            .ToArray();
        var spawned = matchRuntimes.GetRequired(matchingId).GroundItems.SpawnItems(
            defeatedWave.AreaType,
            defeatedWave.PositionX,
            defeatedWave.PositionY,
            itemIds,
            mapId: Config.SWARM_MATCH_MAP,
            layout: GroundItemSpawnLayout.EliminationScatter);

        foreach (var item in spawned)
        {
            eventLogs.LogGroundItemSpawned(
                matchingId,
                0,
                item.GroundItemUid,
                item.ItemId,
                defeatedWave.AreaType.ToString(),
                0,
                isBot: false);
        }

        using var packet = PacketMaker.G_TO_C_GROUND_ITEM_SPAWN(
            (int)defeatedWave.AreaType,
            spawned.ToList());
        foreach (var session in sessions.Where(session => session.CurrentArea == defeatedWave.AreaType))
            session.TrySend(packet);
    }

    /// <summary>
    ///     즉시 몬스터 타격 (교차사격 쓸기). 착탄 지연 큐와 같은 정산 —
    ///     결과 집계, 처치 계측(monster_lifetime), 처치 로그, 소환석 드롭. 공격자 화면엔 숫자만
    ///     띄운다(투사체 없음).
    /// </summary>
    public void ApplySwarmMonsterHitNow(
        long matchingId,
        long combatTargetId,
        int monsterId,
        long attackerId,
        int weaponItemId,
        AreaType area,
        int damage,
        bool critical,
        List<GameClientSession> allSessions)
    {
        var damageResult = matchRuntimes.GetRequired(matchingId).Monsters.ApplyMonsterDamage(matchingId, combatTargetId, attackerId, damage);
        if (!damageResult.Applied)
            return;

        eventLogs.RecordMonsterHit(matchingId, attackerId, damage, damageResult.Killed);
        var attackerSession = allSessions.FirstOrDefault(session => session.PlayerId == attackerId);
        SendMonsterHitNotification(attackerSession,
            monsterId, area, weaponItemId, damage, critical, showDamageOnly: true);

        if (damageResult.Killed && damageResult.MonsterState != null)
            SettleSwarmMonsterKill(matchingId, damageResult, attackerId, damage, allSessions);
    }

    /// <summary>처치 정산 — 착탄 큐와 즉시 타격이 같은 경로를 쓴다.</summary>
    public void SettleSwarmMonsterKill(
        long matchingId,
        SwarmArenaDamageResult damageResult,
        long attackerId,
        int damage,
        List<GameClientSession> allSessions)
    {
        if (damageResult.MonsterState == null)
            return;

        // 기준점 계측 (#232 1단계): 종·생존초·살아 있는 동안 받은 공격 사건 수. 완료 조건
        // "몬스터당 공격 모양 평균 2회 이상"과 "즉시 지워져 기준점이 못 되는 몹"을 여기서 잰다.
        eventLogs.LogSystem(
            matchingId,
            $"monster_lifetime kind={damageResult.Kind} area={damageResult.MonsterState.AreaType} " +
            $"aliveSeconds={damageResult.AliveSeconds:F1} attackEvents={damageResult.AttackEventCount} " +
            $"killer={attackerId}");

        // 처치 계측 (#226 E): 종·구역·처치자 — 요약의 몹 처치 지표가 이 이벤트를 읽는다.
        eventLogs.LogSwarmAfterimageKilled(
            matchingId, damageResult.MonsterId,
            damageResult.MonsterState.AreaType.ToString(),
            isCore: damageResult.Kind == SwarmMonsterKind.RunawayGoblin,
            firstAttackerPlayerId: attackerId,
            lastAttackerPlayerId: attackerId,
            new Dictionary<long, int> { [attackerId] = damage });
        SpawnSwarmSummonStone(
            matchingId, damageResult.MonsterState, allSessions,
            damageResult.HeartReward, damageResult.BootsReward);
    }

    /// <summary>
    ///     플레이어 충격 (고정값, 티어 무관): 사격 피격 경로를 재사용해 체력 감소·피격 숫자·탈락 흐름이 그대로
    ///     따라온다. 봇도 같은 값. 소유자 화면에는 사격 피드백을 보낸다. label은 로그용(어느 모양이 때렸나).
    /// </summary>
    public void ApplySwarmShock(
        long matchingId,
        long ownerId,
        int weaponItemId,
        AreaType area,
        long victimId,
        string label,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots,
        List<GameClientSession> allSessions,
        float damageScale = 1f,
        bool isPeriodicDamage = false)
    {
        SwarmMatchRuntime runtime = matchRuntimes.GetRequired(matchingId).Swarm;
        // 받는 피해 배율: 고정 충격 50에 1/3을 곱한다. 태양·바람·파도 충격이 전부 이 한 곳을 지난다.
        // damageScale: 파도 소용돌이(#268)는 당김이 본체라 피해를 타격 피드백 수준(1/4)으로 줄인다.
        int shock = Math.Max(1, (int)MathF.Round(
            Config.ScaleSwarmDamageTaken(Config.SWARM_CROSSFIRE_SHOCK_DAMAGE) * damageScale));
        // 상처 (#268): 상처 입은 피해자만 PvP 충격 치명타가 열린다 — PvE와 같은 2배.
        if (runtime.WindBlade.IsWounded(victimId, DateTime.UtcNow) &&
            runtime.Pacing.RollCritical(Config.SWARM_WIND_WOUND_CRIT_CHANCE))
            shock = Math.Max(shock + 1, (int)MathF.Round(shock * SwarmCriticalMultiplier));
        int healthBefore;
        int healthAfter;

        var victimSession = aliveSessions.FirstOrDefault(session => session.PlayerId == victimId);
        var ownerSession = allSessions.FirstOrDefault(session => session.PlayerId == ownerId);
        int ownerHealth = ownerSession?.CurrentHealth ?? aliveBots.FirstOrDefault(bot => bot.PlayerId == ownerId)?.Health ?? -1;
        if (victimSession != null)
        {
            healthBefore = victimSession.CurrentHealth;
            // 사격 피격 경로 재사용 — 체력 감소·피격 숫자·탈락 흐름이 그대로 따라온다.
            ApplyProximityAutoCombatHit(victimSession, ownerId, area, weaponItemId, shock, isPeriodicDamage, ownerHealth);
            healthAfter = victimSession.CurrentHealth;
        }
        else
        {
            var bot = aliveBots.FirstOrDefault(candidate => candidate.PlayerId == victimId);
            if (bot == null)
                return;
            healthBefore = bot.Health;
            bot.LastProximityAttackerPlayerId = ownerId;
            runtime.BotTactics.LastDamagedAtUtc[(matchingId, bot.PlayerId)] = DateTime.UtcNow;
            bot.LastDamagedAtUtc = DateTime.UtcNow;
            eventLogs.LogHit(
                matchingId, ownerId, bot.PlayerId, weaponItemId, shock,
                bot.Health > 0 &&
                bot.Health - shock <= 0,
                BotPlayerManager.IsBotPlayerId(ownerId), DateTimeOffset.UtcNow);
            bot.Health = Math.Max(0, bot.Health - shock);
            healthAfter = bot.Health;
        }

        SendPlayerHitNotification(ownerSession, victimId, area, weaponItemId, shock, healthAfter, isPeriodicDamage);

        eventLogs.LogSystem(
            matchingId,
            $"{label} owner={ownerId} victim={victimId} weapon={weaponItemId} " +
            $"healthBefore={healthBefore} healthAfter={healthAfter}");
    }

}
