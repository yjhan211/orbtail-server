using game_server.players;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.sessions;

/// <summary>
///     플레이어의 행동 상태 변경 요청을 처리하고 체력 변경을 전송한다.
///     행동 변경 시 매치 상태를 확인하고 필요한 상호작용을 취소한다.
///     체력은 본인에게 전송하며 행동 상태는 틱 끝 동기화에서 전송한다.
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
            if (!isExploreState && msg.State != PlayerState.IDLE && Player.Interactions.PendingInteractId.HasValue)
            {
                Logger.LogWarning("Ignored state change while door opening is pending: PlayerId={PlayerId}, State={State}", PlayerId, msg.State);
                return Task.CompletedTask;
            }

            if (msg.State == PlayerState.SLEEP)
            {
                if (_interactions.CancelPendingInteractions(match, Player) is { } canceledForSleep)
                {
                    SendDoorOpenInterrupted(canceledForSleep);
                }
                Player.TryStartSleep();
                return Task.CompletedTask;
            }

            if (!isExploreState)
            {
                if (_interactions.CancelPendingInteractions(match, Player) is { } canceledId)
                {
                    SendDoorOpenInterrupted(canceledId);
                }
            }

            Player.State = isExploreState ? PlayerState.EXPLORE_1 : PlayerState.IDLE;
        }

        return Task.CompletedTask;
    }

    internal void SendHealth(Player.HealthChange change)
    {
        using var packet = PacketMaker.G_TO_C_PLAYER_STATS_UPDATE(change.After, change.RequestedDelta);
        TrySend(packet);
    }
}
