using System.Buffers.Binary;
using network.infrastructure;
using Microsoft.Extensions.Logging;
using network.common;
using network.infrastructure.messaging;
using user_server.sessions;

namespace user_server.matching.coordination;

/// <summary>
///     Game Server가 Core NATS로 보내는 매칭 lifecycle 4종(left/completed/admission_failed/released)을 받아
///     매칭 claim에 반영한다. payload는 playerId(8바이트 LE) 또는 playerId+matchingId(16바이트 LE)다.
///     User Server가 여럿이면 큐 그룹으로 한 프로세스만 받는다. 세션 배정 해제는
///     라우터가 세션을 가진 프로세스로 넘긴다.
/// </summary>
internal sealed class MatchingLifecycleSubscriber(
    INatsClient natsClient,
    IPlayerSessionRouter sessions,
    IMatchingManager matchingManager,
    BackgroundTaskTracker taskTracker,
    ILogger logger)
{
    public const string QueueGroup = "user_server.matching_lifecycle";

    private enum MatchingLifecycleEvent
    {
        PlayerLeft,
        PlayerCompleted,
        PlayerAdmissionFailed,
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
            MatchingLifecycleSubjects.PlayerAdmissionFailed,
            (_, body) => HandleLifecycleMessage(body, MatchingLifecycleEvent.PlayerAdmissionFailed),
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
        if (body.Length != sizeof(long) && body.Length != sizeof(long) * 2)
        {
            logger.LogWarning("Invalid matching lifecycle message length: {Length}", body.Length);
            return;
        }

        if (lifecycleEvent is MatchingLifecycleEvent.PlayerAdmissionFailed or MatchingLifecycleEvent.PlayerReleased &&
            body.Length != sizeof(long) * 2)
        {
            logger.LogWarning(
                "Release-only lifecycle message requires playerId + matchingId payload: Event={Event}, Length={Length}",
                lifecycleEvent,
                body.Length);
            return;
        }

        long playerId = BinaryPrimitives.ReadInt64LittleEndian(body);
        long matchingId = body.Length == sizeof(long) * 2
            ? BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(sizeof(long)))
            : 0;
        if (matchingId > 0 && lifecycleEvent != MatchingLifecycleEvent.PlayerAdmissionFailed)
            sessions.ClearMatchingAssignment(playerId, matchingId);
        taskTracker.TryRun(
            () => lifecycleEvent switch
            {
                MatchingLifecycleEvent.PlayerAdmissionFailed =>
                    matchingManager.HandleEntryFailureAsync(playerId, matchingId),
                _ => matchingManager.ReleaseMatchingClaimAsync(playerId, matchingId)
            },
            $"handle {lifecycleEvent} for player {playerId}, matching {matchingId}");
    }
}
