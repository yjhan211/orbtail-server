using game_server.players;
using game_server.matches;
using game_server.players.bots;
using game_server.items;
using game_server.logging;
using game_server.monsters;
using game_server.sessions;
using Microsoft.Extensions.Logging;
using MessagePack;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.combat;

/// <summary>
///     오브 공격의 치명타·몬스터 피해·처치 보상과 플레이어 충격을 적용한다.
///     매치마다 생성되며 예약된 몬스터·PvP 피해, 소수점 잔여 피해와 치명타 난수를 관리한다. 호출자는 매치 잠금을 보유한다.
///     상태 변경 뒤 피격·드롭 패킷을 보내며 전송 실패로 적용한 피해를 되돌리지 않는다.
/// </summary>
internal sealed class MatchCombatDamageService(
    MatchRuntime runtime,
    GameEventLogManager eventLogs,
    ILogger<MatchCombatDamageService> logger)
{
    private readonly List<PendingMonsterHit> _pendingMonsterHits = new();
    private readonly List<(ProximityCombatAttack Attack, DateTime DueAtUtc)> _pendingPvpHits = new();
    private const int SwarmRingVfxKindRetaliationBlocked = 6;

    private readonly Random _criticalRng = new();
    private bool RollCritical(double chance) => _criticalRng.NextDouble() < chance;

    private readonly Dictionary<long, float> _pvpDamageCarry = new();
    private static float SwarmPvpDamagePerDamage =>
        SwarmConfigData.GetFloat("SWARM_PVP_DAMAGE_PER_DAMAGE", 0.12f);

    /// <summary>착탄 예정 피해를 이 매치의 대기열에 추가한다.</summary>
    public void ScheduleMonsterHit(PendingMonsterHit hit) => _pendingMonsterHits.Add(hit);

    public void SchedulePvpHit(ProximityCombatAttack attack, DateTime dueAtUtc) =>
        _pendingPvpHits.Add((attack, dueAtUtc));

    public void ProcessPendingPvpHits(PlayerEliminationService eliminations, DateTime nowUtc, IReadOnlyList<Player> players, List<GameClientSession> sessions)
    {
        for (int index = _pendingPvpHits.Count - 1; index >= 0; index--)
        {
            var pending = _pendingPvpHits[index];
            if (nowUtc < pending.DueAtUtc)
                continue;
            _pendingPvpHits.RemoveAt(index);
            ApplySwarmPvpAttack(eliminations, pending.Attack, players, sessions, broadcastVfx: false);
            if (runtime.IsEnded)
                return;
        }
    }

    /// <summary>피해자별 소수점 잔여 피해를 누적하고 이번 공격에 적용할 정수 체력 피해를 반환한다.</summary>
    internal int ConsumeSwarmPvpDamage(long victimId, int rawDamage)
    {
        float total = (_pvpDamageCarry.TryGetValue(victimId, out float carry) ? carry : 0f) +
                      rawDamage * SwarmPvpDamagePerDamage;
        int whole = (int)total;
        _pvpDamageCarry[victimId] = total - whole;
        return whole;
    }
    public void SendPlayerHitNotification(Player? attacker, long targetPlayerId, AreaType area, int weaponItemId, int damage, int targetHealth, bool isPeriodicDamage = false)
    {
        if (attacker == null || attacker.PlayerId == 0 || targetPlayerId == 0)
        {
            return;
        }
        using var packet = PacketMaker.G_TO_C_COMBAT_HIT(new G_TO_C_COMBAT_HIT
        {
            AttackerId = attacker.PlayerId,
            TargetId = targetPlayerId,
            AreaType = area,
            WeaponItemId = weaponItemId,
            Damage = damage,
            AttackerHealth = attacker.Health,
            TargetHealth = targetHealth,
            IsDot = isPeriodicDamage
        });
        attacker.Session?.TrySend(packet);
    }

    public void SendMonsterHitNotification(Player? attacker, int monsterId, AreaType area, int weaponItemId, int damage, bool critical = false, bool showDamageOnly = false)
    {
        if (attacker == null || attacker.PlayerId == 0 || attacker.IsEliminated || monsterId < 0 || weaponItemId <= 0 || damage <= 0) return;
        using var packet = PacketMaker.G_TO_C_COMBAT_HIT(new G_TO_C_COMBAT_HIT
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
        });
        attacker.Session?.TrySend(packet);
    }

    /// <summary>일반 피격의 로그·체력 변경·결과 전송을 매치 잠금 안에서 처리한다.</summary>
    public void ApplyProximityAutoCombatHit(PlayerEliminationService eliminations,
        Player victim, long sourcePlayerId, AreaType area, int weaponItemId,
        int damage, bool isPeriodicDamage = false, int sourceHealth = -1)
    {
        if (runtime.IsEnded || victim.IsEliminated || damage <= 0) return;

        RecordCombatContact(victim, sourcePlayerId);
        eventLogs.LogHit(runtime.MatchingId, sourcePlayerId, victim.PlayerId, weaponItemId, damage,
            victim.Health > 0 && victim.Health - damage <= 0,
            BotPlayerManager.IsBotPlayerId(sourcePlayerId), DateTimeOffset.UtcNow);

        var change = victim.ApplyDamage(damage);
        var session = victim.Session;
        PlayerHealthChangeService.Record(runtime.MatchingId, victim, change, eventLogs, logger);
        if (change.IsDepleted)
            eliminations.EliminatePlayer(runtime.MatchingId, victim.PlayerId, EliminationReason.HEALTH_ZERO, attackerPlayerId: sourcePlayerId);

        if (session == null) return;
        using var packet = PacketMaker.G_TO_C_COMBAT_HIT(new G_TO_C_COMBAT_HIT
        {
            AttackerId = sourcePlayerId,
            TargetId = victim.PlayerId,
            AreaType = area,
            WeaponItemId = weaponItemId,
            Damage = damage,
            AttackerHealth = sourcePlayerId == victim.PlayerId ? victim.Health : sourceHealth,
            TargetHealth = victim.Health,
            IsDot = isPeriodicDamage
        });
        session.TrySend(packet);
    }

    private void RecordCombatContact(Player victim, long attackerId)
    {
        var nowUtc = DateTime.UtcNow;
        victim.MarkSwarmCombat(nowUtc);
        if (victim.InterruptDoor() is { } interactId)
        {
            eventLogs.LogExploreCancelled(runtime.MatchingId, victim.PlayerId, interactId,
                victim.CurrentArea.ToString(), "door_unlock_hit", isBot: victim.PlayerId < 0);
            victim.Session?.SendDoorOpenInterrupted(interactId);
        }
        var bot = runtime.Bots.GetBots(runtime.MatchingId).FirstOrDefault(bot => ReferenceEquals(bot.Player, victim));
        if (bot == null) return;

        // 피격 후 도주·문 열기 중단을 판단하는 봇 AI 입력만 별도로 남긴다.
        bot.LastProximityAttackerPlayerId = attackerId;
        bot.LastDamagedAtUtc = nowUtc;
        runtime.BotTactics.LastDamagedAtUtc[(runtime.MatchingId, victim.PlayerId)] = nowUtc;
    }

    /// <summary>몬스터 피해를 적용하고 같은 피해량을 클라이언트에 알린다.</summary>
    public void ApplySwarmAfterimageMonsterHit(PlayerEliminationService eliminations, Player victim, int monsterId, int damage)
    {
        if (runtime.IsEnded || victim.IsEliminated || monsterId <= 0 || damage <= 0) return;

        var nowUtc = DateTime.UtcNow;
        // 수면 상태는 유지하되 피격 직후의 수면 진입·회복을 제한한다.
        victim.MarkSwarmCombat(nowUtc);
        if (victim.InterruptDoor() is { } interactId)
        {
            eventLogs.LogExploreCancelled(runtime.MatchingId, victim.PlayerId, interactId,
                victim.CurrentArea.ToString(), "door_unlock_hit", isBot: victim.PlayerId < 0);
            victim.Session?.SendDoorOpenInterrupted(interactId);
        }
        var bot = runtime.Bots.GetBots(runtime.MatchingId).FirstOrDefault(bot => ReferenceEquals(bot.Player, victim));
        if (bot != null)
        {
            bot.LastDamagedAtUtc = nowUtc;
            runtime.BotTactics.LastDamagedAtUtc[(runtime.MatchingId, victim.PlayerId)] = nowUtc;
        }

        int healthBefore = victim.Health;
        int healthAfter = Math.Max(0, healthBefore - damage);
        bool isLethal = healthBefore > 0 && healthAfter <= 0;
        bool isBot = BotPlayerManager.IsBotPlayerId(victim.PlayerId);
        eventLogs.LogSwarmAfterimageHit(
            runtime.MatchingId, monsterId, victim.PlayerId, victim.CurrentArea.ToString(), damage,
            healthBefore, healthAfter, isLethal, isBot, new DateTimeOffset(nowUtc));
        logger.LogInformation(
            "Monster attack: MatchingId={MatchingId}, MonsterId={MonsterId}, Target={Target}, IsBot={IsBot}, Damage={Damage}, HealthBefore={HealthBefore}, HealthAfter={HealthAfter}, Killed={Killed}",
            runtime.MatchingId, monsterId, victim.PlayerId, isBot, damage, healthBefore, healthAfter, isLethal);

        var change = victim.ApplyDamage(damage);
        var session = victim.Session;
        PlayerHealthChangeService.Record(runtime.MatchingId, victim, change, eventLogs, logger);
        if (change.IsDepleted)
            eliminations.EliminatePlayer(runtime.MatchingId, victim.PlayerId, EliminationReason.HEALTH_ZERO);

        if (session == null) return;
        using var packet = PacketMaker.G_TO_C_COMBAT_HIT(new G_TO_C_COMBAT_HIT
        {
            AttackerId = monsterId,
            AttackerKind = CombatEntityKind.Monster,
            TargetId = victim.PlayerId,
            AreaType = victim.CurrentArea,
            Damage = damage,
            TargetHealth = victim.Health
        });
        session.TrySend(packet);
    }

    private static double SwarmCriticalChance => SwarmConfigData.GetDouble("SWARM_CRITICAL_CHANCE", 0.15d);
    private static float SwarmCriticalMultiplier => SwarmConfigData.GetFloat("SWARM_CRITICAL_MULTIPLIER", 2f);

    /// <summary>PvE 치명타 굴림 — 적중이면 배율을 적용한 피해를 돌려준다.</summary>
    public int RollSwarmCriticalDamage(int damage, out bool critical)
    {
        critical = RollCritical(SwarmCriticalChance);
        return critical
            ? Math.Max(damage + 1, (int)MathF.Round(damage * SwarmCriticalMultiplier))
            : damage;
    }

    private void SpawnSwarmSummonStone(
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
        var spawned = runtime.GroundItems.SpawnItems(
            defeatedWave.AreaType,
            defeatedWave.PositionX,
            defeatedWave.PositionY,
            itemIds,
            mapId: Config.SWARM_MATCH_MAP,
            layout: GroundItemSpawnLayout.EliminationScatter);

        foreach (var item in spawned)
        {
            eventLogs.LogGroundItemSpawned(
                runtime.MatchingId,
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
        foreach (var session in sessions.Where(session => session.Player.CurrentArea == defeatedWave.AreaType))
            session.TrySend(packet);
    }

    /// <summary>
    ///     즉시 몬스터 타격 (교차사격 쓸기). 착탄 지연 큐와 같은 정산 —
    ///     결과 집계, 처치 계측(monster_lifetime), 처치 로그, 소환석 드롭. 공격자 화면엔 숫자만
    ///     띄운다(투사체 없음).
    /// </summary>
    public void ApplySwarmMonsterHitNow(
        long combatTargetId,
        int monsterId,
        long attackerId,
        int weaponItemId,
        AreaType area,
        int damage,
        bool critical,
        List<GameClientSession> allSessions)
    {
        var damageResult = runtime.Monsters.ApplyMonsterDamage(runtime.MatchingId, combatTargetId, attackerId, damage);
        if (!damageResult.Applied)
            return;

        eventLogs.RecordMonsterHit(runtime.MatchingId, attackerId, damage, damageResult.Killed);
        var attacker = runtime.GetParticipant(attackerId);
        SendMonsterHitNotification(attacker,
            monsterId, area, weaponItemId, damage, critical, showDamageOnly: true);

        if (damageResult.Killed && damageResult.MonsterState != null)
            SettleSwarmMonsterKill(damageResult, attackerId, damage, allSessions);
    }

    /// <summary>처치 정산 — 착탄 큐와 즉시 타격이 같은 경로를 쓴다.</summary>
    public void SettleSwarmMonsterKill(
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
            runtime.MatchingId,
            $"monster_lifetime kind={damageResult.Kind} area={damageResult.MonsterState.AreaType} " +
            $"aliveSeconds={damageResult.AliveSeconds:F1} attackEvents={damageResult.AttackEventCount} " +
            $"killer={attackerId}");

        // 처치 계측 (#226 E): 종·구역·처치자 — 요약의 몹 처치 지표가 이 이벤트를 읽는다.
        eventLogs.LogSwarmAfterimageKilled(
            runtime.MatchingId, damageResult.MonsterId,
            damageResult.MonsterState.AreaType.ToString(),
            isCore: damageResult.Kind == SwarmMonsterKind.RunawayGoblin,
            firstAttackerPlayerId: attackerId,
            lastAttackerPlayerId: attackerId,
            new Dictionary<long, int> { [attackerId] = damage });
        SpawnSwarmSummonStone(
            damageResult.MonsterState, allSessions,
            damageResult.HeartReward, damageResult.BootsReward);
    }

    /// <summary>
    ///     플레이어 충격 (고정값, 티어 무관): 사격 피격 경로를 재사용해 체력 감소·피격 숫자·탈락 흐름이 그대로
    ///     따라온다. 봇도 같은 값. 소유자 화면에는 사격 피드백을 보낸다. label은 로그용(어느 모양이 때렸나).
    /// </summary>
    public void ApplySwarmShock(PlayerEliminationService eliminations,
        long ownerId,
        int weaponItemId,
        AreaType area,
        long victimId,
        string label,
        IReadOnlyList<Player> players,
        float damageScale = 1f,
        bool isPeriodicDamage = false)
    {
        if (runtime.IsEnded) return;
        // 받는 피해 배율: 고정 충격 50에 1/3을 곱한다. 태양·바람·파도 충격이 전부 이 한 곳을 지난다.
        // damageScale: 파도 소용돌이(#268)는 당김이 본체라 피해를 타격 피드백 수준(1/4)으로 줄인다.
        int shock = Math.Max(1, (int)MathF.Round(
            Config.ScaleSwarmDamageTaken(Config.SWARM_CROSSFIRE_SHOCK_DAMAGE) * damageScale));
        // 상처 (#268): 상처 입은 피해자만 PvP 충격 치명타가 열린다 — PvE와 같은 2배.
        if (runtime.WindOrbAttacks.IsWounded(victimId, DateTime.UtcNow) &&
            RollCritical(Config.SWARM_WIND_WOUND_CRIT_CHANCE))
            shock = Math.Max(shock + 1, (int)MathF.Round(shock * SwarmCriticalMultiplier));
        var victim = players.FirstOrDefault(player => player.PlayerId == victimId);
        if (victim == null || victim.IsEliminated) return;

        var owner = runtime.GetParticipant(ownerId);
        int ownerHealth = owner?.Health ?? -1;
        int healthBefore = victim.Health;
        ApplyProximityAutoCombatHit(eliminations, victim, ownerId, area, weaponItemId, shock, isPeriodicDamage, ownerHealth);
        int healthAfter = victim.Health;

        SendPlayerHitNotification(owner, victimId, area, weaponItemId, shock, healthAfter, isPeriodicDamage);

        eventLogs.LogSystem(
            runtime.MatchingId,
            $"{label} owner={ownerId} victim={victimId} weapon={weaponItemId} " +
            $"healthBefore={healthBefore} healthAfter={healthAfter}");
    }

    /// <summary>착탄 시각이 된 피해를 한 번 적용하고 몬스터 처치를 정산한다.</summary>
    public void ProcessPendingMonsterHits(
        DateTime nowUtc, List<GameClientSession> sessions)
    {
        long matchingId = runtime.MatchingId;
        for (int index = _pendingMonsterHits.Count - 1; index >= 0; index--)
        {
            var hit = _pendingMonsterHits[index];
            if (nowUtc < hit.ApplyAtUtc)
                continue;

            _pendingMonsterHits.RemoveAt(index);
            var damageResult = runtime.Monsters.ApplyMonsterDamage(
                matchingId, hit.CombatTargetId, hit.AttackerId, hit.Damage);

            // 결과 집계 (#229): 스웜 전투는 전부 여기를 지난다. 여기서 안 세면
            // 결과 화면이 수백 킬을 "처치 0회"로 표시한다.
            if (damageResult.Applied)
            {
                eventLogs.RecordMonsterHit(
                    matchingId, hit.AttackerId, hit.Damage, damageResult.Killed);
            }

            // 처치 정산은 교차사격 즉시 타격과 같은 경로 — 계측·처치 로그·소환석 드롭.
            if (damageResult.Applied && damageResult.Killed && damageResult.MonsterState != null)
                SettleSwarmMonsterKill(damageResult, hit.AttackerId, hit.Damage, sessions);
        }
    }

    public int ApplySwarmPvpAttack(PlayerEliminationService eliminations,
        ProximityCombatAttack attack,
        IReadOnlyList<Player> players,
        List<GameClientSession> allSessions,
        bool broadcastVfx = true,
        bool sendAttackerFeedback = true)
    {
        long matchingId = runtime.MatchingId;
        if (runtime.IsEnded) return 0;
        // 반격 보호 (#227 7단계): 방금 이 표적의 꼬리를 자른 공격자의 본체 피해는 통과하지 못한다.
        // 착탄 시점에 보므로 창이 열리기 '전에' 발사된 대기 투사체도 함께 걸린다.
        // 제3자·잔상·폐쇄는 이 경로를 타지 않아 종전대로 들어간다.
        var nowUtc = DateTime.UtcNow;
        if (runtime.TrailCombat.CutRetaliationWindows.TryGetValue(
                (matchingId, attack.AttackerPlayerId, attack.TargetPlayerId), out var guardWindow) &&
            nowUtc < guardWindow.ExpiresAtUtc)
        {
            guardWindow.BlockedHits++;
            guardWindow.BlockedDamage += attack.Damage;
            // 잔광 앞에서 짧게 깨지는 연출만 — 피해 숫자·피격 눌림·인카운터 배너는 만들지 않는다.
            SendSwarmRetaliationVfx(
                attack.AttackerPlayerId, attack.TargetPlayerId, attack.Area,
                SwarmRingVfxKindRetaliationBlocked, 0f, allSessions);
            return 0;
        }

        int healthDamage = ConsumeSwarmPvpDamage(attack.TargetPlayerId, attack.Damage);
        var attacker = runtime.GetParticipant(attack.AttackerPlayerId);
        int attackerHealth = attacker?.Health ?? -1;
        var target = players.FirstOrDefault(player => player.PlayerId == attack.TargetPlayerId);
        if (target == null || target.IsEliminated) return 0;

        if (healthDamage > 0)
            ApplyProximityAutoCombatHit(eliminations, target, attack.AttackerPlayerId, attack.Area,
                attack.WeaponItemId, healthDamage, sourceHealth: attackerHealth);
        else
            RecordCombatContact(target, attack.AttackerPlayerId);
        int targetHealth = target.Health;

        if (healthDamage > 0 && sendAttackerFeedback)
        {
            SendPlayerHitNotification(attacker,
                attack.TargetPlayerId, attack.Area, attack.WeaponItemId, healthDamage, targetHealth);
        }
        // 태양 착탄(#226)은 발사 시점에 이미 연출을 쐈다 — 이중 투사체 방지.
        if (broadcastVfx)
            BroadcastSwarmAttackVfxToTargetAndObservers(attack, allSessions);
        return healthDamage;
    }

    public void SendSwarmRetaliationVfx(
        long cutterId, long victimId, AreaType area, int kind, float seconds,
        List<GameClientSession> allSessions)
    {
        using var packet = Packet.Create((int)Protocol.G_TO_C_ORB_RING_EFFECT);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_ORB_RING_EFFECT
        {
            OwnerPlayerId = cutterId,
            CenterX = 0f,
            CenterY = 0f,
            Radius = seconds,
            Kind = kind,
            VictimPlayerId = victimId,
            FromOrdinal = 0
        }));
        foreach (var session in allSessions)
        {
            if (!session.PlayerId.HasValue || session.Player.CurrentArea != area)
                continue;
            if (session.PlayerId.Value == victimId || session.PlayerId.Value == cutterId)
                session.TrySend(packet);
        }
    }

    public static void BroadcastSwarmAttackVfxToTargetAndObservers(
        ProximityCombatAttack attack,
        IReadOnlyCollection<GameClientSession> sessions)
    {
        foreach (var observer in sessions)
        {
            // 탈락자도 받는다 (#219): 관전 중에도 봇 전투 연출이 계속 보여야 한다.
            if (!observer.PlayerId.HasValue ||
                observer.PlayerId.Value == attack.AttackerPlayerId ||
                observer.Player.CurrentArea != attack.Area)
            {
                continue;
            }

            using var packet = Packet.Create((int)Protocol.G_TO_C_PROXIMITY_ATTACK_VFX);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_PROXIMITY_ATTACK_VFX
            {
                AttackerPlayerId = attack.AttackerPlayerId,
                TargetPlayerId = attack.TargetPlayerId,
                AreaType = attack.Area,
                WeaponItemId = attack.WeaponItemId
            }));
            observer.TrySend(packet);
        }
    }
}
