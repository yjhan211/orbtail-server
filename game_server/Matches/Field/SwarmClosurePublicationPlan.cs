using System.Collections.Immutable;
using network.common;
using network.common.data.models;

namespace game_server.matches.field;

/// <summary>
///     예정 폐쇄 틱의 불변 송신 계획. 권위 상태 변경과 계획 동결은 매치 잠금 안에서 함께 끝나고,
///     패킷 생성·전송은 같은 잠금 안에서 계획 순서대로 이어진다.
/// </summary>
internal sealed record SwarmClosurePublicationPlan(
    long MatchingId,
    ImmutableArray<SwarmClosureOutbound> Outbound);

internal abstract record SwarmClosureOutbound(ImmutableArray<int> RecipientOrdinals);

internal sealed record SwarmFieldStateOutbound(
    long StartedAtUnixMs,
    ImmutableArray<int> RecipientOrdinals)
    : SwarmClosureOutbound(RecipientOrdinals);

internal sealed record SwarmClosureWarningOutbound(
    AreaType Area,
    int SecondsRemaining,
    long ClosureAtUnixMs,
    ImmutableArray<int> RecipientOrdinals)
    : SwarmClosureOutbound(RecipientOrdinals);

internal sealed record SwarmAreaClosedOutbound(
    AreaType Area,
    ImmutableArray<int> RecipientOrdinals)
    : SwarmClosureOutbound(RecipientOrdinals);

internal sealed record SwarmDoorStateOutbound(
    int DoorId,
    ImmutableArray<int> RecipientOrdinals)
    : SwarmClosureOutbound(RecipientOrdinals);

internal sealed record SwarmInventoryUpdateOutbound(
    SwarmInGameItemSnapshot Item,
    ImmutableArray<int> RecipientOrdinals)
    : SwarmClosureOutbound(RecipientOrdinals);

internal sealed record SwarmRingVfxOutbound(
    long OwnerPlayerId,
    float CenterX,
    float CenterY,
    float Radius,
    int Kind,
    long VictimPlayerId,
    int FromOrdinal,
    ImmutableArray<int> RecipientOrdinals)
    : SwarmClosureOutbound(RecipientOrdinals);

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
