using Microsoft.Extensions.Logging;
using network.common;
using network.contracts.messaging;
using network.infrastructure;
using network.interfaces;

namespace game_server.services;

/// <summary>
///     Runs one bounded Redis-outbox publisher loop for this GameServer process. Redis claim
///     leases coordinate all GameServer replicas; JetStream message ids make a publish repeated
///     after an ambiguous acknowledgement safe.
/// </summary>
public sealed class MatchingLifecycleOutboxWorker(
    MatchingLifecycleOutboxStore outboxStore,
    INatsClient natsClient,
    ILogger logger)
{
    private const int MaximumBatchSize = 16;
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FailedPublishRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FailurePollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private CancellationTokenSource? _workerCancellation;
    private Task? _workerTask;
    private int _acceptingEnqueues;

    public void Start()
    {
        lock (_stateLock)
        {
            if (_workerTask != null)
                return;

            _workerCancellation = new CancellationTokenSource();
            Volatile.Write(ref _acceptingEnqueues, 1);
            _workerTask = Task.Run(() => RunAsync(_workerCancellation.Token));
        }
    }

    public async Task<MatchingLifecycleOutboxEnqueueResult> EnqueueAsync(
        MatchingLifecycleOutboxRecord record)
    {
        if (Volatile.Read(ref _acceptingEnqueues) == 0)
            throw new InvalidOperationException("Matching lifecycle outbox worker is not accepting new events.");

        MatchingLifecycleOutboxEnqueueResult result = await outboxStore.EnqueueAsync(record);
        Wake();
        return result;
    }

    public async Task StopAsync()
    {
        Task? workerTask;
        CancellationTokenSource? workerCancellation;
        lock (_stateLock)
        {
            Volatile.Write(ref _acceptingEnqueues, 0);
            workerTask = _workerTask;
            workerCancellation = _workerCancellation;
            _workerTask = null;
            _workerCancellation = null;
        }

        if (workerTask == null || workerCancellation == null)
            return;

        workerCancellation.Cancel();
        Wake();
        try
        {
            await workerTask.WaitAsync(StopTimeout);
        }
        catch (TimeoutException ex)
        {
            logger.LogWarning(
                ex,
                "Matching lifecycle outbox worker did not stop within {Timeout}; continuing to wait for exact claim rescheduling before NATS close",
                StopTimeout);
            await workerTask;
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation interrupts a Redis or NATS wait.
        }
        finally
        {
            workerCancellation.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            int claimedCount;
            try
            {
                IReadOnlyList<MatchingLifecycleOutboxClaim> claims =
                    await outboxStore.ClaimDueAsync(MaximumBatchSize, ClaimLease);
                claimedCount = claims.Count;
                for (int index = 0; index < claims.Count; index++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        await RescheduleClaimsBestEffortAsync(claims, index);
                        return;
                    }

                    try
                    {
                        await PublishClaimAsync(claims[index], cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        await RescheduleClaimsBestEffortAsync(claims, index);
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Matching lifecycle outbox batch failed; Redis leases will retry");
                claimedCount = 0;
                if (!await WaitForSignalAsync(FailurePollInterval, cancellationToken))
                    return;
                continue;
            }

            if (claimedCount == 0 && !await WaitForSignalAsync(IdlePollInterval, cancellationToken))
                return;
        }
    }

    private async Task PublishClaimAsync(
        MatchingLifecycleOutboxClaim claim,
        CancellationToken cancellationToken)
    {
        if (claim.RawRecord == null)
        {
            bool removed = await outboxStore.RemoveMissingAsync(claim);
            if (removed)
            {
                logger.LogDebug(
                    "Removed stale lifecycle outbox index entry: Fingerprint={Fingerprint}",
                    claim.EventIdFingerprint);
            }
            return;
        }

        MatchingLifecycleOutboxRecord? record = claim.Record;
        if (record == null ||
            !string.Equals(
                record.EventIdFingerprint,
                claim.EventIdFingerprint,
                StringComparison.Ordinal))
        {
            logger.LogError(
                "Lifecycle outbox record is malformed; it will remain bounded by record TTL: Fingerprint={Fingerprint}",
                claim.EventIdFingerprint);
            return;
        }

        try
        {
            NatsDurablePublishAck ack = await natsClient.PublishDurableAsync(
                MatchingLifecycleSubjects.Stream,
                record.Subject,
                record.EventId,
                record.Payload,
                cancellationToken);
            bool completed = await outboxStore.CompleteAsync(claim);
            if (!completed)
            {
                logger.LogDebug(
                    "Lifecycle outbox completion lost its exact Redis claim; duplicate publish remains safe: EventId={EventId}, Stream={Stream}, Sequence={Sequence}",
                    record.EventId,
                    ack.Stream,
                    ack.Sequence);
                return;
            }

            logger.LogDebug(
                "Lifecycle outbox event published: EventId={EventId}, Stream={Stream}, Sequence={Sequence}, Duplicate={Duplicate}",
                record.EventId,
                ack.Stream,
                ack.Sequence,
                ack.Duplicate);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Lifecycle outbox publish failed; rescheduling the exact claim for retry: EventId={EventId}, Fingerprint={Fingerprint}",
                record.EventId,
                record.EventIdFingerprint);
            await RescheduleClaimBestEffortAsync(claim);
        }
    }

    private async Task RescheduleClaimsBestEffortAsync(
        IReadOnlyList<MatchingLifecycleOutboxClaim> claims,
        int startIndex)
    {
        for (int index = startIndex; index < claims.Count; index++)
            await RescheduleClaimBestEffortAsync(claims[index]);
    }

    private async Task RescheduleClaimBestEffortAsync(MatchingLifecycleOutboxClaim claim)
    {
        try
        {
            bool rescheduled = await outboxStore.RescheduleAsync(
                claim,
                FailedPublishRetryDelay);
            if (!rescheduled)
            {
                logger.LogDebug(
                    "Lifecycle outbox claim could not be rescheduled because its exact lease changed: Fingerprint={Fingerprint}",
                    claim.EventIdFingerprint);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Lifecycle outbox claim reschedule failed; lease expiry remains the fallback: Fingerprint={Fingerprint}",
                claim.EventIdFingerprint);
        }
    }

    private async Task<bool> WaitForSignalAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await _wakeSignal.WaitAsync(timeout, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private void Wake()
    {
        try
        {
            _wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // One pending wake-up is enough for a single worker loop.
        }
    }
}
