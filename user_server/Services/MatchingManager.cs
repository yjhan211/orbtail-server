using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using network.common;
using network.contracts.authentication;
using network.infrastructure.routing;
using network.interfaces;
using user_server.network;

namespace user_server.services;

/// <summary>
///     매칭 서브시스템의 composition root이자 수명 조정자. 1초 timer로 <see cref="MatchmakingPass" />를 겹치지 않게
///     하나만 실행하고, 큐 등록/취소·이탈 페널티·claim 해제·입장 실패 통지를 collaborator에 위임하며,
///     background operation(watchdog·lifecycle handler)을 추적해 quiesce → stop 순서로 배수한다.
///     매칭 규칙·Redis 키·패킷 조립은 소유하지 않는다.
/// </summary>
public class MatchingManager : IMatchingManager
{
    private readonly ConcurrentDictionary<long, Task> _backgroundTasks = new();
    private readonly object _backgroundTaskLock = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Func<long, GameSession?> _getSession;
    private readonly MatchingQueueClaimCoordinator _matchingClaims;
    private readonly MatchingQueue _queue;
    private readonly LeavePenaltyService _leavePenalties;
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

    public MatchingManager(ILogger logger, ICacheHelper cacheHelper,
        IMatchingQueueClaimStore matchingClaimStore, IRedLockFactory redLock,
        IGameHandoffTicketService gameHandoffTicketService,
        Func<long, GameSession?> getSession)
    {
        _logger = logger;
        _getSession = getSession;
        _matchingClaims = new MatchingQueueClaimCoordinator(cacheHelper, matchingClaimStore, logger);
        _leavePenalties = new LeavePenaltyService(cacheHelper, logger);
        _queue = new MatchingQueue(cacheHelper, redLock, _matchingClaims, _leavePenalties, logger);

        DevMatchOverrides overrides = DevMatchOverrides.FromEnvironment(cacheHelper, redLock, logger);
        var rosterBuilder = new MatchRosterBuilder(cacheHelper, overrides, logger);
        _handoff = new MatchHandoffPublisher(
            cacheHelper,
            redLock,
            gameHandoffTicketService,
            _matchingClaims,
            getSession,
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

    /// <summary>
    ///     이탈을 기록하고 Game Server가 보고한 정확한 active claim을 해제한다.
    /// </summary>
    public async Task RecordLeaveAsync(long playerId, long matchingId)
    {
        await ReleaseMatchingClaimAsync(playerId, matchingId);
        await _leavePenalties.RecordLeaveAsync(playerId);
    }

    public async Task RecordGameCompletionAsync(long playerId, long matchingId)
    {
        await ReleaseMatchingClaimAsync(playerId, matchingId);
        await _leavePenalties.RecordCompletionAsync(playerId);
    }

    public async Task AbortMatchingAdmissionAsync(long playerId, long matchingId)
    {
        GameSession? session = _getSession(playerId);
        string? requestId = session?.ActiveMatchingRequestId;
        if (MatchingRequestTokens.IsSafeTokenComponent(requestId))
            _handoff.NotifyAdmissionFailed(playerId, matchingId);

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

        _shutdownCts.Dispose();

        _logger.LogInformation("MatchingManager stopped");
    }
}
