using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using network.common;
using network.interfaces;
using user_server.network;

namespace user_server.services;

/// <summary>
///     Game Server가 Core NATS로 보내는 매칭 lifecycle 4종(left/completed/admission_failed/released)을 받아
///     매칭 claim·leave penalty에 반영한다. payload는 playerId(8바이트 LE) 또는 playerId+matchingId(16바이트 LE)다.
/// </summary>
internal sealed class MatchingLifecycleSubscriber(
    INatsClient natsClient,
    UserSessionRegistry sessions,
    IMatchingManager matchingManager,
    ILogger logger)
{
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
            (_, body) => HandleLifecycleMessage(body, MatchingLifecycleEvent.PlayerLeft));
        natsClient.Subscribe(
            MatchingLifecycleSubjects.PlayerCompleted,
            (_, body) => HandleLifecycleMessage(body, MatchingLifecycleEvent.PlayerCompleted));
        natsClient.Subscribe(
            MatchingLifecycleSubjects.PlayerAdmissionFailed,
            (_, body) => HandleLifecycleMessage(body, MatchingLifecycleEvent.PlayerAdmissionFailed));
        natsClient.Subscribe(
            MatchingLifecycleSubjects.PlayerReleased,
            (_, body) => HandleLifecycleMessage(body, MatchingLifecycleEvent.PlayerReleased));
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
            sessions.Get(playerId)?.ClearMatchingAssignment(matchingId);
        matchingManager.TryRunBackgroundOperation(
            () => lifecycleEvent switch
            {
                MatchingLifecycleEvent.PlayerCompleted =>
                    matchingManager.RecordGameCompletionAsync(playerId, matchingId),
                MatchingLifecycleEvent.PlayerAdmissionFailed =>
                    matchingManager.AbortMatchingAdmissionAsync(playerId, matchingId),
                MatchingLifecycleEvent.PlayerReleased =>
                    matchingManager.ReleaseMatchingClaimAsync(playerId, matchingId),
                _ => matchingManager.RecordLeaveAsync(playerId, matchingId)
            },
            $"handle {lifecycleEvent} for player {playerId}, matching {matchingId}");
    }
}
