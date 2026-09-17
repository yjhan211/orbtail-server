using game_server.players;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches;

/// <summary>
///     플레이어의 이동 경로와 상대 오브 꼬리의 교차를 판정하고, 오브 파괴와 반격 보호를 처리한다.
///     절단 통지와 반격 보호 알림은 틱 끝에 발행되도록 대기열에 넣는다.
/// </summary>
internal sealed class MatchTrailCutService(PlayerOrbTrailService orbTrails, MatchCombatDamageService combatDamage, MatchSynchronizationService synchronization)
{
    private readonly record struct TrailCutHit(Player Victim, int Ordinal, Vector3f OrbPosition);

    public void ProcessTick(MatchRuntime runtime, DateTime nowUtc, IReadOnlyList<Player> players)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Trail cuts require the match lock.");
        }

        var chainsByOwner = new Dictionary<long, List<Vector3f>>();
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
            var orbTiers = PlayerOrbTrailService.GetOrbTiersInOrder(runtime, owner);
            var chain = new List<Vector3f>(orbs.Count);
            for (int ordinal = 0; ordinal < orbs.Count; ordinal++)
            {
                chain.Add(PlayerOrbTrailService.GetOrbPosition(runtime, owner, ordinal, position, orbTiers));
            }
            chainsByOwner[owner.PlayerId] = chain;
        }

        foreach (var cutter in players)
        {
            var position = cutter.Position;
            if (position == null)
            {
                continue;
            }
            // 오브가 없으면 절단할 수 없음
            if (chainsByOwner.ContainsKey(cutter.PlayerId))
            {
                ProcessTrailCut(runtime, cutter, chainsByOwner, nowUtc);
            }

            // 판정이 끝난 뒤에 이번 위치를 다음 틱의 직전 위치로 남긴다.
            cutter.TrailLastTickPosition = new Vector3f(position.X, position.Y, 0f);
        }

    }

    internal void ProcessTrailCut(MatchRuntime runtime, Player cutter, Dictionary<long, List<Vector3f>> chainsByOwner, DateTime nowUtc)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Trail cuts require the match lock.");
        }
        if (runtime.IsEnded || cutter.IsEliminated)
        {
            return;
        }

        var previousPosition = cutter.TrailLastTickPosition;
        var currentPosition = cutter.Position;
        if (previousPosition == null || currentPosition == null)
        {
            return;
        }

        // 절단자 이동량
        float segmentDx = currentPosition.X - previousPosition.X;
        float segmentDy = currentPosition.Y - previousPosition.Y;
        float segmentLengthSquared = segmentDx * segmentDx + segmentDy * segmentDy;
        if (segmentLengthSquared < 0.0004f)
        {
            // 거리 0.02(제곱 0.0004)보다 적게 움직였으면 서 있는 것
            return;
        }
        if (segmentLengthSquared > 4)
        {
            // 한 틱에 갈 수 없는 거리
            return;
        }

        var cutterArea = cutter.CurrentArea;
        if (!TryFindCut(runtime, cutter, cutterArea, chainsByOwner, nowUtc, out var hit))
        {
            return;
        }

        long cutterId = cutter.PlayerId;
        var victim = hit.Victim;
        var destroyedOrbs = orbTrails.DestroyOrbsFromOrdinal(runtime, victim, hit.Ordinal, nowUtc);
        if (destroyedOrbs.Count == 0)
        {
            return;
        }

        foreach (var destroyedOrb in destroyedOrbs)
        {
            victim.Session?.SendOrbUpdate(destroyedOrb);
        }

        synchronization.QueueAreaPacket(runtime, cutterArea, Protocol.G_TO_C_ORB_TAIL_CUT, new G_TO_C_ORB_TAIL_CUT
        {
            CutterPlayerId = cutterId,
            VictimPlayerId = victim.PlayerId,
            FromOrdinal = hit.Ordinal,
            X = hit.OrbPosition.X,
            Y = hit.OrbPosition.Y
        });

        // 꼬리를 잘린 플레이어는 잠시동안 다시 잘리지 않음
        victim.StatusEffects.Apply(PlayerStatusEffectKind.TailCutGuard, nowUtc.AddSeconds(Config.SWARM_TAIL_CUT_GUARD_SECONDS));
        synchronization.QueueAreaPacket(runtime, cutterArea, Protocol.G_TO_C_STATUS_EFFECT, new G_TO_C_STATUS_EFFECT
        {
            SourcePlayerId = cutterId,
            TargetPlayerId = victim.PlayerId,
            AreaType = cutterArea,
            Effect = CombatStatusEffectKind.TailCutGuard,
            DurationMs = (int)(Config.SWARM_TAIL_CUT_GUARD_SECONDS * 1000d)
        });
        combatDamage.MarkAttacked(runtime, victim, cutterId, nowUtc);
    }

    private static bool TryFindCut(MatchRuntime runtime, Player cutter, AreaType cutterArea, Dictionary<long, List<Vector3f>> chainsByOwner, DateTime nowUtc, out TrailCutHit hit)
    {
        hit = default;
        bool found = false;

        var previousPosition = cutter.TrailLastTickPosition!;
        var currentPosition = cutter.Position!;

        float nearestCrossingT = float.MaxValue;
        foreach ((long ownerId, var chain) in chainsByOwner)
        {
            if (ownerId == cutter.PlayerId)
            {
                // 자신 꼬리 생략
                continue;
            }

            var owner = runtime.GetPlayer(ownerId);
            if (owner == null || owner.IsEliminated || owner.Position == null)
            {
                continue;
            }

            if (owner.CurrentArea != cutterArea)
            {
                continue;
            }

            if (owner.StatusEffects.IsActive(PlayerStatusEffectKind.TailCutGuard, nowUtc))
            {
                continue;
            }

            for (int ordinal = 0; ordinal < chain.Count; ordinal++)
            {
                var orbPosition = chain[ordinal];
                var orbHitPoint = new Vector3f(orbPosition.X, orbPosition.Y + Config.SWARM_TRAIL_CUT_ORB_HIT_Y_OFFSET, 0f);

                bool crossed = GroundGeometry.TrySegmentHitsPoint(previousPosition, currentPosition, orbHitPoint, out float crossingT);
                if (!crossed)
                {
                    var linkStart = ordinal == 0 ? owner.Position : chain[ordinal - 1];
                    crossed = GroundGeometry.TrySegmentIntersection(previousPosition, currentPosition, linkStart, orbPosition, out crossingT);
                }
                if (!crossed)
                {
                    continue;
                }
                if (crossingT >= nearestCrossingT)
                {
                    continue;
                }
                nearestCrossingT = crossingT;
                hit = new TrailCutHit(owner, ordinal, orbPosition);
                found = true;
            }
        }
        return found;
    }
}
