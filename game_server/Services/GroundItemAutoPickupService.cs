using game_server.sessions;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.services;

/// <summary>
/// 승인된 이동 경로와 현재 위치에서 자동 획득을 처리하고 상태 변경과 결과를 전송한다.
/// 후보는 매치가 세션별로 소유하며, 모든 호출은 해당 매치 잠금 안에서 실행한다.
/// </summary>
internal sealed class GroundItemAutoPickupService(
    GameEventLogManager eventLogs,
    ILogger<GroundItemAutoPickupService> logger)
{
    public static void RecordMovement(GameClientSession session, Vector3f from, Vector3f to, AreaType nextArea)
    {
        var match = session.Match;
        RequireMatchLock(match);
        if (match.IsTerminal || !session.PlayerId.HasValue) return;
        var candidates = GetCandidates(match, session);
        if (nextArea != session.CurrentArea)
            candidates.Record(match.GroundItems, session.PlayerId.Value, session.CurrentArea,
                from, to, session.CurrentMapId);
        candidates.Record(match.GroundItems, session.PlayerId.Value, nextArea,
            from, to, nextArea != session.CurrentArea ? session.CurrentMapId : null);
    }

    public void Process(MatchRuntime match, IReadOnlyCollection<GameClientSession> sessions)
    {
        RequireMatchLock(match);
        // 접속 종료·탈락·세션 교체로 더 이상 처리하지 않는 후보는 버린다.
        foreach (var stale in match.GroundItemPickupCandidates.Keys.Except(sessions).ToArray())
            match.GroundItemPickupCandidates.Remove(stale);
        foreach (var session in sessions)
        {
            if (match.IsTerminal) return;
            try
            {
                Process(session);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Automatic pickup failed: MatchingId={MatchingId}, PlayerId={PlayerId}",
                    match.MatchingId, session.PlayerId);
            }
        }
    }

    public void Process(GameClientSession session)
    {
        var match = session.Match;
        RequireMatchLock(match);
        if (match.IsTerminal || !session.PlayerId.HasValue || session.MatchingId <= 0 ||
            session.IsEliminated || session.IsGameEnded || session.IsConnectionReleased || session.LastValidatedPosition == null)
        {
            match.GroundItemPickupCandidates.Remove(session);
            return;
        }
        var candidates = GetCandidates(match, session);
        candidates.Record(match.GroundItems, session.PlayerId.Value, session.CurrentArea,
            session.LastValidatedPosition, session.LastValidatedPosition);
        match.GroundItemPickupCandidates.Remove(session);
        foreach (var candidate in candidates.Take())
        {
            if (match.IsTerminal) break;
            var pickup = GroundItemPickupService.TryPickup(match, session.PlayerId.Value, candidate.Area,
                candidate.Position, session.CurrentHealth, candidate.GroundItemUid);
            if (pickup.Status != GroundItemClaimStatus.Success || pickup.ClaimedItem == null) continue;
            PublishGroundItemPickup(session, pickup);
        }
    }

    private static GroundItemPickupCandidates GetCandidates(MatchRuntime match, GameClientSession session)
    {
        if (!match.GroundItemPickupCandidates.TryGetValue(session, out var candidates))
            match.GroundItemPickupCandidates.Add(session, candidates = new GroundItemPickupCandidates());
        return candidates;
    }

    private static void RequireMatchLock(MatchRuntime match)
    {
        if (!Monitor.IsEntered(match.Sync))
            throw new InvalidOperationException("Automatic pickup requires the match lock.");
    }

    private void PublishGroundItemPickup(GameClientSession session, GroundItemPickupResult pickup)
    {
        var claimedItem = pickup.ClaimedItem!;
        var addedItem = pickup.AddedItem;

        if (pickup.BootsPickup)
        {
            // 부츠 (#222 M4): 이속은 클라 이동이 소유한다 — 서버는 픽업 결과만 확정.
            // 클라가 픽업 결과(ItemId)로 10초 버프·HUD 타이머를 시작한다.
        }
        else if (pickup.SummonStonePickup)
        {
            var summonState = session.Match.SummonStones.AddStones(session.PlayerId.Value, 1);
            session.SendSummonStoneState(1, claimedItem.PositionX, claimedItem.PositionY);
            eventLogs.LogSummonStoneAward(
                session.MatchingId,
                session.PlayerId.Value,
                monsterId: 0,
                amount: 1,
                summonState.StoneCount,
                session.CurrentArea.ToString(),
                isCore: false,
                isBot: false);
        }
        else if (pickup.AutoUsed)
        {
            int effectiveHealthRecovery = Math.Min(pickup.HealthRecovery, Math.Max(0, Config.MAX_HEALTH - session.CurrentHealth));
            int requestedRecovery = pickup.HealthRecovery;
            int effectiveRecovery = effectiveHealthRecovery;
            session.HealthChanges.Handle(session.Condition.Recover(pickup.HealthRecovery));
            // 하트는 앞줄 오브 HP도 만충으로 (#222 M4) — 원작 하트의 스쿼드 회복.
            if (claimedItem.ItemId == Config.HEART_GROUND_ITEM_ID)
                GameClientSession.SwarmHeartPickupCallback?.Invoke(session.MatchingId, session.PlayerId.Value);
            eventLogs.LogRecoveryUse(
                session.MatchingId, session.PlayerId.Value, claimedItem.ItemId,
                effectiveRecovery, source: "ground_auto_use", isBot: false);
            eventLogs.LogPelletPickupOutcome(
                session.MatchingId, session.PlayerId.Value, claimedItem.ItemId, requestedRecovery, effectiveRecovery,
                effectiveRecovery == 0 ? "wasted" : effectiveRecovery == requestedRecovery ? "effective" : "partial_waste",
                isBot: false);
        }
        else if (addedItem != null)
        {
            session.SendOrbUpdate(addedItem);

        }

        using (var removed = PacketMaker.G_TO_C_GROUND_ITEM_REMOVED(
                   claimedItem.GroundItemUid, session.PlayerId.Value, pickup.AutoUsed))
        {
            foreach (var other in session.Match.Sessions.GetInArea((AreaType)claimedItem.AreaType))
                other.TrySend(removed);
        }
        eventLogs.LogGroundItemPickup(
            session.MatchingId,
            session.PlayerId.Value,
            pickup.DiscovererPlayerId,
            claimedItem.GroundItemUid,
            claimedItem.ItemId,
            session.CurrentArea.ToString(),
            pickup.AutoUsed,
            isBot: false);
        if (!pickup.SummonStonePickup && !pickup.BootsPickup)
        {
            var boardAfterPickup = session.Match.Inventory.GetPlayerInventory(session.PlayerId.Value);
            eventLogs.LogOrbBoardTransition(
                session.MatchingId, session.PlayerId.Value, boardAfterPickup.GetAllItems(),
                boardAfterPickup.GetOrderedOrbs().FirstOrDefault()?.ItemId ?? 0, session.CurrentArea.ToString(), "pickup", isBot: false);
        }
        using var result = PacketMaker.G_TO_C_GROUND_ITEM_PICKUP_RESULT(
            claimedItem.GroundItemUid, claimedItem.ItemId, true, pickup.AutoUsed, ErrorCode.SUCCESS);
        session.TrySend(result);
    }
}
