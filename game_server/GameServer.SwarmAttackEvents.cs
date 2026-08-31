using game_server.network;
using game_server.services;
using MessagePack;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server;

public partial class GameServer
{
    // G_TO_C_SWARM_ENCIRCLE_VFX의 확장 Kind. 기존 0~6과 충돌하지 않는다.
    private const int SwarmVfxSunCharge = 7;
    private const int SwarmVfxSunLaunch = 8;
    private const int SwarmVfxWindCharge = 9;
    private const int SwarmVfxWindLaunch = 10;
    private const int SwarmVfxWaveCharge = 11;
    private const int SwarmVfxWaveExplode = 12;
    private const int SwarmVfxSunCancel = 13;
    private const int SwarmVfxWindCancel = 14;
    private const int SwarmVfxWaveCancel = 15;

    /// <summary>
    ///     #227 6단계. 오브별 독립 PvP 연사를 속성별 한 번의 공격 사건으로 묶는다.
    ///     PvE는 기존 ProximityAutoCombatResolver가 계속 소유한다.
    /// </summary>
    private void ProcessSwarmPvpAttackEvents(
        long matchingId,
        DateTime nowUtc,
        List<SpotArenaPlayerSpatial> participants,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots,
        List<GameClientSession> allSessions)
    {
        SwarmPvpAttackEventState attackEvents = GetSwarmMatchRuntime(matchingId).AttackEvents;
        DispatchPendingSwarmAttackVisuals(attackEvents, nowUtc, participants, allSessions);
        ApplyPendingSwarmAttackHits(
            attackEvents, matchingId, nowUtc, participants, aliveSessions, aliveBots, allSessions);
        LaunchReadySwarmAttackEvents(
            attackEvents, matchingId, nowUtc, participants, allSessions);

        foreach (var owner in participants)
        {
            if (!IsSwarmAttackArmed(matchingId, owner.PlayerId, nowUtc) ||
                IsSwarmCutDummyPlayer(matchingId, owner.PlayerId))
            {
                continue;
            }

            if (attackEvents.IsAttributeBlocked(owner.PlayerId, nowUtc))
            {
                continue;
            }

            foreach (OrbColor color in new[]
                     {
                         OrbColor.Red,
                         OrbColor.Green,
                         OrbColor.Blue
                     })
            {
                if (attackEvents.IsColorBlocked(owner.PlayerId, color, nowUtc))
                {
                    continue;
                }

                var target = ResolveSwarmAttackTarget(
                    attackEvents, matchingId, owner, color, participants);
                if (!target.HasValue)
                    continue;
                var resolvedTarget = target.Value;

                var snapshots = BuildSwarmAttackParticipantSnapshots(
                    matchingId, owner, resolvedTarget, color);
                if (snapshots.Count == 0)
                    continue;

                long attackEventId = attackEvents.AllocateAttackEventId();
                int damageBudget = SwarmPvpAttackEventRules.CapDamage(
                    color, snapshots.Sum(snapshot => snapshot.Damage));
                var attackEvent = new SwarmPvpAttackEvent
                {
                    AttackEventId = attackEventId,
                    MatchingId = matchingId,
                    AttackerId = owner.PlayerId,
                    TargetId = resolvedTarget.PlayerId,
                    Area = owner.Area,
                    Color = color,
                    StartedAtUtc = nowUtc,
                    LaunchAtUtc = nowUtc.AddSeconds(
                        SwarmPvpAttackEventRules.GetTelegraphSeconds(color)),
                    SnapshotDamageBudget = damageBudget,
                    Participants = snapshots
                };
                // 다음 속성 사건은 현재 사건이 실제 발사된 뒤에만 준비한다. 예고 시작 간격이
                // 아니라 발사 간격을 보장해야 서로 다른 색의 탄막이 한 프레임에 겹치지 않는다.
                attackEvents.RegisterEvent(
                    attackEvent,
                    nowUtc.AddSeconds(SwarmPvpAttackEventRules.GetIntervalSeconds(color)),
                    attackEvent.LaunchAtUtc.AddSeconds(
                        SwarmPvpAttackEventRules.CrossAttributeGapSeconds));

                SendSwarmAttackChargeVfx(attackEvent, allSessions);
                LogSwarmAttackEvent("started", attackEvent, snapshots, damageBudget);
                break;
            }
        }
    }

