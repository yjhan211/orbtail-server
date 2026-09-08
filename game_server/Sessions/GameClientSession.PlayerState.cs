using game_server.services;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;

namespace game_server.sessions;

/// <summary>행동 상태 변경 요청을 검증하고 상호작용 취소·상태 변경·전송을 조율한다.</summary>
public partial class GameClientSession
{
    private Task HandlePlayerState(C_TO_G_PLAYER_STATE msg)
    {
        if (!PlayerId.HasValue)
        {
            return Task.CompletedTask;
        }

        var match = Volatile.Read(ref _match);
        if (match == null)
        {
            return Task.CompletedTask;
        }

        using (match.Enter())
        {
            if (match.IsTerminal)
            {
                return Task.CompletedTask;
            }
            if (IsGameplayActionBlocked(out var errorCode))
            {
                Logger.LogWarning("Ignored player state while gameplay is locked: PlayerId={PlayerId}, State={State}, ErrorCode={ErrorCode}", PlayerId, msg.State, errorCode);
                return Task.CompletedTask;
            }

            bool isExploreState = msg.State == PlayerState.EXPLORE_1;
            if (!isExploreState && msg.State != PlayerState.IDLE && _interactions.Count > 0)
            {
                Logger.LogWarning("Ignored state change while RNG collect is pending: PlayerId={PlayerId}, State={State}", PlayerId, msg.State);
                return Task.CompletedTask;
            }

            if (msg.State == PlayerState.SLEEP)
            {
                int[] canceledIds = MatchInteractionService.CancelPendingInteractions(match, _interactions);
                SendInteractionCanceled(canceledIds, "PlayerState:SLEEP");
                if (!_condition.IsSleeping && _condition.TryStartSleep(DateTime.UtcNow))
                {
                    Notifications.SendState();
                }
                return Task.CompletedTask;
            }

            if (!isExploreState)
            {
                int[] canceledIds = MatchInteractionService.CancelPendingInteractions(match, _interactions);
                SendInteractionCanceled(canceledIds, $"PlayerState:{msg.State}");
            }

            _condition.State = isExploreState ? PlayerState.EXPLORE_1 : PlayerState.IDLE;
            Notifications.SendState(includeSelf: false);
        }

        return Task.CompletedTask;
    }
}
