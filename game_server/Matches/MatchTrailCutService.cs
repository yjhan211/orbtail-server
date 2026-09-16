using game_server.players;
using game_server.players.bots;
using game_server.sessions;
using MessagePack;
using network.common;
using network.common.data;
using network.common.data.models;
using network.packets;

namespace game_server.matches;

/// <summary>
///     플레이어의 이동 경로와 상대 오브 꼬리의 교차를 판정하고, 절단 비용·오브 드롭·반격 보호를 처리한다.
///     절단 결과를 로그와 패킷으로 알린다.
/// </summary>
internal sealed class MatchTrailCutService(
    PlayerOrbTrailService orbTrails,
    MatchCombatDamageService combatDamage,
    PlayerHealthService healthService,
    BotBehaviorService botBehavior)
{
    private const float SwarmTrailCutMaxSegmentLength = 2f;
    private const float SwarmTrailCutMinSegmentLengthSquared = 0.0004f;
    private const float SwarmTrailCutOrbHitYOffset = 0.15f;
    private const float SwarmTrailCutFlashRadius = PlayerOrbTrailService.CutFlashRadius;
    private const int SwarmRingVfxKindCut = PlayerOrbTrailService.CutVfxKind;
    private const int SwarmRingVfxKindRetaliationGuard = 5;
    private static double SwarmTrailCutSameOrbDebounceSeconds => SwarmConfigData.GetDouble("SWARM_TRAIL_CUT_SAME_ORB_DEBOUNCE_SECONDS", 0.8d);
    private static int SwarmSingleCutHealthCost => SwarmConfigData.GetInt("SWARM_SINGLE_CUT_HEALTH_COST", 35);
    private static double SwarmSingleCutHealLockSeconds => SwarmConfigData.GetDouble("SWARM_SINGLE_CUT_HEAL_LOCK_SECONDS", 8d);
    private static double SwarmCutRetaliationWindowSeconds => SwarmConfigData.GetDouble("SWARM_CUT_RETALIATION_WINDOW_SECONDS", 1.2d);

    public void ProcessTick(MatchRuntime runtime, DateTime nowUtc, List<PlayerPositionSnapshot> participants, List<GameClientSession> sessions)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Trail cuts require the match lock.");
        }

        var orbPointsByOwner = new Dictionary<long, List<Vector3f>>();
        foreach (var owner in participants)
        {
            var orbs = runtime.GetOrbs(owner.PlayerId).GetOrderedOrbs();
            if (orbs.Count == 0)
            {
                continue;
            }
            var ownerPlayer = runtime.GetParticipant(owner.PlayerId)!;
            var orbTiers = new List<int>(orbs.Count);
            foreach (var orb in orbs)
            {
                orbTiers.Add(PlayerOrbCollection.GetOrbTier(orb.ItemId));
            }
            var orbPoints = new List<Vector3f>(orbs.Count);
            for (int ordinal = 0; ordinal < orbs.Count; ordinal++)
            {
                orbPoints.Add(orbTrails.GetOrbPosition(runtime, ownerPlayer, ordinal, owner.Position, orbTiers));
            }
            orbPointsByOwner[owner.PlayerId] = orbPoints;
        }

        foreach (var cutter in participants)
        {
            var cutterPlayer = runtime.GetParticipant(cutter.PlayerId);
            if (cutterPlayer == null)
            {
                continue;
            }
            var previousPosition = cutterPlayer.TrailLastTickPosition;
            cutterPlayer.TrailLastTickPosition = new Vector3f(cutter.Position.X, cutter.Position.Y, 0f);
            if (previousPosition == null || !orbPointsByOwner.ContainsKey(cutter.PlayerId))
            {
                continue;
            }
            TryPerformSwarmTrailCut(runtime, cutter.PlayerId, cutter.Area, previousPosition, cutter.Position, orbPointsByOwner, nowUtc, sessions);
        }

        var expiredPairs = new List<(long CutterId, long VictimId)>();
        foreach (var (pair, window) in runtime.CutRetaliationWindows)
        {
            if (nowUtc >= window.ExpiresAtUtc)
            {
                expiredPairs.Add(pair);
            }
        }

        foreach (var pair in expiredPairs)
        {
            runtime.CutRetaliationWindows.Remove(pair);
        }
    }

    internal void TryPerformSwarmTrailCut(MatchRuntime runtime, long cutterId, AreaType cutterArea,
        Vector3f previousPosition,
        Vector3f currentPosition,
        Dictionary<long, List<Vector3f>> orbPointsByOwner,
        DateTime nowUtc,
        List<GameClientSession> allSessions)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Trail cuts require the match lock.");
        }

        var cutter = runtime.GetParticipant(cutterId);
        if (runtime.IsEnded || cutter == null || cutter.IsEliminated)
        {
            return;
        }

        float segmentDx = currentPosition.X - previousPosition.X;
        float segmentDy = currentPosition.Y - previousPosition.Y;
        float segmentLengthSquared = segmentDx * segmentDx + segmentDy * segmentDy;
        if (segmentLengthSquared < SwarmTrailCutMinSegmentLengthSquared || segmentLengthSquared > SwarmTrailCutMaxSegmentLength * SwarmTrailCutMaxSegmentLength)
        {
            return;
        }

        long victimId = 0;
        int cutOrdinal = -1;
        long cutOrbUid = 0;
        float nearestCrossingT = float.MaxValue;
        Vector3f? cutOrbPosition = null;
        var cutArea = AreaType.None;
        foreach ((long ownerId, var orbPoints) in orbPointsByOwner)
        {
            if (ownerId == cutterId)
            {
                continue;
            }
            var owner = runtime.GetParticipant(ownerId);
            if (owner == null || owner.IsEliminated || owner.GameInfo.ObjectInfo.Area != cutterArea || owner.Position == null)
            {
                continue;
            }
            var ownerPosition = owner.Position;
            var ownerOrbs = runtime.GetOrbs(ownerId).GetOrderedOrbs();
            int orbCount = Math.Min(orbPoints.Count, ownerOrbs.Count);
            bool cutBlocked = runtime.CutRetaliationWindows.TryGetValue((cutterId, ownerId), out var guardOnOwner) && nowUtc < guardOnOwner.ExpiresAtUtc;
            for (int ordinal = 0; ordinal < orbCount; ordinal++)
            {
                var orbHitPoint = new Vector3f(orbPoints[ordinal].X, orbPoints[ordinal].Y + SwarmTrailCutOrbHitYOffset, 0f);
                bool latched = cutter.OrbCutLatches.TryGetValue(ownerOrbs[ordinal].ItemUid, out var lastLatchedAtUtc);
                bool withinDebounce = latched && (nowUtc - lastLatchedAtUtc).TotalSeconds < SwarmTrailCutSameOrbDebounceSeconds;
                if (withinDebounce)
                {
                    continue;
                }
                bool stillInsideOrb = latched && GroundGeometry.IsInsideOrbHitEllipse(previousPosition, orbHitPoint);
                if (stillInsideOrb)
                {
                    continue;
                }

                bool crossed = GroundGeometry.TrySegmentHitsPoint(previousPosition, currentPosition, orbHitPoint, out float crossingT);
                if (!crossed)
                {
                    var linkStart = ordinal == 0 ? ownerPosition : orbPoints[ordinal - 1];
                    crossed = GroundGeometry.TrySegmentIntersection(previousPosition, currentPosition, linkStart, orbPoints[ordinal], out crossingT);
                }

                if (!crossed)
                {
                    continue;
                }
                if (cutBlocked)
                {
                    guardOnOwner!.BlockedCuts++;
                    break;
                }

                if (crossingT >= nearestCrossingT)
                {
                    continue;
                }
                nearestCrossingT = crossingT;
                victimId = ownerId;
                cutOrdinal = ordinal;
                cutOrbUid = ownerOrbs[ordinal].ItemUid;
                cutOrbPosition = orbPoints[ordinal];
                cutArea = owner.GameInfo.ObjectInfo.Area;
            }
        }

        if (victimId == 0 || cutOrbPosition == null)
        {
            return;
        }

        var cutterBot = runtime.Bots.GetBot(cutterId);
        int cutterHealthBefore = cutter.Health;
        if (cutterHealthBefore - SwarmSingleCutHealthCost <= 0)
        {
            return;
        }

        if (cutterBot != null && !botBehavior.CanCutTrail(cutterBot, cutterHealthBefore, nowUtc, SwarmSingleCutHealthCost))
        {
            cutter.OrbCutLatches[cutOrbUid] = nowUtc;
            return;
        }

        cutter.OrbCutLatches[cutOrbUid] = nowUtc;
        cutter.MarkSwarmCombat(nowUtc);

        var victim = runtime.GetParticipant(victimId)!;
        var destroyedOrbs = orbTrails.DestroyOrbsFromOrdinal(runtime, victim, cutOrdinal);
        if (destroyedOrbs.Count == 0)
        {
            return;
        }

        var firstDestroyedOrb = destroyedOrbs[0];
        if (runtime.CutRetaliationWindows.TryGetValue((victimId, cutterId), out var guardOnCutter) && nowUtc < guardOnCutter.ExpiresAtUtc)
        {
            guardOnCutter.Retaliated = true;
        }

        var guardKey = (cutterId, victimId);
        if (!runtime.CutRetaliationWindows.TryGetValue(guardKey, out var guardOnVictim))
        {
            guardOnVictim = new CutRetaliationWindow { OpenedArea = cutArea };
            runtime.CutRetaliationWindows[guardKey] = guardOnVictim;
        }
        guardOnVictim.ExpiresAtUtc = nowUtc.AddSeconds(SwarmCutRetaliationWindowSeconds);
        combatDamage.SendSwarmRetaliationVfx(runtime, cutterId, victimId, cutArea, SwarmRingVfxKindRetaliationGuard, (float)SwarmCutRetaliationWindowSeconds, allSessions);

        foreach (var destroyedOrb in destroyedOrbs)
        {
            victim.Session?.SendOrbUpdate(destroyedOrb);
        }

        using (var ringPacket = Packet.Create((int)Protocol.G_TO_C_ORB_RING_EFFECT))
        {
            ringPacket.SetBody(MessagePackSerializer.Serialize(new G_TO_C_ORB_RING_EFFECT
            {
                OwnerPlayerId = cutterId,
                CenterX = cutOrbPosition.X,
                CenterY = cutOrbPosition.Y,
                Radius = SwarmTrailCutFlashRadius,
                Kind = SwarmRingVfxKindCut,
                VictimPlayerId = victimId,
                FromOrdinal = cutOrdinal
            }));
            foreach (var session in allSessions)
            {
                if (session.PlayerId.HasValue && session.Player.GameInfo.ObjectInfo.Area == cutArea)
                {
                    session.TrySend(ringPacket);
                }
            }
        }

        var healLockUntil = nowUtc.AddSeconds(SwarmSingleCutHealLockSeconds);
        combatDamage.ApplyProximityAutoCombatHit(runtime, healthService, cutter, cutterId, cutterArea, firstDestroyedOrb.ItemId, SwarmSingleCutHealthCost);
        cutter.StatusEffects.Apply(PlayerStatusEffectKind.HealingBlocked, healLockUntil);
        if (cutterBot != null)
        {
            cutterBot.LastTrailCutAtUtc = nowUtc;
        }

        combatDamage.RecordCombatContact(runtime, victim, cutterId, nowUtc);

    }
}