    private SpotArenaPlayerSpatial? ResolveSwarmAttackTarget(
        SwarmPvpAttackEventState attackEvents,
        long matchingId,
        SpotArenaPlayerSpatial owner,
        OrbColor color,
        List<SpotArenaPlayerSpatial> participants)
    {
        if (attackEvents.TryGetCurrentTarget(owner.PlayerId, out long currentTargetId))
        {
            if (TryGetSwarmAttackParticipant(
                    participants, currentTargetId, owner.Area, out var currentTarget) &&
                BuildSwarmAttackParticipantSnapshots(matchingId, owner, currentTarget, color).Count > 0)
            {
                return currentTarget;
            }
        }

        var orderedCandidates = participants
            .Where(candidate => candidate.PlayerId != owner.PlayerId && candidate.Area == owner.Area)
            .OrderBy(candidate => GetSwarmNormalizedDistanceSquared(owner.Position, candidate.Position));
        foreach (var candidate in orderedCandidates)
        {
            if (BuildSwarmAttackParticipantSnapshots(matchingId, owner, candidate, color).Count > 0)
                return candidate;
        }

        attackEvents.ClearCurrentTarget(owner.PlayerId);
        return null;
    }

    private static bool TryGetSwarmAttackParticipant(
        List<SpotArenaPlayerSpatial> participants,
        long playerId,
        AreaType area,
        out SpotArenaPlayerSpatial participant)
    {
        foreach (var candidate in participants)
        {
            if (candidate.PlayerId != playerId || candidate.Area != area)
                continue;

            participant = candidate;
            return true;
        }

        participant = default;
        return false;
    }

    private List<SwarmAttackParticipantSnapshot> BuildSwarmAttackParticipantSnapshots(
        long matchingId,
        SpotArenaPlayerSpatial owner,
        SpotArenaPlayerSpatial target,
        OrbColor color)
    {
        var orderedItems = _inGameInventoryManager.GetPlayerInventory(matchingId, owner.PlayerId)
            .GetAllItems()
            .Where(item => item.Count > 0 && GetSquadOrbTier(item.ItemId) > 0)
            .OrderBy(item => item.ItemUid)
            .ToList();
        var tiers = orderedItems.Select(item => GetSquadOrbTier(item.ItemId)).ToList();
        var result = new List<SwarmAttackParticipantSnapshot>();
        float range = color == OrbColor.Blue
            ? SwarmPvpAttackEventRules.WaveParticipationRange
            : SwarmPvpAttackEventRules.SunWindRange;

        for (int ordinal = 0; ordinal < orderedItems.Count; ordinal++)
        {
            var item = orderedItems[ordinal];
            if (!OrbData.TryGetColorAndTier(item.ItemId, out var itemColor, out int tier) ||
                itemColor != color)
            {
                continue;
            }

            var origin = GetSwarmOrbTrailPosition(
                matchingId, owner.PlayerId, ordinal, owner.Position, tiers);
            if (!IsWithinSwarmOrbRange(origin, range, target.Position) ||
                !HasSwarmAttackLineOfSight(owner.PlayerId, origin, target))
            {
                continue;
            }

            result.Add(new SwarmAttackParticipantSnapshot(
                item.ItemUid,
                item.ItemId,
                ordinal,
                ordinal,
                tier,
                origin,
                SwarmPvpAttackEventRules.GetPerOrbEventDamage(color, tier)));
        }

        return result;
    }

