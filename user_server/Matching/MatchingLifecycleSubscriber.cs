using MessagePack;
using network.common.data.models;
using network.infrastructure;
using Microsoft.Extensions.Logging;
using network.common;
using network.infrastructure.messaging;
using user_server.sessions;

namespace user_server.matching;

/// <summary>
///     GameServer가 보내는 퇴장·게임 완료·입장 실패·예약 해제 알림을 NATS로 받는다.
///     받은 알림에 따라 세션의 매칭 배정을 해제하고, 예약 해제나 입장 실패 처리를 MatchingManager에 요청한다.
///     비동기 처리 작업은 BackgroundTaskTracker에 등록해 예외와 종료 시 완료 대기를 관리한다.
///
///     여러 UserServer 중 하나만 알림을 받도록 큐 그룹을 사용하며,
///     다른 서버에 연결된 플레이어의 세션 처리는 라우터에 맡긴다.
/// </summary>
internal sealed class MatchingLifecycleSubscriber(
    INatsClient natsClient,
    IPlayerSessionRouter sessions,
    IMatchingManager matchingManager,
    BackgroundTaskTracker taskTracker,
    ILogger logger)
{
    private const string QueueGroup = "user_server.matching_lifecycle";
    private static readonly MessagePackSerializerOptions SerializerOptions =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    private enum MatchingLifecycleEvent
    {
        PlayerLeft,
        PlayerCompleted,
        PlayerEntryFailed,
        PlayerReleased
    }

    public void Start()
    {
        natsClient.Subscribe(
            MatchingLifecycleSubjects.PlayerLeft,
            (_, body) => HandleLifecycleMessage(body, MatchingLifecycleEvent.PlayerLeft),
            QueueGroup);
        natsClient.Subscribe(
            MatchingLifecycleSubjects.PlayerCompleted,
            (_, body) => HandleLifecycleMessage(body, MatchingLifecycleEvent.PlayerCompleted),
            QueueGroup);
        natsClient.Subscribe(
            MatchingLifecycleSubjects.PlayerEntryFailed,
            (_, body) => HandleLifecycleMessage(body, MatchingLifecycleEvent.PlayerEntryFailed),
            QueueGroup);
        natsClient.Subscribe(
            MatchingLifecycleSubjects.PlayerReleased,
            (_, body) => HandleLifecycleMessage(body, MatchingLifecycleEvent.PlayerReleased),
            QueueGroup);
    }

    public async Task StopAsync()
    {
        try
        {
            await natsClient.CloseAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "NATS close failed during shutdown");
        }
    }

    private void HandleLifecycleMessage(byte[] body, MatchingLifecycleEvent lifecycleEvent)
    {
        G_TO_U_MATCHING_LIFECYCLE? message;
        try
        {
            message = MessagePackSerializer.Deserialize<G_TO_U_MATCHING_LIFECYCLE>(body, SerializerOptions);
        }
        catch (MessagePackSerializationException ex)
        {
            logger.LogWarning(ex, "Invalid matching lifecycle message: Event={Event}", lifecycleEvent);
            return;
        }
        if (message.PlayerId <= 0 || message.MatchingId <= 0)
        {
            logger.LogWarning("Matching lifecycle message requires positive playerId and matchingId: Event={Event}", lifecycleEvent);
            return;
        }

        long playerId = message.PlayerId;
        long matchingId = message.MatchingId;

        if (lifecycleEvent != MatchingLifecycleEvent.PlayerEntryFailed)
        {
            sessions.ClearMatchingAssignment(playerId, matchingId);
        }
        taskTracker.TryRun(
            () => lifecycleEvent switch
            {
                MatchingLifecycleEvent.PlayerEntryFailed =>
                    matchingManager.HandleEntryFailureAsync(playerId, matchingId),
                _ => matchingManager.ReleaseMatchingReservationAsync(playerId, matchingId)
            },
            $"handle {lifecycleEvent} for player {playerId}, matching {matchingId}");
    }
}
