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
    BotBehaviorService botBehavior)
{
    private const float SwarmTrailCutMaxSegmentLength = 2f;
    private const float SwarmTrailCutMinSegmentLengthSquared = 0.0004f;
    private const float SwarmTrailCutOrbHitYOffset = 0.15f;

    public void ProcessTick(MatchRuntime runtime, DateTime nowUtc, IReadOnlyList<Player> players, List<GameClientSession> sessions)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Trail cuts require the match lock.");
        }

        var orbPointsByOwner = new Dictionary<long, List<Vector3f>>();
        foreach (var owner in players)
        {
            var position = owner.Position;
            if (position == null)
            {
                continue;
            }
            var orbs = owner.Orbs.GetOrderedOrbs();
            if (orbs.Count == 0)
            {
                continue;
            }
            var orbTiers = new List<int>(orbs.Count);
            foreach (var orb in orbs)
            {
                orbTiers.Add(PlayerOrbState.GetOrbTier(orb.ItemId));
            }
            var orbPoints = new List<Vector3f>(orbs.Count);
            for (int ordinal = 0; ordinal < orbs.Count; ordinal++)
            {
                orbPoints.Add(orbTrails.GetOrbPosition(runtime, owner, ordinal, position, orbTiers));
            }
            orbPointsByOwner[owner.PlayerId] = orbPoints;
        }

        foreach (var cutter in players)
        {
            var position = cutter.Position;
            if (position == null)
            {
                continue;
            }
            var previousPosition = cutter.TrailLastTickPosition;
            cutter.TrailLastTickPosition = new Vector3f(position.X, position.Y, 0f);
            if (previousPosition == null || !orbPointsByOwner.ContainsKey(cutter.PlayerId))
            {
                continue;
            }
            ProcessTrailCut(runtime, cutter, previousPosition, position, orbPointsByOwner, nowUtc, sessions);
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

    internal void ProcessTrailCut(MatchRuntime runtime, Player cutter,
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

        if (runtime.IsEnded || cutter.IsEliminated)
        {
            return;
        }

        long cutterId = cutter.PlayerId;
        var cutterArea = GameMapData.GetCurrentArea(cutter.GameInfo.ObjectInfo.MapId, cutter.GameInfo.ObjectInfo.Cell);
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
            var owner = runtime.GetPlayer(ownerId);
            if (owner == null || owner.IsEliminated || GameMapData.GetCurrentArea(owner.GameInfo.ObjectInfo.MapId, owner.GameInfo.ObjectInfo.Cell) != cutterArea || owner.Position == null)
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
                bool withinDebounce = latched && (nowUtc - lastLatchedAtUtc).TotalSeconds < Config.SWARM_TRAIL_CUT_SAME_ORB_DEBOUNCE_SECONDS;
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
                cutArea = GameMapData.GetCurrentArea(owner.GameInfo.ObjectInfo.MapId, owner.GameInfo.ObjectInfo.Cell);
            }
        }

        if (victimId == 0 || cutOrbPosition == null)
        {
            return;
        }

        var cutterBot = runtime.Bots.GetBot(cutterId);
        int cutterHealthBefore = cutter.Health;
        if (cutterHealthBefore - Config.SWARM_SINGLE_CUT_HEALTH_COST <= 0)
        {
            return;
        }

        if (cutterBot != null && !botBehavior.CanCutTrail(cutterBot, cutterHealthBefore, nowUtc, Config.SWARM_SINGLE_CUT_HEALTH_COST))
        {
            cutter.OrbCutLatches[cutOrbUid] = nowUtc;
            return;
        }

        cutter.OrbCutLatches[cutOrbUid] = nowUtc;

        var victim = runtime.GetPlayer(victimId)!;
        var destroyedOrbs = orbTrails.DestroyOrbsFromOrdinal(runtime, victim, cutOrdinal);
        if (destroyedOrbs.Count == 0)
        {
            return;
        }

        var firstDestroyedOrb = destroyedOrbs[0];
        var guardKey = (cutterId, victimId);
        if (!runtime.CutRetaliationWindows.TryGetValue(guardKey, out var guardOnVictim))
        {
            guardOnVictim = new CutRetaliationWindow();
            runtime.CutRetaliationWindows[guardKey] = guardOnVictim;
        }
        guardOnVictim.ExpiresAtUtc = nowUtc.AddSeconds(Config.SWARM_CUT_RETALIATION_WINDOW_SECONDS);
        foreach (var guardViewer in new[] { victim, cutter })
        {
            if (guardViewer.Session is not { PlayerId: not null } guardSession)
            {
                continue;
            }
            combatDamage.QueueSessionEffect(runtime, guardSession, Protocol.G_TO_C_STATUS_EFFECT, new G_TO_C_STATUS_EFFECT
            {
                SourcePlayerId = cutterId,
                TargetPlayerId = victimId,
                AreaType = cutArea,
                Effect = CombatStatusEffectKind.CutRetaliationGuard,
                DurationMs = (int)(Config.SWARM_CUT_RETALIATION_WINDOW_SECONDS * 1000d)
            });
        }

        foreach (var destroyedOrb in destroyedOrbs)
        {
            victim.Session?.SendOrbUpdate(destroyedOrb);
        }

        using (var ringPacket = Packet.Create((int)Protocol.G_TO_C_ORB_TAIL_CUT))
        {
            ringPacket.SetBody(MessagePackSerializer.Serialize(new G_TO_C_ORB_TAIL_CUT
            {
                CutterPlayerId = cutterId,
                VictimPlayerId = victimId,
                FromOrdinal = cutOrdinal,
                X = cutOrbPosition.X,
                Y = cutOrbPosition.Y
            }));
            foreach (var session in allSessions)
            {
                if (session.PlayerId.HasValue && GameMapData.GetCurrentArea(session.Player.GameInfo.ObjectInfo.MapId, session.Player.GameInfo.ObjectInfo.Cell) == cutArea)
                {
                    session.TrySend(ringPacket);
                }
            }
        }

        var healLockUntil = nowUtc.AddSeconds(Config.SWARM_SINGLE_CUT_HEAL_LOCK_SECONDS);
        combatDamage.ApplyPlayerHit(runtime, cutter, cutterId, cutterArea, firstDestroyedOrb.ItemId, Config.SWARM_SINGLE_CUT_HEALTH_COST, nowUtc);
        cutter.StatusEffects.Apply(PlayerStatusEffectKind.HealingBlocked, healLockUntil);
        if (cutterBot != null)
        {
            cutterBot.LastTrailCutAtUtc = nowUtc;
        }

        combatDamage.RecordCombatContact(runtime, victim, cutterId, nowUtc);
    }
}