    private List<SwarmAttackParticipantSnapshot> RevalidateSwarmAttackParticipants(
        SwarmPvpAttackEvent attackEvent,
        SpotArenaPlayerSpatial owner,
        SpotArenaPlayerSpatial target)
    {
        var snapshotByUid = attackEvent.Participants.ToDictionary(snapshot => snapshot.ItemUid);
        var orderedItems = _inGameInventoryManager
            .GetPlayerInventory(attackEvent.MatchingId, attackEvent.AttackerId)
            .GetAllItems()
            .Where(item => item.Count > 0 && GetSquadOrbTier(item.ItemId) > 0)
            .OrderBy(item => item.ItemUid)
            .ToList();
        var tiers = orderedItems.Select(item => GetSquadOrbTier(item.ItemId)).ToList();
        var result = new List<SwarmAttackParticipantSnapshot>();
        float range = attackEvent.Color == OrbColor.Blue
            ? SwarmPvpAttackEventRules.WaveParticipationRange
            : SwarmPvpAttackEventRules.SunWindRange;

        for (int ordinal = 0; ordinal < orderedItems.Count; ordinal++)
        {
            var item = orderedItems[ordinal];
            if (!snapshotByUid.TryGetValue(item.ItemUid, out var snapshot))
                continue;
            if (!OrbData.TryGetColorAndTier(item.ItemId, out var color, out _) ||
                color != attackEvent.Color)
            {
                continue;
            }

            var origin = GetSwarmOrbTrailPosition(
                attackEvent.MatchingId, attackEvent.AttackerId, ordinal, owner.Position, tiers);
            if (!IsWithinSwarmOrbRange(origin, range, target.Position) ||
                !HasSwarmAttackLineOfSight(attackEvent.AttackerId, origin, target))
            {
                continue;
            }

            result.Add(snapshot with { CurrentOrdinal = ordinal, Origin = origin });
        }

        return result;
    }

    private void LaunchReadySwarmAttackEvents(
        SwarmPvpAttackEventState attackEvents,
        long matchingId,
        DateTime nowUtc,
        List<SpotArenaPlayerSpatial> participants,
        List<GameClientSession> allSessions)
    {
        foreach (SwarmPvpAttackEvent attackEvent in attackEvents.TakeReadyEvents(nowUtc))
        {
            if (!TryGetSwarmAttackParticipant(
                    participants, attackEvent.AttackerId, attackEvent.Area, out var owner) ||
                !TryGetSwarmAttackParticipant(
                    participants, attackEvent.TargetId, attackEvent.Area, out var target))
            {
                SendSwarmAttackCancelVfx(attackEvent, attackEvent.Participants, allSessions);
                LogSwarmAttackEvent("cancelled_area_or_life", attackEvent, [], 0);
                continue;
            }

            var survivors = RevalidateSwarmAttackParticipants(attackEvent, owner, target);
            var survivorUids = survivors.Select(snapshot => snapshot.ItemUid).ToHashSet();
            var removed = attackEvent.Participants
                .Where(snapshot => !survivorUids.Contains(snapshot.ItemUid))
                .ToList();
            if (removed.Count > 0)
                SendSwarmAttackCancelVfx(attackEvent, removed, allSessions);
            if (survivors.Count == 0)
            {
                LogSwarmAttackEvent("cancelled_no_participants", attackEvent, [], 0);
                continue;
            }

            int damage = SwarmPvpAttackEventRules.CapDamage(
                attackEvent.Color, survivors.Sum(snapshot => snapshot.Damage));
            int highestTier = survivors.Max(snapshot => snapshot.Tier);
            OrbData.TryGetItemId(attackEvent.Color, highestTier, out int representativeItemId);
            DateTime impactAtUtc = QueueSwarmAttackEventPresentation(
                attackEvents, attackEvent, survivors, target, nowUtc);
            IReadOnlyList<ProximityCombatAttack> attacks = attackEvent.Color == OrbColor.Blue
                ? BuildWaveAttackTargets(
                    attackEvent, survivors, participants, representativeItemId, damage)
                :
                [
                    new ProximityCombatAttack(
                        attackEvent.AttackerId,
                        attackEvent.TargetId,
                        attackEvent.Area,
                        representativeItemId,
                        damage,
                        0f,
                        0f)
                ];
            attackEvents.EnqueueHit(new PendingSwarmAttackHit(
                attackEvent.AttackEventId,
                attackEvent.TargetId,
                attacks,
                impactAtUtc));
            LogSwarmAttackEvent(
                "launched",
                attackEvent,
                survivors,
                damage,
                attacks.Select(attack => attack.TargetPlayerId));
        }
    }

    private static IReadOnlyList<ProximityCombatAttack> BuildWaveAttackTargets(
        SwarmPvpAttackEvent attackEvent,
        IReadOnlyCollection<SwarmAttackParticipantSnapshot> participants,
        IReadOnlyCollection<SpotArenaPlayerSpatial> players,
        int representativeItemId,
        int damage)
    {
        var foremost = participants.OrderBy(snapshot => snapshot.CurrentOrdinal).First();
        float radius = SwarmPvpAttackEventRules.GetWaveRadius(
            participants.Max(snapshot => snapshot.Tier));
        float radiusSquared = radius * radius;
        return players
            .Where(player => player.PlayerId != attackEvent.AttackerId &&
                             player.Area == attackEvent.Area &&
                             GetSwarmNormalizedDistanceSquared(foremost.Origin, player.Position) <=
                             radiusSquared)
            .Select(player => new ProximityCombatAttack(
                attackEvent.AttackerId,
                player.PlayerId,
                attackEvent.Area,
                representativeItemId,
                damage,
                0f,
                0f))
            .ToList();
    }

