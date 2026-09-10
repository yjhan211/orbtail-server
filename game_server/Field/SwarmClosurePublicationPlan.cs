using System.Collections.Immutable;
using game_server.sessions;
using network.common;
using network.common.data.models;

namespace game_server.field;

/// <summary>
///     폐쇄 틱에서 보낼 데이터와 수신 세션 목록. 데이터는 복사하고 수신자 순서를 고정한다.
///     계획 작성과 패킷 전송은 같은 매치 잠금 안에서 순서대로 처리한다.
/// </summary>
internal sealed record SwarmClosurePublicationPlan(
    long MatchingId,
    ImmutableArray<SwarmClosureOutbound> Outbound);

internal abstract record SwarmClosureOutbound(ImmutableArray<GameClientSession> Recipients);

internal sealed record SwarmFieldStateOutbound(
    long StartedAtUnixMs,
    ImmutableArray<GameClientSession> Recipients)
    : SwarmClosureOutbound(Recipients);

internal sealed record SwarmClosureWarningOutbound(
    AreaType Area,
    int SecondsRemaining,
    long ClosureAtUnixMs,
    ImmutableArray<GameClientSession> Recipients)
    : SwarmClosureOutbound(Recipients);

internal sealed record SwarmAreaClosedOutbound(
    AreaType Area,
    ImmutableArray<GameClientSession> Recipients)
    : SwarmClosureOutbound(Recipients);

internal sealed record SwarmDoorStateOutbound(
    int DoorId,
    ImmutableArray<GameClientSession> Recipients)
    : SwarmClosureOutbound(Recipients);

internal sealed record SwarmInventoryUpdateOutbound(
    SwarmInGameItemSnapshot Item,
    ImmutableArray<GameClientSession> Recipients)
    : SwarmClosureOutbound(Recipients);

internal sealed record SwarmRingVfxOutbound(
    long OwnerPlayerId,
    float CenterX,
    float CenterY,
    float Radius,
    int Kind,
    long VictimPlayerId,
    int FromOrdinal,
    ImmutableArray<GameClientSession> Recipients)
    : SwarmClosureOutbound(Recipients);

internal readonly record struct SwarmInGameItemSnapshot(
    long ItemUid,
    int ItemId,
    int Count,
    GiftState GiftState)
{
    public static SwarmInGameItemSnapshot Capture(InGameItemInfo item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new SwarmInGameItemSnapshot(item.ItemUid, item.ItemId, item.Count, item.GiftState);
    }

    public InGameItemInfo ToModel() => new()
    {
        ItemUid = ItemUid,
        ItemId = ItemId,
        Count = Count,
        GiftState = GiftState
    };
}
