using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using network.common;
using network.gamehandoff;
using network.infrastructure.routing;
using network.interfaces;
using user_server.network;

namespace user_server.services;

/// <summary>
///     매칭 서브시스템의 composition root이자 수명 조정자. 1초 timer로 <see cref="MatchmakingPass" />를 겹치지 않게
///     하나만 실행하고, 큐 등록/취소·claim 해제·입장 실패 통지를 collaborator에 위임하며,
///     background operation(watchdog·lifecycle handler)을 추적해 quiesce → stop 순서로 배수한다.
///     매칭 규칙·Redis 키·패킷 조립은 소유하지 않는다.
/// </summary>
public class MatchingManager : IMatchingManager
{
    private readonly ConcurrentDictionary<long, Task> _backgroundTasks = new();
    private readonly object _backgroundTaskLock = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly IPlayerSessionRouter _sessions;
    private readonly MatchingLeaderLease _leaderLease;
    private readonly MatchingQueueClaimCoordinator _matchingClaims;
    private readonly MatchingQueue _queue;
    private readonly MatchHandoffPublisher _handoff;
    private readonly MatchmakingPass _pass;
    private readonly ILogger _logger;
    private Timer? _matchingTimer;
    private readonly object _processingTaskLock = new();
    private int _isProcessing;
    private int _quiescing;
    private long _nextBackgroundTaskId;
    private Task _processingTask = Task.CompletedTask;
    private readonly object _stopTaskLock = new();
    private Task? _stopTask;
    private int _started;
    private int _stopping;

    internal MatchingManager(ILogger logger, ICacheHelper cacheHelper,
        IMatchingQueueClaimStore matchingClaimStore, IRedLockFactory redLock,
        IGameHandoffTicketService gameHandoffTicketService,
        IPlayerSessionRouter sessions,
        MatchingLeaderLease leaderLease)
    {
        _logger = logger;
        _sessions = sessions;
        _leaderLease = leaderLease;
        _matchingClaims = new MatchingQueueClaimCoordinator(cacheHelper, matchingClaimStore, logger);
        _queue = new MatchingQueue(cacheHelper, redLock, _matchingClaims, logger);

        DevMatchOverrides overrides = DevMatchOverrides.FromEnvironment(cacheHelper, redLock, logger);
        var rosterBuilder = new MatchRosterBuilder(cacheHelper, overrides, logger);
        _handoff = new MatchHandoffPublisher(
            cacheHelper,
            gameHandoffTicketService,
            _matchingClaims,
            sessions,
            TryRunBackgroundOperation,
            _shutdownCts.Token,
            logger);
        _pass = new MatchmakingPass(
            cacheHelper,
            _queue,
            _matchingClaims,
            rosterBuilder,
            _handoff,
            new GameServerAllocator(new RedisGameServerRegistry(cacheHelper), logger),
            overrides,
            _shutdownCts.Token,
            logger);
    }