    private DateTime QueueSwarmAttackEventPresentation(
        SwarmPvpAttackEventState attackEvents,
        SwarmPvpAttackEvent attackEvent,
        List<SwarmAttackParticipantSnapshot> participants,
        SpotArenaPlayerSpatial target,
        DateTime nowUtc)
    {
        if (attackEvent.Color == OrbColor.Blue)
        {
            var foremost = participants.OrderBy(snapshot => snapshot.CurrentOrdinal).First();
            float radius = SwarmPvpAttackEventRules.GetWaveRadius(
                participants.Max(snapshot => snapshot.Tier));
            attackEvents.EnqueueVisual(new PendingSwarmAttackVisual(
                attackEvent.AttackEventId,
                attackEvent.AttackerId,
                attackEvent.TargetId,
                attackEvent.Area,
                attackEvent.Color,
                SwarmVfxWaveExplode,
                foremost.CurrentOrdinal,
                foremost.Tier,
                foremost.Origin,
                radius,
                nowUtc));
            return nowUtc.AddMilliseconds(20);
        }

        int kind = attackEvent.Color == OrbColor.Red
            ? SwarmVfxSunLaunch
            : SwarmVfxWindLaunch;
        double spacing = SwarmPvpAttackEventRules.GetLaunchSpacingSeconds(attackEvent.Color);
        var visualSequence = new List<SwarmAttackParticipantSnapshot>();
        if (attackEvent.Color == OrbColor.Green)
        {
            for (int burst = 0; burst < 3; burst++)
                visualSequence.AddRange(participants.OrderBy(snapshot => snapshot.CurrentOrdinal));
        }
        else
        {
            visualSequence.AddRange(participants.OrderBy(snapshot => snapshot.CurrentOrdinal));
        }

        int visualCount = Math.Min(
            SwarmPvpAttackEventRules.MaxProjectileVisuals, visualSequence.Count);
        DateTime latestImpactAtUtc = nowUtc;
        for (int index = 0; index < visualCount; index++)
        {
            var participant = visualSequence[index];
            DateTime visualAtUtc = nowUtc.AddSeconds(index * spacing);
            attackEvents.EnqueueVisual(new PendingSwarmAttackVisual(
                attackEvent.AttackEventId,
                attackEvent.AttackerId,
                attackEvent.TargetId,
                attackEvent.Area,
                attackEvent.Color,
                kind,
                participant.CurrentOrdinal,
                participant.Tier,
                participant.Origin,
                0f,
                visualAtUtc));

            float distance = GetSwarmNormalizedDistance(participant.Origin, target.Position);
            DateTime impactAtUtc = visualAtUtc.AddSeconds(
                OrbData.GetPvpProjectileImpactDelaySeconds(participant.ItemId, distance));
            if (impactAtUtc > latestImpactAtUtc)
                latestImpactAtUtc = impactAtUtc;
        }

        return latestImpactAtUtc;
    }

    private void DispatchPendingSwarmAttackVisuals(
        SwarmPvpAttackEventState attackEvents,
        DateTime nowUtc,
        List<SpotArenaPlayerSpatial> participants,
        List<GameClientSession> allSessions)
    {
        foreach (PendingSwarmAttackVisual visual in attackEvents.TakeDueVisuals(nowUtc))
        {
            if (!TryGetSwarmAttackParticipant(
                    participants, visual.TargetId, visual.Area, out _))
                continue;
            SendSwarmAttackEventVfx(visual, allSessions);
        }
    }

