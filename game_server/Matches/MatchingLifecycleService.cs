using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.infrastructure.messaging;
using network.infrastructure.redis;
using System.Collections.Concurrent;

namespace game_server.matches;

/// <summary>
///     매치 종료 후 Redis 예약·인계 정보를 정리하고 UserServer에 종료 사실을 알린다.
///     플레이어별 종료 알림의 중복을 막고, 지연 실행할 작업을 준비하며 완료까지 추적한다.
///     GameServer는 연결과 타이머를 멈춘 뒤 DrainAsync로 기다리고 마지막에 NATS를 닫는다.
///     매치의 게임 상태나 잠금은 소유하지 않는다.
/// </summary>
public sealed class MatchingLifecycleService(IRedisOperations redisOperations, INatsClient natsClient, ILogger logger)
    : IGameSessionLifecycle
{


    private const int MatchingLifecycleTerminalMatchRetention = 4096;
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<long, string>>
        _matchingLifecycleTerminalSubjects = new();
    private readonly ConcurrentQueue<long> _matchingLifecycleTerminalMatchOrder = new();
    private readonly ConcurrentDictionary<long, Task> _pendingMatchingRedisCleanupTasks = new();
    private INatsClient? _matchingLifecycleNatsClient = natsClient ?? throw new ArgumentNullException(nameof(natsClient));
    private long _nextMatchingRedisCleanupId;

    void IGameSessionLifecycle.PublishPlayerLeft(long playerId, long matchingId) =>
        Publish(MatchingLifecycleSubjects.PlayerLeft, playerId, matchingId);

    Action? IGameSessionLifecycle.PrepareGameCompletion(long playerId, long matchingId) =>
        PreparePublication(MatchingLifecycleSubjects.PlayerCompleted, playerId, matchingId);

    void IGameSessionLifecycle.ReleaseMatchingReservation(long playerId, long matchingId) =>
        Publish(MatchingLifecycleSubjects.PlayerReleased, playerId, matchingId);

    /// <summary>
    ///     플레이어의 exact matching reservation 해제를 먼저 시도한 뒤 NATS Core로 종료 사실을 알린다.
    ///     Redis와 NATS 중 한 경로만 성공해도 user_server가 배정을 복구할 수 있고, 둘 다 실패하면 reservation TTL이 남는다.
    /// </summary>
    internal void Publish(string subject, long playerId, long matchingId)
    {
        Action? dispatch = PreparePublication(subject, playerId, matchingId);
        dispatch?.Invoke();
    }

    /// <summary>
    ///     플레이어별 terminal subject를 지금 선점하고, 실제 Core publish는 한 번만 실행되는 Action으로
    ///     돌려준다. 완료(PlayerCompleted) 이벤트는 match runtime finalization commit 뒤에 dispatch된다.
    /// </summary>
    internal Action? PreparePublication(string subject, long playerId, long matchingId)
    {
        if (!TryRegisterMatchingLifecycleTerminal(subject, playerId, matchingId))
            return null;

        int dispatchStarted = 0;
        return () =>
        {
            if (Interlocked.Exchange(ref dispatchStarted, 1) != 0)
                return;

            StartMatchingLifecyclePublication(subject, playerId, matchingId);
        };
    }

    /// <summary>
    ///     한 매치의 한 플레이어는 terminal subject(left/completed/entry_failed/released)를 하나만
    ///     발행한다. 서로 다른 종료 원인이 중복되면 세션 통지와 reservation 해제의 의미가 충돌한다.
    /// </summary>
    private bool TryRegisterMatchingLifecycleTerminal(
        string subject,
        long playerId,
        long matchingId)
    {
        if (playerId <= 0 || matchingId <= 0)
            return true;

        if (!_matchingLifecycleTerminalSubjects.TryGetValue(
                matchingId,
                out ConcurrentDictionary<long, string>? playerSubjects))
        {
            var candidate = new ConcurrentDictionary<long, string>();
            if (_matchingLifecycleTerminalSubjects.TryAdd(matchingId, candidate))
            {
                playerSubjects = candidate;
                _matchingLifecycleTerminalMatchOrder.Enqueue(matchingId);
                while (_matchingLifecycleTerminalSubjects.Count >
                       MatchingLifecycleTerminalMatchRetention &&
                       _matchingLifecycleTerminalMatchOrder.TryDequeue(out long expiredMatchingId))
                {
                    _matchingLifecycleTerminalSubjects.TryRemove(expiredMatchingId, out _);
                }
            }
            else
            {
                playerSubjects = _matchingLifecycleTerminalSubjects[matchingId];
            }
        }

        if (playerSubjects.TryAdd(playerId, subject))
            return true;

        playerSubjects.TryGetValue(playerId, out string? existingSubject);
        logger.LogDebug(
            "Ignored duplicate or conflicting matching lifecycle terminal event: MatchingId={MatchingId}, PlayerId={PlayerId}, ExistingSubject={ExistingSubject}, IgnoredSubject={IgnoredSubject}",
            matchingId,
            playerId,
            existingSubject,
            subject);
        return false;
    }

    private void StartMatchingLifecyclePublication(string subject, long playerId, long matchingId)
    {
        long operationId = Interlocked.Increment(ref _nextMatchingRedisCleanupId);
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingMatchingRedisCleanupTasks.TryAdd(operationId, completion.Task))
        {
            throw new InvalidOperationException(
                $"Duplicate matching lifecycle operation id: {operationId}.");
        }

        _ = RunTrackedMatchingLifecyclePublicationAsync(
            subject,
            playerId,
            matchingId,
            operationId,
            completion);
    }

    private async Task RunTrackedMatchingLifecyclePublicationAsync(
        string subject,
        long playerId,
        long matchingId,
        long operationId,
        TaskCompletionSource<bool> completion)
    {
        try
        {
            try
            {
                await ReleaseMatchingReservationBeforeLifecycleAsync(playerId, matchingId);
            }
            catch (Exception ex)
            {
                logger.LogCritical(
                    ex,
                    "Unexpected matching reservation release failure before lifecycle publish: Subject={Subject}, PlayerId={PlayerId}, MatchingId={MatchingId}",
                    subject,
                    playerId,
                    matchingId);
            }

            PublishMatchingLifecycleCore(subject, playerId, matchingId);
        }
        finally
        {
            CompleteMatchingRedisCleanup(operationId, completion);
        }
    }

    private async Task ReleaseMatchingReservationBeforeLifecycleAsync(long playerId, long matchingId)
    {
        if (playerId <= 0 || matchingId <= 0)
            return;

        string expectedReservation = matchingId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            bool released = await redisOperations.StringDeleteIfEqualsAsync(
                MatchingRedisKeys.ReservationKey(playerId),
                expectedReservation);
            if (!released)
            {
                logger.LogWarning(
                    "Matching reservation was absent or changed before lifecycle publish: PlayerId={PlayerId}, MatchingId={MatchingId}",
                    playerId,
                    matchingId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Matching reservation release failed before lifecycle publish: PlayerId={PlayerId}, MatchingId={MatchingId}",
                playerId,
                matchingId);
        }
    }

    private void PublishMatchingLifecycleCore(string subject, long playerId, long matchingId)
    {
        try
        {
            byte[] payload = MessagePackSerializer.Serialize(new G_TO_U_MATCHING_LIFECYCLE
            {
                PlayerId = playerId,
                MatchingId = matchingId
            }, MessagePackSerializerOptions.Standard);
            _matchingLifecycleNatsClient?.Publish(subject, payload);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Matching lifecycle publish failed: Subject={Subject}, PlayerId={PlayerId}, MatchingId={MatchingId}",
                subject,
                playerId,
                matchingId);
        }
    }

    internal async Task CloseAsync()
    {
        INatsClient? natsClient = Interlocked.Exchange(ref _matchingLifecycleNatsClient, null);
        if (natsClient == null)
            return;

        try
        {
            await natsClient.CloseAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "NATS close failed during shutdown.");
        }
    }

    private async Task CleanupAbandonedMatchingRedisAsync(long matchingId)
    {
        try
        {
            await redisOperations.KeyDeleteAsync(MatchingRedisKeys.Key(matchingId));
            await redisOperations.HashDeleteAsync("matching_bots", matchingId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to remove abandoned matching from Redis: MatchingId={MatchingId}",
                matchingId);
        }
    }

    /// <summary>Redis 정리를 추적 목록에 등록한 뒤 시작한다. 서버 종료는 DrainAsync로 완료를 기다린다.</summary>
    internal void StartRedisCleanup(long matchingId)
    {
        long operationId = Interlocked.Increment(ref _nextMatchingRedisCleanupId);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingMatchingRedisCleanupTasks.TryAdd(operationId, completion.Task))
            throw new InvalidOperationException($"Duplicate matching Redis cleanup operation id: {operationId}.");

        try
        {
            _ = RunTrackedMatchingRedisCleanupAsync(matchingId, operationId, completion);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Unexpected matching Redis cleanup dispatch failure: MatchingId={MatchingId}, OperationId={OperationId}",
                matchingId, operationId);
            CompleteMatchingRedisCleanup(operationId, completion);
        }
    }

    private async Task RunTrackedMatchingRedisCleanupAsync(
        long matchingId,
        long operationId,
        TaskCompletionSource<bool> completion)
    {
        try
        {
            await CleanupAbandonedMatchingRedisAsync(matchingId);
        }
        catch (Exception ex)
        {
            logger.LogCritical(
                ex,
                "Unexpected matching Redis cleanup failure: MatchingId={MatchingId}, OperationId={OperationId}",
                matchingId,
                operationId);
        }
        finally
        {
            CompleteMatchingRedisCleanup(operationId, completion);
        }
    }

    private void CompleteMatchingRedisCleanup(
        long operationId,
        TaskCompletionSource<bool> completion)
    {
        try
        {
            completion.TrySetResult(true);
        }
        finally
        {
            ((ICollection<KeyValuePair<long, Task>>)_pendingMatchingRedisCleanupTasks)
                .Remove(new KeyValuePair<long, Task>(operationId, completion.Task));
        }
    }

    internal async Task DrainAsync()
    {
        while (true)
        {
            var pendingCleanups = _pendingMatchingRedisCleanupTasks.ToArray();
            if (pendingCleanups.Length == 0)
                return;

            await Task.WhenAll(pendingCleanups.Select(pair => pair.Value));
            foreach (var pendingCleanup in pendingCleanups)
            {
                ((ICollection<KeyValuePair<long, Task>>)_pendingMatchingRedisCleanupTasks)
                    .Remove(pendingCleanup);
            }
        }
    }
}
