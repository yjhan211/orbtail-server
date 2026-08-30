using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using network.contracts.authentication;
using network.contracts.scaling;

namespace game_server.services;

/// <summary>
///     Owns this process generation's Redis node lease and keeps routing visibility aligned
///     with the GameServer admission lifecycle.
/// </summary>
public sealed class GameServerNodeLease(
    GameServerScalingOptions options,
    IGameServerRoutingStore routingStore,
    ILogger<GameServerNodeLease> logger)
{
    private const int MatchOwnerRenewalConcurrency = 8;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _matchStoreGate = new(1, 1);
    private readonly SemaphoreSlim _nodeStoreGate = new(1, 1);
    private readonly ConcurrentDictionary<long, OwnedMatchRegistration> _ownedMatches = new();
    private CancellationTokenSource? _heartbeatCts;
    private Task? _matchOwnerHeartbeatTask;
    private Task? _nodeHeartbeatTask;
    private Func<int> _activeMatchCount = static () => 0;
    private Action _leaseLost = static () => { };
    private Action<long, IReadOnlyList<long>> _matchOwnerLost = static (_, _) => { };
    private long _lastSuccessfulHeartbeatTimestamp;
    private int _leaseHeld;
    private int _leaseLostSignaled;
    private int _stopped;
    private GameServerNodeStatus _status = GameServerNodeStatus.Starting;

    public GameServerNodeIdentity Identity { get; } =
        new(options.NodeId, Guid.NewGuid().ToString("N"));

    public bool Enabled => options.Enabled;
    public bool HasLease => !options.Enabled || Volatile.Read(ref _leaseHeld) != 0;

    public async Task StartAsync(
        Func<int> activeMatchCount,
        Action leaseLost,
        Action<long, IReadOnlyList<long>> matchOwnerLost,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activeMatchCount);
        ArgumentNullException.ThrowIfNull(leaseLost);
        ArgumentNullException.ThrowIfNull(matchOwnerLost);
        if (!options.Enabled)
            return;
        if (Interlocked.CompareExchange(ref _stopped, 0, 0) != 0)
            throw new ObjectDisposedException(nameof(GameServerNodeLease));
        if (Volatile.Read(ref _leaseHeld) != 0)
            return;

        _activeMatchCount = activeMatchCount;
        _leaseLost = leaseLost;
        _matchOwnerLost = matchOwnerLost;
        _status = GameServerNodeStatus.Starting;

        bool acquired = await routingStore.TryAcquireNodeLeaseAsync(
            CreateDescriptor(GameServerNodeStatus.Starting),
            options.NodeLeaseLifetime);
        if (!acquired)
        {
            throw new InvalidOperationException(
                $"GameServer node id '{Identity.NodeId}' is already owned by another process generation.");
        }

        Volatile.Write(ref _leaseHeld, 1);
        Volatile.Write(ref _lastSuccessfulHeartbeatTimestamp, Stopwatch.GetTimestamp());
        try
        {
            _status = GameServerNodeStatus.Accepting;
            if (!await routingStore.TryHeartbeatNodeAsync(
                    CreateDescriptor(GameServerNodeStatus.Accepting),
                    options.NodeLeaseLifetime))
            {
                throw new InvalidOperationException("GameServer node lease was lost during startup.");
            }

            Volatile.Write(ref _lastSuccessfulHeartbeatTimestamp, Stopwatch.GetTimestamp());
            _heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _nodeHeartbeatTask = NodeHeartbeatLoopAsync(_heartbeatCts.Token);
            _matchOwnerHeartbeatTask = MatchOwnerHeartbeatLoopAsync(_heartbeatCts.Token);
            logger.LogInformation(
                "GameServer routing lease acquired: NodeId={NodeId}, Generation={Generation}, Endpoint={Host}:{Port}",
                Identity.NodeId,
                Identity.Generation,
                options.PublicHost,
                options.PublicPort);
        }
        catch
        {
            await routingStore.ReleaseNodeLeaseAsync(Identity);
            Volatile.Write(ref _leaseHeld, 0);
            throw;
        }
    }

    public async Task BeginDrainAsync()
    {
        if (!options.Enabled || Volatile.Read(ref _leaseHeld) == 0)
            return;

        await _nodeStoreGate.WaitAsync();
        try
        {
            if (Volatile.Read(ref _leaseHeld) == 0)
                return;

            _status = GameServerNodeStatus.Draining;
            bool draining = await routingStore.TryBeginDrainAsync(
                CreateDescriptor(GameServerNodeStatus.Draining),
                options.NodeLeaseLifetime);
            if (!draining)
            {
                SignalLeaseLost();
                throw new InvalidOperationException("GameServer node lease was lost while beginning drain.");
            }

            Volatile.Write(ref _lastSuccessfulHeartbeatTimestamp, Stopwatch.GetTimestamp());
        }
        finally
        {
            _nodeStoreGate.Release();
        }
    }

    public Task<int> GetOwnedMatchCountAsync() =>
        !options.Enabled
            ? Task.FromResult(0)
            : routingStore.GetOwnedMatchCountAsync(Identity);

    public async Task<GameHandoffContext?> ConsumeAndTrackMatchOwnerAsync(
        Func<Task<GameHandoffContext?>> consume)
    {
        ArgumentNullException.ThrowIfNull(consume);
        if (!options.Enabled || !HasLease)
            return null;

        await _matchStoreGate.WaitAsync();
        try
        {
            if (!HasLease)
                return null;

            // Redis grants only a node-lease-sized provisional owner while consuming the ticket.
            // Holding this gate across consume, local tracking, and active-owner promotion keeps
            // terminal cleanup from releasing the owner between those stages.
            GameHandoffContext? context = await consume();
            GameServerMatchOwner? owner = context?.GetGameServerOwner();
            if (context == null || owner is not { IsValid: true } ||
                !string.Equals(owner.NodeId, Identity.NodeId, StringComparison.Ordinal) ||
                !string.Equals(owner.Generation, Identity.Generation, StringComparison.Ordinal))
            {
                return null;
            }

            if (_ownedMatches.TryGetValue(owner.MatchingId, out OwnedMatchRegistration? existing))
            {
                if (existing.Owner == owner && existing.IsActive &&
                    await TryPromoteMatchOwnerAsync(existing))
                    return context;
                if (existing.Owner == owner)
                    return null;

                await routingStore.ReleaseMatchOwnerAsync(owner);
                throw new InvalidOperationException(
                    $"Consumed handoff owner fence conflicts with local match {owner.MatchingId}.");
            }

            var registration = new OwnedMatchRegistration(
                owner,
                context.HumanRoster
                    .Where(entry => entry.PlayerId > 0)
                    .Select(entry => entry.PlayerId)
                    .Distinct()
                    .ToArray());
            if (!_ownedMatches.TryAdd(owner.MatchingId, registration))
                throw new InvalidOperationException(
                    $"Failed to publish local ownership for match {owner.MatchingId}.");
            if (!await TryPromoteMatchOwnerAsync(registration))
                return null;
            return context;
        }
        finally
        {
            _matchStoreGate.Release();
        }
    }

    private async Task<bool> TryPromoteMatchOwnerAsync(OwnedMatchRegistration registration)
    {
        try
        {
            bool renewed = await routingStore.RenewMatchOwnerAsync(
                registration.Owner,
                options.ActiveOwnerLifetime);
            if (renewed)
                return true;

            if (RemoveOwnedMatchRegistration(registration))
                SignalMatchOwnerLost(registration);
            return false;
        }
        catch (Exception ex)
        {
            // The registration is already visible to the heartbeat loop. A lost promotion
            // response is therefore retried there, while the provisional Redis owner remains
            // bounded by the node lease if this process cannot make further progress.
            logger.LogWarning(
                ex,
                "Initial GameServer match-owner promotion was ambiguous; heartbeat will retry: MatchingId={MatchingId}, Fence={Fence}",
                registration.Owner.MatchingId,
                registration.Owner.Fence);
            return true;
        }
    }

    public async Task<bool> ReleaseMatchOwnerAsync(long matchingId)
    {
        if (!options.Enabled || matchingId <= 0)
            return false;

        await _matchStoreGate.WaitAsync();
        try
        {
            if (!_ownedMatches.TryGetValue(matchingId, out OwnedMatchRegistration? registration))
                return false;
            if (!registration.TryBeginRelease())
                return false;

            try
            {
                bool released = await routingStore.ReleaseMatchOwnerAsync(registration.Owner);
                RemoveOwnedMatchRegistration(registration);
                return released;
            }
            catch
            {
                registration.MarkReleasePending();
                throw;
            }
        }
        finally
        {
            _matchStoreGate.Release();
        }
    }

    public async Task<bool> AbandonMatchOwnerAsync(long matchingId)
    {
        if (!options.Enabled || matchingId <= 0)
            return false;

        await _matchStoreGate.WaitAsync();
        try
        {
            if (!_ownedMatches.TryGetValue(matchingId, out OwnedMatchRegistration? registration))
                return false;
            if (!registration.TryAbandon())
                return false;

            // Exact registration removal is intentionally local-only. The Redis owner key is
            // left untouched so its TTL, rather than this unhealthy process, hands recovery
            // authority back to the UserServer.
            bool removed = RemoveOwnedMatchRegistration(registration);
            await ClampAbandonedMatchOwnerAsync(registration);
            return removed;
        }
        finally
        {
            _matchStoreGate.Release();
        }
    }

    public async Task QuiesceHeartbeatsAsync()
    {
        if (!options.Enabled)
            return;

        await _lifecycleGate.WaitAsync();
        try
        {
            CancellationTokenSource? heartbeatCts = Interlocked.Exchange(ref _heartbeatCts, null);
            Task? nodeHeartbeatTask = Interlocked.Exchange(ref _nodeHeartbeatTask, null);
            Task? matchOwnerHeartbeatTask = Interlocked.Exchange(ref _matchOwnerHeartbeatTask, null);
            if (heartbeatCts == null)
                return;

            heartbeatCts.Cancel();
            try
            {
                Task[] heartbeatTasks = new Task?[] { nodeHeartbeatTask, matchOwnerHeartbeatTask }
                    .Where(task => task != null)
                    .Cast<Task>()
                    .ToArray();
                if (heartbeatTasks.Length > 0)
                    await Task.WhenAll(heartbeatTasks);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                heartbeatCts.Dispose();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        if (!options.Enabled)
            return;

        await QuiesceHeartbeatsAsync();
        await _lifecycleGate.WaitAsync();
        try
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
                return;

            await ReleaseTrackedOwnersAndNodeAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task ReleaseTrackedOwnersAndNodeAsync()
    {
        await _matchStoreGate.WaitAsync();
        try
        {
            foreach (OwnedMatchRegistration registration in _ownedMatches.Values)
            {
                try
                {
                    await routingStore.ReleaseMatchOwnerAsync(registration.Owner);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Failed to release match owner during node shutdown: MatchingId={MatchingId}, Fence={Fence}",
                        registration.Owner.MatchingId,
                        registration.Owner.Fence);
                }
            }
            _ownedMatches.Clear();
        }
        finally
        {
            _matchStoreGate.Release();
        }

        await _nodeStoreGate.WaitAsync();
        try
        {
            await routingStore.ReleaseNodeLeaseAsync(Identity);
            Volatile.Write(ref _leaseHeld, 0);
        }
        finally
        {
            _nodeStoreGate.Release();
        }
    }

    private async Task NodeHeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(options.HeartbeatInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                await _nodeStoreGate.WaitAsync(cancellationToken);
                try
                {
                    bool renewed = await routingStore.TryHeartbeatNodeAsync(
                        CreateDescriptor(_status),
                        options.NodeLeaseLifetime);
                    if (!renewed)
                    {
                        SignalLeaseLost();
                        return;
                    }

                    Volatile.Write(ref _lastSuccessfulHeartbeatTimestamp, Stopwatch.GetTimestamp());
                }
                finally
                {
                    _nodeStoreGate.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "GameServer routing heartbeat failed: NodeId={NodeId}, Generation={Generation}",
                    Identity.NodeId,
                    Identity.Generation);
                long lastSuccess = Volatile.Read(ref _lastSuccessfulHeartbeatTimestamp);
                if (lastSuccess == 0 ||
                    Stopwatch.GetElapsedTime(lastSuccess) >= options.NodeLeaseLifetime)
                {
                    SignalLeaseLost();
                    return;
                }
            }
        }
    }

    private async Task MatchOwnerHeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(options.HeartbeatInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            OwnedMatchRegistration[] registrations = _ownedMatches.Values.ToArray();
            if (registrations.Length == 0)
                continue;

            var lostRegistrations = new ConcurrentQueue<OwnedMatchRegistration>();
            await Parallel.ForEachAsync(
                registrations,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = MatchOwnerRenewalConcurrency
                },
                async (registration, token) =>
                {
                    if (registration.TryBeginPendingReleaseRetry())
                    {
                        try
                        {
                            await routingStore.ReleaseMatchOwnerAsync(registration.Owner);
                            RemoveOwnedMatchRegistration(registration);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            registration.MarkReleasePending();
                            throw;
                        }
                        catch (Exception ex)
                        {
                            registration.MarkReleasePending();
                            logger.LogWarning(
                                ex,
                                "GameServer match-owner release retry failed: MatchingId={MatchingId}, Fence={Fence}",
                                registration.Owner.MatchingId,
                                registration.Owner.Fence);
                        }
                        return;
                    }

                    if (!registration.IsActive)
                        return;

                    bool ownerRenewed;
                    try
                    {
                        ownerRenewed = await routingStore.RenewMatchOwnerAsync(
                            registration.Owner,
                            options.ActiveOwnerLifetime);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        if (registration.IsAbandoned)
                            await ClampAbandonedMatchOwnerAsync(registration);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(
                            ex,
                            "GameServer match-owner heartbeat failed transiently: MatchingId={MatchingId}, Fence={Fence}",
                            registration.Owner.MatchingId,
                            registration.Owner.Fence);
                        if (registration.IsAbandoned)
                            await ClampAbandonedMatchOwnerAsync(registration);
                        return;
                    }

                    if (registration.IsAbandoned)
                    {
                        // Abandon may race an already in-flight renew. Clamp again after that
                        // renew returns so it cannot leave a fresh active-owner lifetime behind.
                        await ClampAbandonedMatchOwnerAsync(registration);
                        return;
                    }
                    if (ownerRenewed || !registration.IsActive)
                        return;

                    if (RemoveOwnedMatchRegistration(registration))
                        lostRegistrations.Enqueue(registration);
                });

            while (lostRegistrations.TryDequeue(out OwnedMatchRegistration? registration))
                SignalMatchOwnerLost(registration);
        }
    }

    private bool RemoveOwnedMatchRegistration(OwnedMatchRegistration registration)
    {
        var item = new KeyValuePair<long, OwnedMatchRegistration>(
            registration.Owner.MatchingId,
            registration);
        return ((ICollection<KeyValuePair<long, OwnedMatchRegistration>>)_ownedMatches).Remove(item);
    }

    private async Task ClampAbandonedMatchOwnerAsync(OwnedMatchRegistration registration)
    {
        try
        {
            bool clamped = await routingStore.ClampMatchOwnerLifetimeAsync(
                registration.Owner,
                options.NodeLeaseLifetime);
            if (!clamped)
            {
                logger.LogDebug(
                    "Abandoned match owner was already changed or expired before TTL clamp: MatchingId={MatchingId}, Fence={Fence}",
                    registration.Owner.MatchingId,
                    registration.Owner.Fence);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to clamp abandoned match owner TTL; its existing Redis TTL remains the recovery fallback: MatchingId={MatchingId}, Fence={Fence}",
                registration.Owner.MatchingId,
                registration.Owner.Fence);
        }
    }

    private GameServerNodeDescriptor CreateDescriptor(GameServerNodeStatus status) => new()
    {
        NodeId = Identity.NodeId,
        Generation = Identity.Generation,
        PublicHost = options.PublicHost,
        PublicPort = options.PublicPort,
        MaxConcurrentMatches = options.MaxConcurrentMatches,
        ActiveMatchCount = Math.Max(0, _activeMatchCount()),
        Status = status,
        HeartbeatUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    };

    private void SignalLeaseLost()
    {
        Volatile.Write(ref _leaseHeld, 0);
        if (Interlocked.Exchange(ref _leaseLostSignaled, 1) != 0)
            return;

        logger.LogCritical(
            "GameServer routing lease lost; process will stop accepting traffic: NodeId={NodeId}, Generation={Generation}",
            Identity.NodeId,
            Identity.Generation);
        _leaseLost();
    }

    private void SignalMatchOwnerLost(OwnedMatchRegistration registration)
    {
        logger.LogError(
            "GameServer match owner fence was lost; aborting only the affected match: MatchingId={MatchingId}, Fence={Fence}",
            registration.Owner.MatchingId,
            registration.Owner.Fence);
        try
        {
            _matchOwnerLost(registration.Owner.MatchingId, registration.HumanPlayerIds);
        }
        catch (Exception ex)
        {
            logger.LogCritical(
                ex,
                "Failed to abort a match after its owner fence was lost: MatchingId={MatchingId}, Fence={Fence}",
                registration.Owner.MatchingId,
                registration.Owner.Fence);
        }
    }

    private sealed class OwnedMatchRegistration(
        GameServerMatchOwner owner,
        IReadOnlyList<long> humanPlayerIds)
    {
        private const int Active = 0;
        private const int Releasing = 1;
        private const int ReleasePending = 2;
        private const int Abandoned = 3;
        private int _releaseState;

        public GameServerMatchOwner Owner { get; } = owner;
        public IReadOnlyList<long> HumanPlayerIds { get; } = humanPlayerIds;
        public bool IsActive => Volatile.Read(ref _releaseState) == Active;
        public bool IsAbandoned => Volatile.Read(ref _releaseState) == Abandoned;

        public bool TryBeginRelease()
        {
            if (Interlocked.CompareExchange(ref _releaseState, Releasing, Active) == Active)
                return true;
            return Interlocked.CompareExchange(ref _releaseState, Releasing, ReleasePending) == ReleasePending;
        }

        public bool TryBeginPendingReleaseRetry() =>
            Interlocked.CompareExchange(ref _releaseState, Releasing, ReleasePending) == ReleasePending;

        public bool TryAbandon() =>
            Interlocked.CompareExchange(ref _releaseState, Abandoned, Active) == Active;

        public void MarkReleasePending() => Volatile.Write(ref _releaseState, ReleasePending);
    }
}