    private void ApplyPendingSwarmAttackHits(
        SwarmPvpAttackEventState attackEvents,
        long matchingId,
        DateTime nowUtc,
        List<SpotArenaPlayerSpatial> participants,
        List<GameClientSession> aliveSessions,
        List<BotPlayerState> aliveBots,
        List<GameClientSession> allSessions)
    {
        foreach (PendingSwarmAttackHit pending in attackEvents.TakeDueHits(nowUtc))
        {
            int totalDamage = 0;
            int appliedTargets = 0;
            int representativeItemId = 0;
            foreach (var attack in pending.Attacks)
            {
                if (!TryGetSwarmAttackParticipant(
                        participants,
                        attack.TargetPlayerId,
                        attack.Area,
                        out _))
                {
                    _gameEventLogManager.LogSystem(
                        matchingId,
                        $"swarm_attack_event id={pending.AttackEventId} phase=discarded_area_exit " +
                        $"attacker={attack.AttackerPlayerId} target={attack.TargetPlayerId}");
                    continue;
                }

                int appliedDamage = ApplySwarmPvpAttack(
                    matchingId,
                    attack,
                    aliveSessions,
                    aliveBots,
                    allSessions,
                    broadcastVfx: false,
                    aggregateEventFeedback: true,
                    sendAttackerFeedback: false);
                totalDamage += appliedDamage;
                representativeItemId = attack.WeaponItemId;
                if (appliedDamage > 0)
                    appliedTargets++;
            }

            if (pending.Attacks.Count == 0)
            {
                _gameEventLogManager.LogSystem(
                    matchingId,
                    $"swarm_attack_event id={pending.AttackEventId} phase=missed_wave_radius " +
                    $"primary_target={pending.FeedbackTargetId}");
                continue;
            }

            var representativeAttack = pending.Attacks[0];
            if (totalDamage > 0)
            {
                allSessions.FirstOrDefault(session =>
                        session.PlayerId == representativeAttack.AttackerPlayerId)
                    ?.SendSwarmAttackEventFeedback(
                        pending.FeedbackTargetId,
                        representativeAttack.Area,
                        representativeItemId,
                        totalDamage);
            }

            _gameEventLogManager.LogSystem(
                matchingId,
                $"swarm_attack_event id={pending.AttackEventId} phase=impact " +
                $"attacker={representativeAttack.AttackerPlayerId} " +
                $"primary_target={pending.FeedbackTargetId} targets={appliedTargets} damage={totalDamage}");
        }
    }

    private void SendSwarmAttackChargeVfx(
        SwarmPvpAttackEvent attackEvent,
        List<GameClientSession> allSessions)
    {
        int kind = attackEvent.Color switch
        {
            OrbColor.Red => SwarmVfxSunCharge,
            OrbColor.Green => SwarmVfxWindCharge,
            OrbColor.Blue => SwarmVfxWaveCharge,
            _ => 0
        };
        if (kind == 0)
            return;

        if (attackEvent.Color == OrbColor.Blue)
        {
            var foremost = attackEvent.Participants
                .OrderBy(snapshot => snapshot.SnapshotOrdinal)
                .First();
            SendSwarmAttackEventVfx(
                new PendingSwarmAttackVisual(
                    attackEvent.AttackEventId,
                    attackEvent.AttackerId,
                    attackEvent.TargetId,
                    attackEvent.Area,
                    attackEvent.Color,
                    kind,
                    foremost.SnapshotOrdinal,
                    foremost.Tier,
                    foremost.Origin,
                    SwarmPvpAttackEventRules.GetWaveRadius(
                        attackEvent.Participants.Max(snapshot => snapshot.Tier)),
                    attackEvent.StartedAtUtc),
                allSessions);
            return;
        }

        foreach (var participant in attackEvent.Participants)
            SendSwarmAttackEventVfx(
                new PendingSwarmAttackVisual(
                    attackEvent.AttackEventId,
                    attackEvent.AttackerId,
                    attackEvent.TargetId,
                    attackEvent.Area,
                    attackEvent.Color,
                    kind,
                    participant.SnapshotOrdinal,
                    participant.Tier,
                    participant.Origin,
                    0f,
                    attackEvent.StartedAtUtc),
                allSessions);
    }

