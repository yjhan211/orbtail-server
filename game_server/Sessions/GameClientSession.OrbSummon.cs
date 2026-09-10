using game_server.items;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.sessions;

/// <summary>
///     오브 소환과 강화 요청을 처리한다.
///     매치 잠금 안에서 소환·강화 서비스를 호출하고 결과를 클라이언트에 보낸다.
///     보유 오브 목록·변경 내역, 소환석 보유량·다음 소환 비용과 계열별 강화 정보도 전송한다.
/// </summary>
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
                State = new SummonStoneStateInfo
                {
                    StoneCount = SummonStoneManager.EmptySnapshot.StoneCount,
                    SuccessfulSummonCount = SummonStoneManager.EmptySnapshot.SuccessfulSummonCount,
                    NextCost = SummonStoneManager.EmptySnapshot.NextCost
                }
            }));
            TrySend(failurePacket);
            return Task.CompletedTask;
        }

        using (match.Enter())
        {
            if (match.IsEnded || IsGameplayActionBlocked(out _))
            {
                var state = match.SummonStones.GetSnapshot(PlayerId ?? 0);
                using var failurePacket = Packet.Create((int)Protocol.G_TO_C_SUMMON_ORB_RESULT, PlayerId ?? 0);
                failurePacket.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SUMMON_ORB_RESULT
                {
                    Success = false,
                    ErrorCode = ErrorCode.INVALID_GAME_STATE,
                    SummonedItemId = 0,
                    SummonedItemUid = 0,
                    State = new SummonStoneStateInfo
                    {
                        StoneCount = state.StoneCount,
                        SuccessfulSummonCount = state.SuccessfulSummonCount,
                        NextCost = state.NextCost
                    }
                }));
                TrySend(failurePacket);
                return Task.CompletedTask;
            }

            long playerId = PlayerId!.Value;
            var attempt = _orbInventory.Summon(match, playerId, Player.CurrentArea);

            if (attempt is { Success: true, AddedItem: not null })
            {
                SendOrbUpdate(attempt.AddedItem);
                var upgradeInfo = _growth.GetOrbUpgradeInfo(MatchingId, playerId);
                SendOrbUpgradeInfo(upgradeInfo);
            }

            using var packet = Packet.Create((int)Protocol.G_TO_C_SUMMON_ORB_RESULT, PlayerId ?? 0);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SUMMON_ORB_RESULT
            {
                Success = attempt.Success,
                ErrorCode = attempt.ErrorCode,
                SummonedItemId = attempt.ItemId,
                SummonedItemUid = attempt.AddedItem?.ItemUid ?? 0,
                State = new SummonStoneStateInfo
                {
                    StoneCount = attempt.State.StoneCount,
                    SuccessfulSummonCount = attempt.State.SuccessfulSummonCount,
                    NextCost = attempt.State.NextCost
                }
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

    private Task HandleUpgradeOrb(C_TO_G_UPGRADE_ORB request)
    {
        if (!PlayerId.HasValue || MatchingId <= 0 || Player.IsEliminated)
        {
            return Task.CompletedTask;
        }

        long matchingId = MatchingId;
        var match = Volatile.Read(ref _match);
        if (match == null)
        {
            using var packet = Packet.Create((int)Protocol.G_TO_C_UPGRADE_ORB_RESULT, PlayerId.Value);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_UPGRADE_ORB_RESULT
            {
                Action = request.Action,
                Success = false,
                ResultItemId = 0,
                TargetItemUid = request.TargetItemUid,
                StoneCount = SummonStoneManager.EmptySnapshot.StoneCount,
                TargetOrdinal = -1
            }));
            TrySend(packet);
            return Task.CompletedTask;
        }

        using (match.Enter())
        {
            if (match.IsEnded)
            {
                using var packet = Packet.Create((int)Protocol.G_TO_C_UPGRADE_ORB_RESULT, PlayerId.Value);
                packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_UPGRADE_ORB_RESULT
                {
                    Action = request.Action,
                    Success = false,
                    ResultItemId = 0,
                    TargetItemUid = request.TargetItemUid,
                    StoneCount = match.SummonStones.GetSnapshot(PlayerId.Value).StoneCount,
                    TargetOrdinal = -1
                }));
                TrySend(packet);
                return Task.CompletedTask;
            }

            var result = _growth.HandleUpgradeOrb(
                Player,
                matchingId,
                request.Action,
                request.TargetItemUid,
                request.SecondItemUid);

            if (result.Success)
            {
                SendOrbList();
                SendOrbUpgradeInfo(_growth.GetOrbUpgradeInfo(matchingId, PlayerId.Value));
            }

            using var resultPacket = Packet.Create((int)Protocol.G_TO_C_UPGRADE_ORB_RESULT, PlayerId.Value);
            resultPacket.SetBody(MessagePackSerializer.Serialize(new G_TO_C_UPGRADE_ORB_RESULT
            {
                Action = request.Action,
                Success = result.Success,
                ResultItemId = result.ResultItemId,
                TargetItemUid = request.TargetItemUid,
                StoneCount = match.SummonStones.GetSnapshot(PlayerId.Value).StoneCount,
                TargetOrdinal = result.TargetOrdinal
            }));
            TrySend(resultPacket);
        }

        return Task.CompletedTask;
    }

    internal void SendOrbUpgradeInfo(G_TO_C_ORB_UPGRADE_INFO levels)
    {
        if (!PlayerId.HasValue)
        {
            return;
        }

        using var packet = Packet.Create((int)Protocol.G_TO_C_ORB_UPGRADE_INFO, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(levels));
        TrySend(packet);
    }

    internal void SendSummonStoneState(int awardedStones = 0, float awardSourceX = 0f, float awardSourceY = 0f)
    {
        if (!PlayerId.HasValue || MatchingId <= 0)
        {
            return;
        }

        var match = Volatile.Read(ref _match);
        var state = match?.SummonStones.GetSnapshot(PlayerId.Value) ?? SummonStoneManager.EmptySnapshot;
        using var packet = Packet.Create((int)Protocol.G_TO_C_SUMMON_STONE_STATE, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SUMMON_STONE_STATE
        {
            State = new SummonStoneStateInfo
            {
                StoneCount = state.StoneCount,
                SuccessfulSummonCount = state.SuccessfulSummonCount,
                NextCost = state.NextCost
            },
            AwardedStones = Math.Max(0, awardedStones),
            AwardSourceX = awardSourceX,
            AwardSourceY = awardSourceY
        }));
        TrySend(packet);
    }

    internal void SendOrbList()
    {
        if (!PlayerId.HasValue)
        {
            return;
        }

        var items = Match.Inventory.GetPlayerInventory(PlayerId.Value).GetAllItems();
        using var packet = PacketMaker.G_TO_C_ORB_LIST(items);
        TrySend(packet);

        Logger.LogDebug("Sent orb list to PlayerId={PlayerId}, ItemCount={Count}", PlayerId, items.Count);
    }

    internal void SendOrbUpdate(InGameItemInfo item)
    {
        if (!PlayerId.HasValue)
        {
            return;
        }

        using var packet = PacketMaker.G_TO_C_ORB_UPDATE([item]);
        TrySend(packet);

        Logger.LogDebug("Sent orb update to PlayerId={PlayerId}, ItemUid={ItemUid}, ItemId={ItemId}, Count={Count}", PlayerId, item.ItemUid, item.ItemId, item.Count);
    }
}
