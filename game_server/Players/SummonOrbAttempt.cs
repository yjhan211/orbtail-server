using network.common;
using network.common.data.models;

namespace game_server.players;

public readonly record struct SummonOrbAttempt(bool Success, ErrorCode ErrorCode, int ItemId,
    InGameItemInfo? AddedItem, SummonStoneSnapshot State)
{
    public static SummonOrbAttempt Failed(ErrorCode errorCode, SummonStoneSnapshot state) =>
        new(false, errorCode, 0, null, state);
}