    private void SendSwarmAttackCancelVfx(
        SwarmPvpAttackEvent attackEvent,
        IReadOnlyCollection<SwarmAttackParticipantSnapshot> participants,
        List<GameClientSession> allSessions)
    {
        int kind = attackEvent.Color switch
        {
            OrbColor.Red => SwarmVfxSunCancel,
            OrbColor.Green => SwarmVfxWindCancel,
            OrbColor.Blue => SwarmVfxWaveCancel,
            _ => 0
        };
        if (kind == 0)
            return;

        if (attackEvent.Color == OrbColor.Blue)
        {
            var foremost = attackEvent.Participants
                .OrderBy(snapshot => snapshot.SnapshotOrdinal)
                .First();
            SendSwarmAttackEventVfx(
                new PendingSwarmAttackVisual(
                    attackEvent.AttackEventId,
                    attackEvent.AttackerId,
                    attackEvent.TargetId,
                    attackEvent.Area,
                    attackEvent.Color,
                    kind,
                    foremost.SnapshotOrdinal,
                    foremost.Tier,
                    foremost.Origin,
                    0f,
                    DateTime.UtcNow),
                allSessions);
            return;
        }

        foreach (var participant in participants)
            SendSwarmAttackEventVfx(
                new PendingSwarmAttackVisual(
                    attackEvent.AttackEventId,
                    attackEvent.AttackerId,
                    attackEvent.TargetId,
                    attackEvent.Area,
                    attackEvent.Color,
                    kind,
                    participant.CurrentOrdinal,
                    participant.Tier,
                    participant.Origin,
                    0f,
                    DateTime.UtcNow),
                allSessions);
    }

    private static void SendSwarmAttackEventVfx(
        PendingSwarmAttackVisual visual,
        List<GameClientSession> allSessions)
    {
        using var packet = Packet.Create((int)Protocol.G_TO_C_SWARM_ENCIRCLE_VFX);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SWARM_ENCIRCLE_VFX
        {
            OwnerPlayerId = visual.AttackerId,
            CenterX = visual.Origin.X,
            CenterY = visual.Origin.Y,
            Radius = visual.Radius > 0f ? visual.Radius : visual.Tier,
            Kind = visual.Kind,
            VictimPlayerId = visual.TargetId,
            FromOrdinal = visual.Ordinal
        }));
        foreach (var session in allSessions)
        {
            if (session.PlayerId.HasValue && session.CurrentArea == visual.Area)
                session.Send(packet);
        }
    }

    private static bool HasSwarmAttackLineOfSight(
        long attackerId,
        Vector3f origin,
        SpotArenaPlayerSpatial target)
    {
        var sourceActor = new ProximityCombatActor(
            attackerId,
            target.Area,
            origin,
            0,
            0f,
            0,
            0f,
            MapId: Config.SWARM_MATCH_MAP,
            Cell: ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, origin));
        var targetActor = new ProximityCombatActor(
            target.PlayerId,
            target.Area,
            target.Position,
            0,
            0f,
            0,
            0f,
            MapId: Config.SWARM_MATCH_MAP,
            Cell: ProximityCombatLineOfSight.WorldPositionToCell(Config.SWARM_MATCH_MAP, target.Position));
        return ProximityCombatLineOfSight.CanTarget(sourceActor, targetActor);
    }

    private static float GetSwarmNormalizedDistanceSquared(Vector3f left, Vector3f right)
    {
        float dx = right.X - left.X;
        float dy = (right.Y - left.Y) * 2f;
        return dx * dx + dy * dy;
    }

    private static float GetSwarmNormalizedDistance(Vector3f left, Vector3f right)
    {
        return MathF.Sqrt(GetSwarmNormalizedDistanceSquared(left, right));
    }

    private void LogSwarmAttackEvent(
        string phase,
        SwarmPvpAttackEvent attackEvent,
        IReadOnlyCollection<SwarmAttackParticipantSnapshot> participants,
        int damageBudget,
        IEnumerable<long>? victimIds = null)
    {
        string participantSummary = string.Join(',', participants.Select(participant =>
            $"{participant.ItemUid}:{participant.SnapshotOrdinal}/{participant.CurrentOrdinal}:" +
            $"T{participant.Tier}@{participant.Origin.X:0.00},{participant.Origin.Y:0.00}"));
        string victims = victimIds == null ? string.Empty :
            $" victims=[{string.Join(',', victimIds)}]";
        _gameEventLogManager.LogSystem(
            attackEvent.MatchingId,
            $"swarm_attack_event id={attackEvent.AttackEventId} phase={phase} " +
            $"attacker={attackEvent.AttackerId} target={attackEvent.TargetId} " +
            $"color={attackEvent.Color} damage={damageBudget} participants=[{participantSummary}]" +
            victims);
    }

}
