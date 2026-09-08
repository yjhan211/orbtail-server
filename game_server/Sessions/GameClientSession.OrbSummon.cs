using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.sessions;

public partial class GameClientSession
{
    private Task HandleSummonOrb(C_TO_G_SUMMON_ORB request)
    {
        if (!PlayerId.HasValue)
        {
            return Task.CompletedTask;
        }
        var match = Volatile.Read(ref _match);
        if (match == null)
        {
            using var failurePacket = Packet.Create((int)Protocol.G_TO_C_SUMMON_ORB_RESULT, PlayerId ?? 0);
            failurePacket.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SUMMON_ORB_RESULT
            {
                Success = false,
                ErrorCode = ErrorCode.INVALID_GAME_STATE,
                SummonedItemId = 0,
                SummonedItemUid = 0,
                State = ToNetworkState(GetSummonStoneSnapshot())
            }));
            TrySend(failurePacket);
            return Task.CompletedTask;
        }

        using (match.Enter())
        {
            if (match.IsTerminal || IsEliminated || IsGameplayActionBlocked(out _))
            {
                using var failurePacket = Packet.Create((int)Protocol.G_TO_C_SUMMON_ORB_RESULT, PlayerId ?? 0);
                failurePacket.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SUMMON_ORB_RESULT
                {
                    Success = false,
                    ErrorCode = ErrorCode.INVALID_GAME_STATE,
                    SummonedItemId = 0,
                    SummonedItemUid = 0,
                    State = ToNetworkState(GetSummonStoneSnapshot())
                }));
                TrySend(failurePacket);
                return Task.CompletedTask;
            }

            long playerId = PlayerId!.Value;
            var attempt = _orbInventory.Summon(match, playerId, CurrentArea);

            if (attempt is { Success: true, AddedItem: not null })
            {
                SendInGameInventoryUpdate(attempt.AddedItem);
            }

            using var packet = Packet.Create((int)Protocol.G_TO_C_SUMMON_ORB_RESULT, PlayerId ?? 0);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SUMMON_ORB_RESULT
            {
                Success = attempt.Success,
                ErrorCode = attempt.ErrorCode,
                SummonedItemId = attempt.ItemId,
                SummonedItemUid = attempt.AddedItem?.ItemUid ?? 0,
                State = ToNetworkState(attempt.State)
            }));
            TrySend(packet);

            Logger.LogInformation(
                "Orb summon request: MatchingId={MatchingId}, PlayerId={PlayerId}, Success={Success}, Error={ErrorCode}, ItemId={ItemId}, Stones={StoneCount}, NextCost={NextCost}",
                MatchingId,
                playerId,
                attempt.Success,
                attempt.ErrorCode,
                attempt.ItemId,
                attempt.State.StoneCount,
                attempt.State.NextCost);

            return Task.CompletedTask;
        }
    }

    private Task HandleDevDummyMove(C_TO_G_DEV_DUMMY_MOVE request)
    {
        if (PlayerId.HasValue && MatchingId > 0)
            SwarmDummyMoveCallback?.Invoke(MatchingId, request.DirX, request.DirY);
        return Task.CompletedTask;
    }

    private Task HandleSwarmGrowthPick(C_TO_G_SWARM_GROWTH_PICK request)
    {
        if (!PlayerId.HasValue || MatchingId <= 0)
            return Task.CompletedTask;

        long matchingId = MatchingId;
        var match = Volatile.Read(ref _match);
        if (match == null)
        {
            SendSwarmGrowthResult(request.OfferId, request.CardIndex, success: false);
            return Task.CompletedTask;
        }

        using (match.Enter())
        {
            if (match.IsTerminal)
            {
                SendSwarmGrowthResult(request.OfferId, request.CardIndex, success: false);
                return Task.CompletedTask;
            }

            _growth.HandlePick(this, matchingId, request.OfferId, request.CardIndex);
        }

        return Task.CompletedTask;
    }

    private Task HandleSwarmOrbDecision(C_TO_G_SWARM_ORB_DECISION request)
    {
        if (!PlayerId.HasValue || MatchingId <= 0 || IsEliminated)
            return Task.CompletedTask;

        long matchingId = MatchingId;
        var match = Volatile.Read(ref _match);
        if (match == null)
        {
            SendSwarmOrbDecisionResult(request.Action, success: false, resultItemId: 0,
                targetItemUid: request.TargetItemUid, targetOrdinal: -1);
            return Task.CompletedTask;
        }

        using (match.Enter())
        {
            if (match.IsTerminal)
            {
                SendSwarmOrbDecisionResult(request.Action, success: false, resultItemId: 0,
                    targetItemUid: request.TargetItemUid, targetOrdinal: -1);
                return Task.CompletedTask;
            }

            _growth.HandleOrbDecision(
                this,
                matchingId,
                request.Action,
                request.TargetItemUid,
                request.SecondItemUid);
        }

        return Task.CompletedTask;
    }

    internal void SendSwarmFamilyLevels(
        int sunLevel, int windLevel, int waveLevel, int sunCost, int windCost, int waveCost)
    {
        if (!PlayerId.HasValue)
            return;

        using var packet = Packet.Create((int)Protocol.G_TO_C_SWARM_FAMILY_LEVELS, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SWARM_FAMILY_LEVELS
        {
            SunLevel = sunLevel,
            WindLevel = windLevel,
            WaveLevel = waveLevel,
            SunCost = sunCost,
            WindCost = windCost,
            WaveCost = waveCost
        }));
        TrySend(packet);
    }

    internal void SendSwarmOrbDecisionResult(
        int action, bool success, int resultItemId, long targetItemUid, int targetOrdinal = -1)
    {
        if (!PlayerId.HasValue)
            return;

        int stones = GetSummonStoneSnapshot().StoneCount;
        using var packet = Packet.Create((int)Protocol.G_TO_C_SWARM_ORB_DECISION_RESULT, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SWARM_ORB_DECISION_RESULT
        {
            Action = action,
            Success = success,
            ResultItemId = resultItemId,
            TargetItemUid = targetItemUid,
            StoneCount = stones,
            TargetOrdinal = targetOrdinal
        }));
        TrySend(packet);
    }

    internal void SendSwarmGrowthOffer(
        int offerId, int cost, int spawnItemId, int enhanceTargetTier, int armorCount,
        int costSummon = 0, int costAttack = 0, int costDefense = 0)
    {
        if (!PlayerId.HasValue)
            return;

        using var packet = Packet.Create((int)Protocol.G_TO_C_SWARM_GROWTH_OFFER, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SWARM_GROWTH_OFFER
        {
            OfferId = offerId,
            Cost = cost,
            Costs = [costSummon, costAttack, costDefense],
            SpawnItemId = spawnItemId,
            EnhanceTargetTier = enhanceTargetTier,
            ArmorCount = armorCount
        }));
        TrySend(packet);
    }

    internal void SendSwarmGrowthResult(int offerId, int cardIndex, bool success)
    {
        if (!PlayerId.HasValue)
            return;

        int stones = GetSummonStoneSnapshot().StoneCount;
        using var packet = Packet.Create((int)Protocol.G_TO_C_SWARM_GROWTH_RESULT, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SWARM_GROWTH_RESULT
        {
            OfferId = offerId,
            CardIndex = cardIndex,
            Success = success,
            StoneCount = stones
        }));
        TrySend(packet);
    }

    private Task HandleDestroyOrb(C_TO_G_DESTROY_ORB request)
    {
        if (!PlayerId.HasValue) return Task.CompletedTask;
        var match = Volatile.Read(ref _match);
        if (match == null)
        {
            using var failurePacket = Packet.Create((int)Protocol.G_TO_C_DESTROY_ORB_RESULT, PlayerId ?? 0);
            failurePacket.SetBody(MessagePackSerializer.Serialize(new G_TO_C_DESTROY_ORB_RESULT
            {
                Success = false,
                ErrorCode = ErrorCode.INVALID_GAME_STATE,
                ItemUid = request.ItemUid,
                RefundedStones = 0,
                State = ToNetworkState(GetSummonStoneSnapshot())
            }));
            TrySend(failurePacket);
            return Task.CompletedTask;
        }

        using (match.Enter())
        {
            if (match.IsTerminal)
            {
                using var failurePacket = Packet.Create((int)Protocol.G_TO_C_DESTROY_ORB_RESULT, PlayerId ?? 0);
                failurePacket.SetBody(MessagePackSerializer.Serialize(new G_TO_C_DESTROY_ORB_RESULT
                {
                    Success = false,
                    ErrorCode = ErrorCode.INVALID_GAME_STATE,
                    ItemUid = request.ItemUid,
                    RefundedStones = 0,
                    State = ToNetworkState(GetSummonStoneSnapshot())
                }));
                TrySend(failurePacket);
                return Task.CompletedTask;
            }

            if (!PlayerId.HasValue)
                return Task.CompletedTask;

            long playerId = PlayerId.Value;
            if (IsGameplayActionBlocked(out _))
            {
                using var failurePacket = Packet.Create((int)Protocol.G_TO_C_DESTROY_ORB_RESULT, PlayerId ?? 0);
                failurePacket.SetBody(MessagePackSerializer.Serialize(new G_TO_C_DESTROY_ORB_RESULT
                {
                    Success = false,
                    ErrorCode = ErrorCode.INVALID_GAME_STATE,
                    ItemUid = request.ItemUid,
                    RefundedStones = 0,
                    State = ToNetworkState(GetSummonStoneSnapshot())
                }));
                TrySend(failurePacket);
                return Task.CompletedTask;
            }

            var runtime = Match;
            var result = _orbInventory.Destroy(runtime, playerId, request.ItemUid, CurrentArea);
            if (result.Error != ErrorCode.SUCCESS)
            {
                using var failurePacket = Packet.Create((int)Protocol.G_TO_C_DESTROY_ORB_RESULT, PlayerId ?? 0);
                failurePacket.SetBody(MessagePackSerializer.Serialize(new G_TO_C_DESTROY_ORB_RESULT
                {
                    Success = false,
                    ErrorCode = result.Error,
                    ItemUid = request.ItemUid,
                    RefundedStones = 0,
                    State = ToNetworkState(result.State)
                }));
                TrySend(failurePacket);
                return Task.CompletedTask;
            }
            SendInGameInventoryUpdate(result.RemovedItem!);
            using var successPacket = Packet.Create((int)Protocol.G_TO_C_DESTROY_ORB_RESULT, PlayerId ?? 0);
            successPacket.SetBody(MessagePackSerializer.Serialize(new G_TO_C_DESTROY_ORB_RESULT
            {
                Success = true,
                ErrorCode = ErrorCode.SUCCESS,
                ItemUid = request.ItemUid,
                RefundedStones = result.RefundedStones,
                State = ToNetworkState(result.State)
            }));
            TrySend(successPacket);

            Logger.LogInformation(
                "Orb destroyed for summon stones: MatchingId={MatchingId}, PlayerId={PlayerId}, ItemId={ItemId}, ItemUid={ItemUid}, Tier={Tier}, RefundedStones={RefundedStones}",
                MatchingId,
                playerId,
                result.ItemId,
                request.ItemUid,
                result.Tier,
                result.RefundedStones);
            return Task.CompletedTask;
        }
    }

    internal void SendFreeSummonState()
    {
        if (!PlayerId.HasValue)
        {
            return;
        }

        using var packet = Packet.Create((int)Protocol.G_TO_C_FREE_SUMMON_STATE, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_FREE_SUMMON_STATE
        {
            Charges = FreeSummonCharges
        }));
        TrySend(packet);
    }

    internal void SendSummonStoneState(int awardedStones = 0, float awardSourceX = 0f, float awardSourceY = 0f)
    {
        if (!PlayerId.HasValue || MatchingId <= 0)
            return;

        var state = GetSummonStoneSnapshot();
        var stateInfo = ToNetworkState(state);
        using var packet = Packet.Create((int)Protocol.G_TO_C_SUMMON_STONE_STATE, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SUMMON_STONE_STATE
        {
            State = stateInfo,
            AwardedStones = Math.Max(0, awardedStones),
            AwardSourceX = awardSourceX,
            AwardSourceY = awardSourceY
        }));
        TrySend(packet);
    }

    private SummonStoneSnapshot GetSummonStoneSnapshot() =>
        PlayerId.HasValue
            ? Volatile.Read(ref _match)?.SummonStones.GetSnapshot(PlayerId.Value) ?? SummonStoneManager.EmptySnapshot
            : SummonStoneManager.EmptySnapshot;

    private SummonStoneStateInfo ToNetworkState(SummonStoneSnapshot state) => new()
    {
        StoneCount = state.StoneCount,
        SuccessfulSummonCount = state.SuccessfulSummonCount,
        NextCost = state.NextCost,
        PoolItemIds = Volatile.Read(ref _match)?.SummonStones.PoolItemIds.ToList() ?? [],
        // 다음 소환의 2택 후보. 결정론적이라 상태 패킷마다 실어도 대기 상태가 필요 없다.
        NextCandidateItemIds = PlayerId.HasValue && Volatile.Read(ref _match) is { IsTerminal: false } match
            ? match.SummonStones.GetSummonCandidates(PlayerId.Value).ToList()
            : []
    };

    internal void GrantSwarmArenaOrb(int itemId)
    {
        if (!PlayerId.HasValue)
            return;

        _orbInventory.Grant(Match, PlayerId.Value, itemId);
        SendInGameInventoryList();
    }
}
