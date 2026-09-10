using Microsoft.Extensions.Logging;
using network.common;
using user_server.matching.creation;
using user_server.matching.queue;
using user_server.sessions;

namespace user_server.matching;

/// <summary>
///     매칭의 시작과 종료를 관리하고, 대기열 등록·취소와 배정 해제 요청을 담당 클래스에 전달한다.
///     1초 주기의 타이머를 기다렸다가 리더 여부를 확인하고 매칭을 실행한다.
///     종료 시 타이머 대기를 중단하고 진행 중인 매칭이 끝날 때까지 기다린다.
///     전체 종료 시 리더 등록을 해제한다. 백그라운드 작업의 종료 순서는 UserServer가 관리한다.
/// </summary>
internal sealed class MatchingManager(
    ILogger<MatchingManager> logger,
    MatchingReservationService matchingReservations,
    MatchingQueue matchingQueue,
    MatchEntryService matchEntryService,
    MatchCreationService matchCreationService,
    MatchingLeaderLease leaderLease)
    : IMatchingManager
{
    private readonly object _lifecycleLock = new();

    private PeriodicTimer? _matchingTimer;
    private Task _matchingLoopTask = Task.CompletedTask;
    private bool _matchingLoopStopped;
    private bool _started;
    private Task? _stopTask;

    public void Start()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_stopTask != null || _matchingLoopStopped, this);
            if (_started)
                throw new InvalidOperationException("MatchingManager is already started.");

            try
            {
                _matchingTimer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                _matchingLoopTask = RunMatchingLoopAsync(_matchingTimer);
                _started = true;
                logger.LogInformation("MatchingManager started");
            }
            catch
            {
                _matchingLoopStopped = true;
                _matchingTimer?.Dispose();
                throw;
            }
        }
    }

    public Task<ErrorCode> AddToQueue(long playerId, PlayerSession user)
    {
        return matchingQueue.AddToQueueAsync(playerId, user);
    }

    public Task<ErrorCode> CancelMatching(long playerId)
    {
        return matchingQueue.CancelMatchingAsync(playerId);
    }

    public Task<bool> HasReservationAsync(long playerId)
    {
        return matchingReservations.HasReservationAsync(playerId);
    }

    private async Task RunMatchingLoopAsync(PeriodicTimer timer)
    {
        while (await timer.WaitForNextTickAsync())
        {
            try
            {
                if (!await leaderLease.TryAcquireOrRenewAsync())
                {
                    continue;
                }

                await matchCreationService.RunAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Matching pass failed before queue processing completed");
            }
        }
    }

    public async Task HandleEntryFailureAsync(long playerId, long matchingId)
    {
        await matchEntryService.NotifyEntryFailedAsync(playerId, matchingId);
        await ReleaseMatchingReservationAsync(playerId, matchingId);
    }

    public async Task ReleaseMatchingReservationAsync(long playerId, long matchingId)
    {
        await matchingReservations.ReleaseMatchingReservationAsync(playerId, matchingId);
    }

    public Task StopAsync()
    {
        lock (_lifecycleLock)
        {
            return _stopTask ??= StopCoreAsync();
        }
    }

    public Task StopMatchingLoopAsync()
    {
        lock (_lifecycleLock)
        {
            _matchingLoopStopped = true;
            _matchingTimer?.Dispose();
            return _matchingLoopTask;
        }
    }

    private async Task StopCoreAsync()
    {
        await StopMatchingLoopAsync();
        await leaderLease.ReleaseAsync();
        logger.LogInformation("MatchingManager stopped");
    }
}
