using network.common.data;
using game_server.matches;
using game_server.players;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.sessions;

/// <summary>
///     오브 소환·강화 요청을 처리하고 오브 관련 상태를 이 연결로 보낸다.
///     매치 잠금 안에서 소환·강화 서비스를 호출하고 결과를 클라이언트에 보낸다.
///     보유 오브 목록·변경 내역, 소환석 보유량·다음 소환 비용, 계열별 강화 정보,
///     다른 플레이어의 오브 표시 상태(마지막 전송값과 다를 때만)도 전송한다.
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
                State = SummonStoneStateInfo.Empty
            }));
            TrySend(failurePacket);
            return Task.CompletedTask;
        }

        using (match.Enter())
        {
            if (match.IsEnded || IsGameplayActionBlocked(out _))
            {
                var state = Player.Orbs.SummonStones;
                using var failurePacket = Packet.Create((int)Protocol.G_TO_C_SUMMON_ORB_RESULT, PlayerId ?? 0);
                failurePacket.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SUMMON_ORB_RESULT
                {
                    Success = false,
                    ErrorCode = ErrorCode.INVALID_GAME_STATE,
                    SummonedItemId = 0,
                    SummonedItemUid = 0,
                    State = state.Copy()
                }));
                TrySend(failurePacket);
                return Task.CompletedTask;
            }

            long playerId = PlayerId!.Value;
            var attempt = _orbGrowth.Summon(match, Player);

            if (attempt is { Success: true, AddedItem: not null })
            {
                SendOrbUpdate(attempt.AddedItem);
                var upgradeInfo = _orbGrowth.GetOrbUpgradeInfo(match, Player);
                SendOrbUpgradeInfo(upgradeInfo);
            }

            using var packet = Packet.Create((int)Protocol.G_TO_C_SUMMON_ORB_RESULT, PlayerId ?? 0);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SUMMON_ORB_RESULT
            {
                Success = attempt.Success,
                ErrorCode = attempt.ErrorCode,
                SummonedItemId = attempt.ItemId,
                SummonedItemUid = attempt.AddedItem?.ItemUid ?? 0,
                State = attempt.State.Copy()
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
                TargetItemId = request.TargetItemId,
                StoneCount = SummonStoneStateInfo.Empty.StoneCount,
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
                    TargetItemId = request.TargetItemId,
                    StoneCount = Player.Orbs.SummonStones.StoneCount,
                    TargetOrdinal = -1
                }));
                TrySend(packet);
                return Task.CompletedTask;
            }

            var result = _orbGrowth.UpgradeOrb(
                match,
                Player,
                request.Action,
                request.TargetItemId);

            if (result.Success)
            {
                SendOrbList();
                SendOrbUpgradeInfo(_orbGrowth.GetOrbUpgradeInfo(match, Player));
            }

            using var resultPacket = Packet.Create((int)Protocol.G_TO_C_UPGRADE_ORB_RESULT, PlayerId.Value);
            resultPacket.SetBody(MessagePackSerializer.Serialize(new G_TO_C_UPGRADE_ORB_RESULT
            {
                Action = request.Action,
                Success = result.Success,
                ResultItemId = result.ResultItemId,
                TargetItemId = request.TargetItemId,
                StoneCount = Player.Orbs.SummonStones.StoneCount,
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
        var state = Player != null ? Player.Orbs.SummonStones : SummonStoneStateInfo.Empty;
        using var packet = Packet.Create((int)Protocol.G_TO_C_SUMMON_STONE_STATE, PlayerId.Value);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_SUMMON_STONE_STATE
        {
            State = state.Copy(),
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

        var items = Player.Orbs.GetAllOrbs();
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

    internal void SendOrbVisualStates(IReadOnlyList<MatchOrbVisual> visuals)
    {
        if (!PlayerId.HasValue)
        {
            return;
        }

        foreach (var visual in visuals)
        {
            if (Player.CurrentArea != visual.Area)
            {
                ForgetOrbVisualState(visual.ActorPlayerId);
                continue;
            }

            SendOrbVisualStateIfChanged(visual);
        }
    }

    internal void SendOrbVisualStateIfChanged(MatchOrbVisual visual)
    {
        if (_lastSentOrbVisualStates.TryGetValue(visual.ActorPlayerId, out var previousState) && previousState.HasSameState(visual))
        {
            return;
        }

        _lastSentOrbVisualStates[visual.ActorPlayerId] = visual;
        using var packet = Packet.Create((int)Protocol.G_TO_C_ORB_EFFECT_STATE);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_ORB_EFFECT_STATE
        {
            PlayerId = visual.ActorPlayerId,
            WeaponItemId = visual.WeaponItemId,
            OrbItemIds = visual.OrbItemIds.ToList(),
            BodyHealth = visual.BodyHealth
        }));
        TrySend(packet);
    }

    internal void ForgetOrbVisualState(long actorPlayerId) => _lastSentOrbVisualStates.Remove(actorPlayerId);
}
