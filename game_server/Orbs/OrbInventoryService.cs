using game_server.items;
using game_server.logging;
using game_server.matches;
using network.common;
using network.common.data.models;

namespace game_server.orbs;

/// <summary>
///     매치 잠금 안에서 오브 소환의 인벤토리와 소환석 변경을 함께 처리한다.
///     소환 결과와 오브 구성 변경을 게임 이벤트로 기록한 뒤 결과를 반환한다.
///     플레이어별 상태는 MatchRuntime에 두고, 요청 검사와 패킷 전송은 세션이 맡는다.
/// </summary>
internal sealed class OrbInventoryService(GameEventLogManager eventLog)
{
    public SummonOrbAttempt Summon(MatchRuntime runtime, long playerId, AreaType area)
    {
        RequireLock(runtime);
        var attempt = runtime.SummonStones.TrySummon(playerId,
            itemId => runtime.Inventory.TryAddItemWithCapacity(
                playerId, itemId, Config.SWARM_ORB_CAPACITY, out var added) ? added : null);

        if (attempt is { Success: true, AddedItem: not null })
        {
            var inventory = runtime.Inventory.GetPlayerInventory(playerId);
            eventLog.LogOrbBoardTransition(runtime.MatchingId, playerId,
                inventory.GetAllItems(), inventory.GetOrderedOrbs().FirstOrDefault()?.ItemId ?? 0,
                area.ToString(), "summon", isBot: playerId < 0);
        }

        eventLog.LogOrbSummonAttempt(runtime.MatchingId, playerId,
            attempt.Success, attempt.ErrorCode, attempt.ItemId,
            attempt.State.StoneCount, attempt.State.NextCost, attempt.State.SuccessfulSummonCount,
            area.ToString(), isBot: playerId < 0);
        return attempt;
    }



    public void Grant(MatchRuntime runtime, long playerId, int itemId)
    {
        RequireLock(runtime);
        // 같은 종류라도 오브 하나마다 독립 ItemUid를 유지한다.
        runtime.Inventory.GetPlayerInventory(playerId)
            .TryAddItemWithCapacity(itemId, Config.SWARM_ORB_CAPACITY, out _);
    }

    private static void RequireLock(MatchRuntime runtime)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
            throw new InvalidOperationException("Orb inventory changes require the match lock.");
    }
}
