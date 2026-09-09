using game_server.matches.field;
using game_server.players;
using game_server.matches;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.sessions;

/// <summary>
///     플레이어의 행동 상태 변경 요청을 처리하고, 체력과 행동 상태를 전송한다.
///     행동 변경 시 매치 상태를 확인하고 필요한 상호작용을 취소한다.
///     체력은 본인에게, 행동 상태는 같은 구역의 플레이어들에게 전송한다.
///     전송 메서드를 호출할 때는 매치 잠금을 유지해야 한다.
/// </summary>
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
            if (match.IsEnded)
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
                Logger.LogWarning("Ignored state change while door opening is pending: PlayerId={PlayerId}, State={State}", PlayerId, msg.State);
                return Task.CompletedTask;
            }

            if (msg.State == PlayerState.SLEEP)
            {
                int[] canceledIds = MatchInteractionService.CancelPendingInteractions(match, _interactions);
                SendInteractionCanceled(canceledIds, "PlayerState:SLEEP");
                if (!_condition.IsSleeping && _condition.TryStartSleep(DateTime.UtcNow))
                {
                    SendPlayerState();
                }
                return Task.CompletedTask;
            }

            if (!isExploreState)
            {
                int[] canceledIds = MatchInteractionService.CancelPendingInteractions(match, _interactions);
                SendInteractionCanceled(canceledIds, $"PlayerState:{msg.State}");
            }

            _condition.State = isExploreState ? PlayerState.EXPLORE_1 : PlayerState.IDLE;
            SendPlayerState(includeSelf: false);
        }

        return Task.CompletedTask;
    }

    internal void SendHealth(PlayerCondition.HealthChange change)
    {
        using var packet = PacketMaker.G_TO_C_PLAYER_STATS_UPDATE(change.After, change.RequestedDelta);
        TrySend(packet);
    }

    internal void SendPlayerState(bool includeSelf = true)
    {
        if (!PlayerId.HasValue)
        {
            return;
        }
        var targetSessions = new List<GameClientSession>();
        foreach (var session in Match.Sessions.Values.ToList())
        {
            if (session.IsEliminated || session.CurrentArea != CurrentArea)
                continue;
            if (!includeSelf && session.PlayerId == PlayerId)
                continue;
            targetSessions.Add(session);
        }
        using var packet = PacketMaker.G_TO_C_PLAYER_STATE(PlayerId.Value, Condition.State);
        foreach (var targetSession in targetSessions)
        {
            targetSession.TrySend(packet);
        }
        Logger.LogInformation("Player state sent: PlayerId={PlayerId}, State={State}, Recipients={Count}", PlayerId, Condition.State, targetSessions.Count);
    }
}
