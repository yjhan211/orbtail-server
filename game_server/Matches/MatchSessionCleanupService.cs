using System.Collections.Concurrent;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.infrastructure.messaging;
using network.infrastructure.redis;

namespace game_server.matches;

/// <summary>
///     세션의 퇴장·게임 완료를 Redis와 UserServer에 알리고 매칭 데이터를 정리한다.
///     플레이어별 종료 알림이 중복되지 않도록 관리하며,
///     매칭 예약 해제, NATS 알림 발행, 종료된 매치의 Redis 데이터 삭제를 수행한다.
/// </summary>
public sealed class MatchSessionCleanupService(IRedisOperations redisOperations, INatsClient natsClient, ILogger logger)
    : IMatchSessionCleanup
{
    private const int MaxRememberedMatches = 4096;
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<long, string>> _playerNotifications = new();
    private readonly ConcurrentQueue<long> _matchOrder = new();
    private readonly ConcurrentDictionary<long, Task> _pendingTasks = new();
    private INatsClient? _natsClient = natsClient ?? throw new ArgumentNullException(nameof(natsClient));
    private long _nextTaskId;

    void IMatchSessionCleanup.PublishPlayerLeft(long playerId, long matchingId) =>
        Publish(MatchingLifecycleSubjects.PlayerLeft, playerId, matchingId);

    Action? IMatchSessionCleanup.PrepareGameCompletion(long playerId, long matchingId) =>
        PrepareNotification(MatchingLifecycleSubjects.PlayerCompleted, playerId, matchingId);

    void IMatchSessionCleanup.ReleaseMatchingReservation(long playerId, long matchingId) =>
        Publish(MatchingLifecycleSubjects.PlayerReleased, playerId, matchingId);

    internal void Publish(string subject, long playerId, long matchingId)
    {
        var dispatch = PrepareNotification(subject, playerId, matchingId);
        dispatch?.Invoke();
    }

    internal Action? PrepareNotification(string subject, long playerId, long matchingId)
    {
        if (playerId <= 0 || matchingId <= 0)
        {
            return null;
        }

        var newPlayerSubjects = new ConcurrentDictionary<long, string>();
        var playerSubjects = _playerNotifications.GetOrAdd(matchingId, newPlayerSubjects);
        if (ReferenceEquals(playerSubjects, newPlayerSubjects))
        {
            _matchOrder.Enqueue(matchingId);
            while (_playerNotifications.Count > MaxRememberedMatches && _matchOrder.TryDequeue(out long expiredMatchingId))
            {
                _playerNotifications.TryRemove(expiredMatchingId, out _);
            }
        }

        if (!playerSubjects.TryAdd(playerId, subject))
        {
            playerSubjects.TryGetValue(playerId, out string? existingSubject);
            logger.LogDebug(
                "Ignored duplicate or conflicting matching lifecycle terminal event: MatchingId={MatchingId}, PlayerId={PlayerId}, ExistingSubject={ExistingSubject}, IgnoredSubject={IgnoredSubject}",
                matchingId, playerId, existingSubject, subject);
            return null;
        }

        int dispatchStarted = 0;
        return () =>
        {
            if (Interlocked.Exchange(ref dispatchStarted, 1) != 0)
            {
                return;
            }

            long operationId = Interlocked.Increment(ref _nextTaskId);
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pendingTasks.TryAdd(operationId, completion.Task))
            {
                throw new InvalidOperationException($"Duplicate matching lifecycle operation id: {operationId}.");
            }
            _ = ReleaseReservationAndPublishAsync(subject, playerId, matchingId, operationId, completion);
        };
    }

    private async Task ReleaseReservationAndPublishAsync(string subject, long playerId, long matchingId, long operationId, TaskCompletionSource<bool> completion)
    {
        try
        {
            if (playerId > 0 && matchingId > 0)
            {
                string expectedReservation = matchingId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                bool released = await redisOperations.StringDeleteIfEqualsAsync(MatchingRedisKeys.ReservationKey(playerId), expectedReservation);
                if (!released)
                {
                    logger.LogWarning("Matching reservation was absent or changed before lifecycle publish: PlayerId={PlayerId}, MatchingId={MatchingId}", playerId, matchingId);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Matching reservation release failed before lifecycle publish: Subject={Subject}, PlayerId={PlayerId}, MatchingId={MatchingId}", subject, playerId, matchingId);
        }

        try
        {
            byte[] payload = MessagePackSerializer.Serialize(new G_TO_U_MATCHING_LIFECYCLE
            {
                PlayerId = playerId,
                MatchingId = matchingId
            }, MessagePackSerializerOptions.Standard);
            _natsClient?.Publish(subject, payload);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Matching lifecycle publish failed: Subject={Subject}, PlayerId={PlayerId}, MatchingId={MatchingId}", subject, playerId, matchingId);
        }

        CompleteCleanupTask(operationId, completion);
    }

    internal async Task CloseAsync()
    {
        var natsClient = Interlocked.Exchange(ref _natsClient, null);
        if (natsClient == null)
        {
            return;
        }

        try
        {
            await natsClient.CloseAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "NATS close failed during shutdown.");
        }
    }

    internal void StartMatchDataCleanup(long matchingId)
    {
        long operationId = Interlocked.Increment(ref _nextTaskId);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingTasks.TryAdd(operationId, completion.Task))
        {
            throw new InvalidOperationException($"Duplicate matching Redis cleanup operation id: {operationId}.");
        }

        try
        {
            _ = DeleteMatchDataAsync(matchingId, operationId, completion);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Unexpected matching Redis cleanup dispatch failure: MatchingId={MatchingId}, OperationId={OperationId}", matchingId, operationId);
            CompleteCleanupTask(operationId, completion);
        }
    }

    private async Task DeleteMatchDataAsync(long matchingId, long operationId, TaskCompletionSource<bool> completion)
    {
        try
        {
            await redisOperations.KeyDeleteAsync(MatchingRedisKeys.Key(matchingId));
            await redisOperations.HashDeleteAsync("matching_bots", matchingId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to remove matching data from Redis: MatchingId={MatchingId}",
                matchingId);
        }
        finally
        {
            CompleteCleanupTask(operationId, completion);
        }
    }

    private void CompleteCleanupTask(long operationId, TaskCompletionSource<bool> completion)
    {
        try
        {
            completion.TrySetResult(true);
        }
        finally
        {
            ICollection<KeyValuePair<long, Task>> pendingTasks = _pendingTasks;
            var completedTask = new KeyValuePair<long, Task>(operationId, completion.Task);
            pendingTasks.Remove(completedTask);
        }
    }

    internal async Task DrainAsync()
    {
        while (true)
        {
            var pendingCleanups = _pendingTasks.ToArray();
            if (pendingCleanups.Length == 0)
            {
                return;
            }

            await Task.WhenAll(pendingCleanups.Select(pair => pair.Value));
            foreach (var pendingCleanup in pendingCleanups)
            {
                ICollection<KeyValuePair<long, Task>> pendingTasks = _pendingTasks;
                pendingTasks.Remove(pendingCleanup);
            }
        }
    }
}