    /// <summary>
    ///     lifecycle 구독이 준비된 뒤 큐 polling을 시작한다.
    /// </summary>
    public void Start()
    {
        lock (_stopTaskLock)
        {
            ObjectDisposedException.ThrowIf(_stopTask != null || Volatile.Read(ref _stopping) != 0, this);
            if (Volatile.Read(ref _started) != 0)
                throw new InvalidOperationException("MatchingManager is already started.");

            try
            {
                // 매칭 대기열을 1초마다 확인한다.
                _matchingTimer = new Timer(
                    OnMatchingTimerTick,
                    null,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1));
                Volatile.Write(ref _started, 1);
                _logger.LogInformation("MatchingManager started");
            }
            catch
            {
                Volatile.Write(ref _stopping, 1);
                _shutdownCts.Cancel();
                _matchingTimer?.Dispose();
                _matchingTimer = null;
                throw;
            }
        }
    }

    public Task<ErrorCode> AddToQueue(long playerId, GameSession user)
    {
        return _queue.AddToQueueAsync(playerId, user);
    }

    public Task<ErrorCode> CancelMatching(long playerId)
    {
        return _queue.CancelMatchingAsync(playerId);
    }

    /// <summary>
    ///     비동기 매칭 pass 하나를 시작하고 timer callback이 겹치지 않게 막는다.
    /// </summary>
    private void OnMatchingTimerTick(object? state)
    {
        if (Volatile.Read(ref _stopping) != 0 || Volatile.Read(ref _quiescing) != 0) return;
        if (Interlocked.CompareExchange(ref _isProcessing, 1, 0) != 0) return;

        lock (_processingTaskLock)
        {
            if (Volatile.Read(ref _stopping) != 0 || Volatile.Read(ref _quiescing) != 0)
            {
                Interlocked.Exchange(ref _isProcessing, 0);
                return;
            }

            _processingTask = RunMatchingPassAsync();
        }
    }

    private async Task RunMatchingPassAsync()
    {
        try
        {
            // 큐는 리더만 읽는다. 리더가 아니면 이번 tick은 비우고 다음 tick에 다시 lease를 본다.
            if (!await _leaderLease.TryAcquireOrRenewAsync())
                return;

            await _pass.RunAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Matching pass failed before queue processing completed");
        }
        finally
        {
            Interlocked.Exchange(ref _isProcessing, 0);
        }
    }

    /// <summary>이탈·완주 모두 Game Server가 보고한 정확한 active claim을 해제한다 — 이탈 페널티는 두지 않는다.</summary>
    public Task RecordLeaveAsync(long playerId, long matchingId) => ReleaseMatchingClaimAsync(playerId, matchingId);

    public Task RecordGameCompletionAsync(long playerId, long matchingId) => ReleaseMatchingClaimAsync(playerId, matchingId);

    public async Task AbortMatchingAdmissionAsync(long playerId, long matchingId)
    {
        // 이 매치에 배정되지 않은 세션은 라우터 쪽에서 보낼 것 없음으로 처리한다.
        await _handoff.NotifyAdmissionFailedAsync(playerId, matchingId);
        await ReleaseMatchingClaimAsync(playerId, matchingId);
    }

    public async Task ReleaseMatchingClaimAsync(long playerId, long matchingId)
    {
        await _matchingClaims.ReleaseActiveBestEffortAsync(playerId, matchingId);
    }

    public bool TryRunBackgroundOperation(Func<Task> operation, string operationName)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);

        lock (_backgroundTaskLock)
        {
            if (Volatile.Read(ref _stopping) != 0)
            {
                _logger.LogDebug(
                    "Ignoring matching background operation during shutdown: {OperationName}",
                    operationName);
                return false;
            }

            long operationId = Interlocked.Increment(ref _nextBackgroundTaskId);
            Task trackedTask = RunBackgroundOperationAsync(operationId, operation, operationName);
            _backgroundTasks.TryAdd(operationId, trackedTask);
            if (trackedTask.IsCompleted)
                _backgroundTasks.TryRemove(operationId, out _);
            return true;
        }
    }

    public Task StopAsync()
    {
        lock (_stopTaskLock)
        {
            return _stopTask ??= StopCoreAsync();
        }
    }

    public async Task QuiesceAsync()
    {
        if (Interlocked.Exchange(ref _quiescing, 1) == 0)
            _matchingTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        Task processingTask;
        lock (_processingTaskLock)
        {
            processingTask = _processingTask;
        }

        await processingTask;
    }

    private async Task RunBackgroundOperationAsync(
        long operationId,
        Func<Task> operation,
        string operationName)
    {
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Matching background operation failed: {OperationName}", operationName);
        }
        finally
        {
            _backgroundTasks.TryRemove(operationId, out _);
        }
    }

    private async Task StopCoreAsync()
    {
        await QuiesceAsync();
        lock (_backgroundTaskLock)
        {
            Volatile.Write(ref _stopping, 1);
            _shutdownCts.Cancel();
        }

        if (_matchingTimer != null)
            await _matchingTimer.DisposeAsync();

        Task processingTask;
        lock (_processingTaskLock)
        {
            processingTask = _processingTask;
        }

        await processingTask;

        while (true)
        {
            Task[] backgroundTasks;
            lock (_backgroundTaskLock)
            {
                backgroundTasks = _backgroundTasks.Values.ToArray();
            }

            if (backgroundTasks.Length == 0)
                break;
            await Task.WhenAll(backgroundTasks);
        }

        await _leaderLease.ReleaseAsync();
        _shutdownCts.Dispose();

        _logger.LogInformation("MatchingManager stopped");
    }
}
