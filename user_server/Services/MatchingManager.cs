using System.Collections.Concurrent;
using System.Globalization;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.contracts.authentication;
using network.contracts.scaling;
using network.helpers;
using network.infrastructure;
using network.interfaces;
using network.packets;
using user_server.network;
using user_server.services.scaling;

namespace user_server.services;

internal static class MatchingConfigRedisKeys
{
    internal const string Key = "matching_config";
    internal const string JobPoolField = "job_pool";
}

public class MatchingManager : IMatchingManager
{
    private const string MatchingQueueKey = "matching_queue";
    private const string MatchingIdKey = "matching_id";
    private const string LeavePenaltyKey = "leave_penalties";
    private const string LeavePenaltyDecayAtKey = "leave_penalty_decay_at";
    private const string MatchingQueueLockKeyPrefix = "matching_queue_lock:";
    private const int MatchingTimeoutSeconds = 3;
    private const int BotFillTimeoutSeconds = 30;
    private const int LeavePenaltySeconds = 30;
    private const int MaxLeavePenaltySeconds = 300;
    private const int PenaltyDecayIntervalHours = 24;
    private const int DefaultPlayersPerMatch = 1;
    private const int DefaultGamePlayersPerMatch = 8;
    private const int AdmissionRecoveryScanBatchSize = 32;
    private static readonly TimeSpan MatchingClaimReservationLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ActiveMatchingClaimLifetime = MatchingHandoffRedisKeys.AdmissionClaimLifetime;
    private static readonly TimeSpan GameServerOwnerLossGrace = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MatchingLifecycleMarkerLifetime = TimeSpan.FromDays(8);

    private static int PlayersPerMatch => IsSoloMapValidation ? 1 : IsTwoPlayerTestMatch ? 2 : DefaultPlayersPerMatch;
    private static int GamePlayersPerMatch => IsSoloMapValidation
        ? DefaultPlayersPerMatch
        : Config.SWARM_PLAYERS_PER_MATCH;

    private static bool IsTwoPlayerTestMatch => Environment.GetEnvironmentVariable("TEST_TWO_PLAYER_MATCH") == "1";
    private static bool IsSoloMapValidation =>
        Environment.GetEnvironmentVariable("SOLO_MAP_VALIDATION") == "1";
    private static JobTitle? ForcedPlayerJob => ParseForcedPlayerJob();

    private static long _botIdCounter; // Negative PlayerIds are reserved for bots.
    private readonly ICacheHelper _cacheHelper;
    private readonly ConcurrentDictionary<long, Task> _backgroundTasks = new();
    private readonly object _backgroundTaskLock = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Func<long, GameSession?> _getSession;
    private readonly IGameHandoffTicketService _gameHandoffTicketService;
    private readonly MatchingManagerScalingContext? _scalingContext;
    private readonly bool _writeLegacySpawnFields;
    private readonly ILogger _logger;
    private readonly Task _matchingLeaderHeartbeatTask;
    private readonly Timer _matchingTimer;
    private readonly object _processingTaskLock = new();
    private readonly IRedLockFactory _redLock;
    private int _isProcessing;
    private int _quiescing;
    private MatchingLeaderLease? _matchingLeaderLease;
    private long _nextBackgroundTaskId;
    private Task _processingTask = Task.CompletedTask;
    private readonly object _stopTaskLock = new();
    private Task? _stopTask;
    private int _stopping;

    public MatchingManager(ILogger logger, ICacheHelper cacheHelper, IRedLockFactory redLock,
        IGameHandoffTicketService gameHandoffTicketService,
        Func<long, GameSession?> getSession,
        bool writeLegacySpawnFields,
        MatchingManagerScalingContext? scalingContext = null)
    {
        _logger = logger;
        _cacheHelper = cacheHelper;
        _redLock = redLock;
        _gameHandoffTicketService = gameHandoffTicketService;
        _getSession = getSession;
        _writeLegacySpawnFields = writeLegacySpawnFields;
        _scalingContext = scalingContext;

        // 매칭 대기열을 1초마다 확인한다.
        _matchingTimer = new Timer(OnMatchingTimerTick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _matchingLeaderHeartbeatTask = IsUserServerScalingEnabled
            ? RunMatchingLeaderHeartbeatAsync()
            : Task.CompletedTask;
        _logger.LogInformation(
            "MatchingManager initialized: UserServerScaling={UserServerScaling}, GameServerRouting={GameServerRouting}",
            IsUserServerScalingEnabled,
            IsGameServerRoutingEnabled);
        if (IsAdmissionRecoveryEnabled)
        {
            _logger.LogInformation(
                "Matching admission recovery is persisted in Redis and scanned by the current matching leader");
        }
        else if (IsGameServerRoutingEnabled)
        {
            _logger.LogWarning(
                "Matching admission watchdog state is process-local; Redis admission and claim TTLs are the fail-safe after a UserServer restart");
        }
    }

    private bool IsUserServerScalingEnabled => _scalingContext?.UserServerScalingEnabled == true;
    private bool IsGameServerRoutingEnabled => _scalingContext?.GameServerRoutingEnabled == true;
    private bool IsAdmissionRecoveryEnabled =>
        IsUserServerScalingEnabled || IsGameServerRoutingEnabled;

    private Task<DateTimeOffset> GetMatchingTimeAsync()
    {
        return IsAdmissionRecoveryEnabled
            ? _scalingContext!.CoordinationStore.GetRedisTimeAsync()
            : Task.FromResult(DateTimeOffset.UtcNow);
    }

    public async Task<ErrorCode> AddToQueue(long playerId, GameSession user)
    {
        try
        {
            await using var queueLock = await _redLock.AcquireLockAsync(
                MakeMatchingQueueLockKey(playerId),
                Config.LOCK_TTL);
            if (await HasMatchingClaimAsync(playerId))
                return ErrorCode.MATCHING_ALREADY_IN_QUEUE;

            int removedCount = await RemovePlayerEntriesFromQueueAsync(playerId);
            if (removedCount > 0)
                _logger.LogInformation("Player {PlayerId}: removed {Count} stale matching entries", playerId, removedCount);

            // A worker may have claimed the snapshot that was removed above.
            if (await HasMatchingClaimAsync(playerId))
                return ErrorCode.MATCHING_ALREADY_IN_QUEUE;

            string? requestId = user.ActiveMatchingRequestId;
            if (!UserServerClusterOptions.IsSafeTokenComponent(requestId))
            {
                _logger.LogWarning(
                    "Matching queue rejected because the session has no active request fence: PlayerId={PlayerId}",
                    playerId);
                return ErrorCode.MATCHING_FAILED;
            }

            UserSessionOwner? sessionOwner = null;
            if (IsUserServerScalingEnabled)
            {
                sessionOwner = user.SessionOwner;
                if (sessionOwner is not { IsValid: true } || sessionOwner.PlayerId != playerId)
                {
                    _logger.LogWarning(
                        "Matching queue rejected because the session has no valid owner route: PlayerId={PlayerId}",
                        playerId);
                    return ErrorCode.MATCHING_FAILED;
                }

                UserSessionOwner? currentOwner = await _scalingContext!.CoordinationStore
                    .GetSessionOwnerAsync(playerId);
                if (currentOwner != sessionOwner)
                {
                    _logger.LogWarning(
                        "Matching queue rejected because session ownership changed: PlayerId={PlayerId}",
                        playerId);
                    return ErrorCode.MATCHING_FAILED;
                }
            }

            DateTimeOffset matchingNow = await GetMatchingTimeAsync();
            var queueData = new MatchingQueueData
            {
                PlayerId = playerId,
                RequestTime = matchingNow.UtcDateTime,
                UserChannel = user.GetChannelName(),
                OwnerNodeId = sessionOwner?.NodeId ?? string.Empty,
                OwnerNodeGeneration = sessionOwner?.NodeGeneration ?? string.Empty,
                OwnerSessionId = sessionOwner?.SessionId ?? string.Empty,
                OwnerSessionGeneration = sessionOwner?.SessionGeneration ?? 0,
                RequestId = requestId!
            };

            byte[] serialized = MessagePackSerializer.Serialize(queueData);

            // Apply a queue delay based on the player's accumulated leave count.
            long penaltyDelay = await GetLeavePenaltyDelayAsync(playerId);
            long score = matchingNow.ToUnixTimeSeconds() + penaltyDelay;

            await _cacheHelper.SortedSetAddAsync(MatchingQueueKey, serialized, score);

            if (penaltyDelay > 0)
                _logger.LogInformation("Player {PlayerId} queued with a {Penalty}s leave penalty", playerId, penaltyDelay);
            else
                _logger.LogInformation("Player {PlayerId} queued for matching", playerId);

            return ErrorCode.SUCCESS;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue player for matching: PlayerId={PlayerId}", playerId);
            return ErrorCode.SERVER_INTERNAL_ERROR;
        }
    }

    public async Task<ErrorCode> CancelMatching(long playerId)
    {
        try
        {
            await using var queueLock = await _redLock.AcquireLockAsync(
                MakeMatchingQueueLockKey(playerId),
                Config.LOCK_TTL);
            string cancellationClaimId = "cancel_" + Guid.NewGuid().ToString("N");
            bool cancellationClaimAcquired = await _cacheHelper.StringSetIfNotExistsAsync(
                MakeMatchingClaimKey(playerId),
                cancellationClaimId,
                MatchingClaimReservationLifetime);
            if (!cancellationClaimAcquired)
                return ErrorCode.MATCHING_FAILED;

            try
            {
                int removedCount = await RemovePlayerEntriesFromQueueAsync(playerId);
                _logger.LogInformation(
                    "Matching cancelled: PlayerId={PlayerId}, RemovedEntries={RemovedEntries}",
                    playerId,
                    removedCount);
                return ErrorCode.SUCCESS;
            }
            finally
            {
                await _cacheHelper.StringDeleteIfEqualsAsync(
                    MakeMatchingClaimKey(playerId),
                    cancellationClaimId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel matching: PlayerId={PlayerId}", playerId);
            return ErrorCode.SERVER_INTERNAL_ERROR;
        }
    }

    private async Task<int> RemovePlayerEntriesFromQueueAsync(long playerId)
    {
        byte[][] allEntries = await _cacheHelper.SortedSetRangeByScoreAsync(MatchingQueueKey);
        int removedCount = 0;

        foreach (byte[] entry in allEntries)
        {
            try
            {
                var data = MessagePackSerializer.Deserialize<MatchingQueueData>(entry);
                if (data.PlayerId != playerId) continue;

                if (await _cacheHelper.SortedSetRemoveAsync(MatchingQueueKey, entry))
                    removedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Invalid matching entry removed while cleaning the queue");
                if (await _cacheHelper.SortedSetRemoveAsync(MatchingQueueKey, entry))
                    removedCount++;
            }
        }

        return removedCount;
    }

    private async Task<byte[][]> SanitizeMatchingEntriesAsync(IEnumerable<byte[]> entries)
    {
        var validEntries = new List<byte[]>();
        var seenPlayerIds = new HashSet<long>();

        foreach (byte[] entry in entries)
        {
            bool removeEntry = false;
            try
            {
                var data = MessagePackSerializer.Deserialize<MatchingQueueData>(entry);
                bool invalidRoute = IsUserServerScalingEnabled && !HasValidDeliveryRoute(data);
                removeEntry = data.PlayerId <= 0 || invalidRoute || !seenPlayerIds.Add(data.PlayerId);
                if (!removeEntry)
                {
                    validEntries.Add(entry);
                    continue;
                }

                _logger.LogWarning(
                    "Removed duplicate or invalid matching entry: PlayerId={PlayerId}, InvalidRoute={InvalidRoute}",
                    data.PlayerId,
                    invalidRoute);
            }
            catch (Exception ex)
            {
                removeEntry = true;
                _logger.LogWarning(ex, "Removed malformed matching queue entry");
            }

            if (removeEntry)
                await _cacheHelper.SortedSetRemoveAsync(MatchingQueueKey, entry);
        }

        return validEntries.ToArray();
    }

    private static bool HasValidDeliveryRoute(MatchingQueueData data)
    {
        return data.PlayerId > 0 &&
               UserServerClusterOptions.IsSafeNodeId(data.OwnerNodeId) &&
               UserServerClusterOptions.IsSafeTokenComponent(data.OwnerNodeGeneration) &&
               UserServerClusterOptions.IsSafeTokenComponent(data.OwnerSessionId) &&
               data.OwnerSessionGeneration > 0 &&
               UserServerClusterOptions.IsSafeTokenComponent(data.RequestId);
    }

    private static string MakeMatchingDeliveryId(
        MatchingDeliveryKind kind,
        long matchingId,
        long playerId,
        string requestId)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)kind}:{matchingId}:{playerId}:{requestId}");
    }

    private async Task<MatchingClaimLease?> TryAcquireMatchingClaimsAsync(IEnumerable<byte[]> entries)
    {
        var claimedEntries = entries
            .Select(entry => (
                Entry: entry,
                PlayerId: MessagePackSerializer.Deserialize<MatchingQueueData>(entry).PlayerId))
            .Where(item => item.PlayerId > 0)
            .DistinctBy(item => item.PlayerId)
            .ToList();
        string claimId = Guid.NewGuid().ToString("N");
        var acquiredPlayerIds = new List<long>(claimedEntries.Count);

        try
        {
            foreach (var claimedEntry in claimedEntries)
            {
                bool acquired = await _cacheHelper.TryClaimSortedSetEntryAsync(
                    MatchingQueueKey,
                    claimedEntry.Entry,
                    MakeMatchingClaimKey(claimedEntry.PlayerId),
                    claimId,
                    MatchingClaimReservationLifetime);
                if (!acquired)
                {
                    await RollbackMatchingClaimsAsync(new MatchingClaimLease(claimId, acquiredPlayerIds));
                    return null;
                }

                acquiredPlayerIds.Add(claimedEntry.PlayerId);
            }

            return new MatchingClaimLease(claimId, acquiredPlayerIds);
        }
        catch
        {
            await RollbackMatchingClaimsAsync(new MatchingClaimLease(claimId, acquiredPlayerIds));
            throw;
        }
    }

    private async Task RollbackMatchingClaimsAsync(MatchingClaimLease claimLease)
    {
        foreach (long playerId in claimLease.PlayerIds)
        {
            try
            {
                await _cacheHelper.StringDeleteIfEqualsAsync(
                    MakeMatchingClaimKey(playerId),
                    claimLease.ClaimId);
                if (claimLease.MatchingId.HasValue)
                {
                    await _cacheHelper.StringDeleteIfEqualsAsync(
                        MakeMatchingClaimKey(playerId),
                        claimLease.MatchingId.Value.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Matching claim rollback failed; TTL will release it: PlayerId={PlayerId}",
                    playerId);
            }
        }
    }

    private static string MakeMatchingClaimKey(long playerId)
    {
        return MatchingHandoffRedisKeys.ClaimKey(playerId);
    }

    private static string MakeMatchingQueueLockKey(long playerId)
    {
        return MatchingQueueLockKeyPrefix + playerId;
    }

    private async Task<bool> HasMatchingClaimAsync(long playerId)
    {
        var claim = await _cacheHelper.StringGetAsync(MakeMatchingClaimKey(playerId));
        return !claim.IsNullOrEmpty;
    }

    private async Task CommitMatchingClaimsAsync(MatchingClaimLease claimLease, long matchingId)
    {
        string matchingIdValue = matchingId.ToString(CultureInfo.InvariantCulture);
        claimLease.MatchingId = matchingId;

        foreach (long playerId in claimLease.PlayerIds)
        {
            bool committed = await _cacheHelper.StringSetIfEqualsAsync(
                MakeMatchingClaimKey(playerId),
                claimLease.ClaimId,
                matchingIdValue,
                ActiveMatchingClaimLifetime);
            if (!committed)
            {
                throw new InvalidOperationException(
                    $"Matching claim ownership changed before commit for player {playerId}.");
            }
        }
    }

    /// <summary>
    ///     Starts one asynchronous matching pass and prevents overlapping timer callbacks.
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
            if (!await TryEnterMatchingPassAsync())
                return;
            if (IsAdmissionRecoveryEnabled)
            {
                await RecoverExpiredMatchingAdmissionsAsync();
                if (!await RenewMatchingLeadershipAsync())
                    return;
            }
            await ProcessMatchingQueueAsync();
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

    private async Task<bool> TryEnterMatchingPassAsync()
    {
        if (!IsUserServerScalingEnabled)
            return true;

        MatchingLeaderLease? currentLease = Volatile.Read(ref _matchingLeaderLease);
        if (currentLease != null &&
            await _scalingContext!.CoordinationStore.RenewMatchingLeaderAsync(
                currentLease,
                _scalingContext.UserServerOptions.MatchingLeaderLeaseLifetime))
        {
            return true;
        }

        if (currentLease != null)
            Interlocked.CompareExchange(ref _matchingLeaderLease, null, currentLease);
        MatchingLeaderLease? acquiredLease = await _scalingContext!.CoordinationStore
            .TryAcquireMatchingLeaderAsync(
                _scalingContext.Identity,
                _scalingContext.UserServerOptions.MatchingLeaderLeaseLifetime);
        if (acquiredLease == null)
            return false;

        Volatile.Write(ref _matchingLeaderLease, acquiredLease);
        _logger.LogInformation(
            "Matching leader acquired: NodeId={NodeId}, Generation={Generation}, Fence={Fence}",
            acquiredLease.NodeId,
            acquiredLease.NodeGeneration,
            acquiredLease.Fence);
        return true;
    }

    private async Task<bool> RenewMatchingLeadershipAsync()
    {
        if (!IsUserServerScalingEnabled)
            return true;

        MatchingLeaderLease? lease = Volatile.Read(ref _matchingLeaderLease);
        if (lease == null)
            return false;

        bool renewed = await _scalingContext!.CoordinationStore.RenewMatchingLeaderAsync(
            lease,
            _scalingContext.UserServerOptions.MatchingLeaderLeaseLifetime);
        if (renewed)
            return true;

        Interlocked.CompareExchange(ref _matchingLeaderLease, null, lease);
        _logger.LogWarning(
            "Matching leader lease was lost; the current queue pass will stop: Fence={Fence}",
            lease.Fence);
        return false;
    }

    private async Task RunMatchingLeaderHeartbeatAsync()
    {
        using var timer = new PeriodicTimer(
            _scalingContext!.UserServerOptions.MatchingLeaderHeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdownCts.Token))
            {
                MatchingLeaderLease? lease = Volatile.Read(ref _matchingLeaderLease);
                if (lease == null)
                    continue;

                bool renewed;
                try
                {
                    renewed = await _scalingContext.CoordinationStore.RenewMatchingLeaderAsync(
                        lease,
                        _scalingContext.UserServerOptions.MatchingLeaderLeaseLifetime);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Matching leader heartbeat renewal failed transiently: Fence={Fence}",
                        lease.Fence);
                    continue;
                }

                if (renewed)
                    continue;

                Interlocked.CompareExchange(ref _matchingLeaderLease, null, lease);
                _logger.LogWarning(
                    "Matching leader heartbeat lost the exact lease: Fence={Fence}",
                    lease.Fence);
            }
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
            // Expected during orderly shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Matching leader heartbeat stopped unexpectedly");
        }
    }

    private async Task RecoverExpiredMatchingAdmissionsAsync()
    {
        DateTimeOffset now = await GetMatchingTimeAsync();
        IReadOnlyList<long> matchingIds = await _scalingContext!.CoordinationStore
            .GetExpiredMatchingAdmissionRecoveryIdsAsync(
                now,
                AdmissionRecoveryScanBatchSize);
        foreach (long matchingId in matchingIds)
        {
            if (Volatile.Read(ref _stopping) != 0)
                return;
            if (!await RenewMatchingLeadershipAsync())
                return;

            try
            {
                await RecoverMatchingAdmissionAsync(matchingId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Matching admission recovery attempt failed and remains indexed for retry: MatchingId={MatchingId}",
                    matchingId);
            }
        }
    }

    private async Task RecoverMatchingAdmissionAsync(long matchingId)
    {
        MatchingAdmissionRecoveryRecord? record = await _scalingContext!.CoordinationStore
            .GetMatchingAdmissionRecoveryAsync(matchingId);
        if (record == null)
        {
            await _scalingContext.CoordinationStore.RemoveMatchingAdmissionRecoveryAsync(matchingId);
            return;
        }

        DateTimeOffset now = await GetMatchingTimeAsync();
        if (record.DeadlineUnixMilliseconds > now.ToUnixTimeMilliseconds())
            return;

        if (record.AdmissionCompleted)
        {
            await MonitorCompletedMatchingAsync(record, now);
            return;
        }

        var admissionState = await _cacheHelper.StringGetAsync(
            MatchingHandoffRedisKeys.AdmissionStateKey(matchingId));
        if (!admissionState.IsNullOrEmpty &&
            string.Equals(
                admissionState.ToString(),
                MatchingHandoffRedisKeys.AdmissionCompletedState,
                StringComparison.Ordinal))
        {
            await MonitorCompletedMatchingAsync(record, now);
            return;
        }

        bool rollbackWon = await TryCancelAdmissionForRollbackAsync(matchingId);
        if (!rollbackWon)
        {
            admissionState = await _cacheHelper.StringGetAsync(
                MatchingHandoffRedisKeys.AdmissionStateKey(matchingId));
            if (!admissionState.IsNullOrEmpty &&
                string.Equals(
                    admissionState.ToString(),
                    MatchingHandoffRedisKeys.AdmissionCompletedState,
                    StringComparison.Ordinal))
            {
                await MonitorCompletedMatchingAsync(record, now);
            }

            return;
        }

        _logger.LogWarning(
            "Persistent matching admission deadline expired; resuming exact roster rollback: MatchingId={MatchingId}, Players={PlayerCount}",
            matchingId,
            record.Players.Count);
        await CleanupCanceledMatchingAdmissionAsync(record);
    }

    private async Task MonitorCompletedMatchingAsync(
        MatchingAdmissionRecoveryRecord record,
        DateTimeOffset now)
    {
        if (await AreAllMatchingPlayersTerminalAsync(record))
        {
            await CleanupCanceledMatchingAdmissionAsync(record, notifyPlayersOverride: false);
            return;
        }

        GameServerMatchOwner? owner = record.GetGameServerOwner();
        if (owner == null || !IsGameServerRoutingEnabled)
        {
            _logger.LogWarning(
                "Completed matching recovery cannot monitor a static GameServer owner; removing recovery metadata: MatchingId={MatchingId}",
                record.MatchingId);
            await _scalingContext!.CoordinationStore.RemoveMatchingAdmissionRecoveryAsync(
                record.MatchingId);
            return;
        }

        bool ownerActive = await _scalingContext!.GameServerRoutingStore
            .IsMatchOwnerActiveAsync(owner);
        if (!ownerActive)
        {
            if (record.GameServerOwnerLossObservedUnixMilliseconds == 0)
            {
                MatchingAdmissionRecoveryRecord graceRecord = CopyMatchingAdmissionRecovery(
                    record,
                    now.Add(GameServerOwnerLossGrace),
                    admissionCompleted: true,
                    gameServerOwnerLossObservedUnixMilliseconds: now.ToUnixTimeMilliseconds());
                if (await TryUpdateMatchingAdmissionRecoveryAsync(record, graceRecord))
                {
                    _logger.LogWarning(
                        "Exact GameServer owner disappeared; waiting for durable lifecycle terminals before abort: MatchingId={MatchingId}, GraceSeconds={GraceSeconds}",
                        owner.MatchingId,
                        GameServerOwnerLossGrace.TotalSeconds);
                }
                return;
            }

            _logger.LogWarning(
                "Exact GameServer owner or node lease disappeared; aborting only roster entries without durable terminal evidence: MatchingId={MatchingId}, NodeId={NodeId}, Generation={Generation}, Fence={Fence}",
                owner.MatchingId,
                owner.NodeId,
                owner.Generation,
                owner.Fence);
            if (record.CleanupMode != MatchingAdmissionCleanupMode.AbortAndNotify)
                record = await PromoteMatchingAdmissionCleanupModeAsync(record);
            await CleanupCanceledMatchingAdmissionAsync(
                record,
                protectDurableLifecycleIntent: true);
            return;
        }

        TimeSpan probeInterval = GetGameServerRecoveryProbeInterval();
        MatchingAdmissionRecoveryRecord updatedRecord = CopyMatchingAdmissionRecovery(
            record,
            now.Add(probeInterval),
            admissionCompleted: true);
        bool updated = await TryUpdateMatchingAdmissionRecoveryAsync(record, updatedRecord);
        if (!updated)
        {
            _logger.LogDebug(
                "Completed matching recovery was concurrently advanced or leadership changed: MatchingId={MatchingId}",
                record.MatchingId);
        }
    }

    private async Task<bool> HasMatchingPlayerTerminalAsync(
        MatchingAdmissionRecoveryRecord record,
        MatchingAdmissionRecoveryRoute route)
    {
        var terminal = await _cacheHelper.StringGetAsync(
            UserServerScalingKeys.MatchingLifecycleTerminal(
                route.PlayerId,
                record.MatchingId));
        return !terminal.IsNullOrEmpty;
    }

    private async Task<bool> AreAllMatchingPlayersTerminalAsync(
        MatchingAdmissionRecoveryRecord record)
    {
        foreach (MatchingAdmissionRecoveryRoute route in record.Players)
        {
            if (!await HasMatchingPlayerTerminalAsync(record, route))
                return false;
        }

        return true;
    }

    private TimeSpan GetGameServerRecoveryProbeInterval()
    {
        long intervalTicks = Math.Max(
            TimeSpan.FromSeconds(1).Ticks,
            _scalingContext!.GameServerRoutingOptions.MaximumNodeAge.Ticks / 2);
        return TimeSpan.FromTicks(intervalTicks);
    }

    private async Task<bool> TryUpdateMatchingAdmissionRecoveryAsync(
        MatchingAdmissionRecoveryRecord expectedRecord,
        MatchingAdmissionRecoveryRecord updatedRecord)
    {
        MatchingLeaderLease? leaderLease = IsUserServerScalingEnabled
            ? Volatile.Read(ref _matchingLeaderLease)
            : null;
        if (IsUserServerScalingEnabled && leaderLease == null)
            return false;

        bool updated;
        try
        {
            updated = await _scalingContext!.CoordinationStore.TryUpdateMatchingAdmissionRecoveryAsync(
                leaderLease,
                expectedRecord,
                updatedRecord,
                MatchingHandoffRedisKeys.Lifetime);
        }
        catch (Exception updateError)
        {
            try
            {
                MatchingAdmissionRecoveryRecord? current = await _scalingContext!.CoordinationStore
                    .GetMatchingAdmissionRecoveryAsync(expectedRecord.MatchingId);
                if (current != null && MatchingAdmissionRecoveryRecordsEqual(current, updatedRecord))
                {
                    _logger.LogWarning(
                        updateError,
                        "Admission recovery update response was lost; exact read-back confirmed commit: MatchingId={MatchingId}",
                        expectedRecord.MatchingId);
                    return true;
                }
            }
            catch (Exception readBackError)
            {
                _logger.LogWarning(
                    readBackError,
                    "Admission recovery update read-back failed: MatchingId={MatchingId}",
                    expectedRecord.MatchingId);
            }

            throw;
        }
        if (!updated)
        {
            MatchingAdmissionRecoveryRecord? current = await _scalingContext.CoordinationStore
                .GetMatchingAdmissionRecoveryAsync(expectedRecord.MatchingId);
            if (current != null && MatchingAdmissionRecoveryRecordsEqual(current, updatedRecord))
                return true;
        }

        if (!updated && leaderLease != null)
        {
            MatchingLeaderLease? currentLease = Volatile.Read(ref _matchingLeaderLease);
            if (ReferenceEquals(currentLease, leaderLease) &&
                !await _scalingContext.CoordinationStore.RenewMatchingLeaderAsync(
                    leaderLease,
                    _scalingContext.UserServerOptions.MatchingLeaderLeaseLifetime))
            {
                Interlocked.CompareExchange(ref _matchingLeaderLease, null, leaderLease);
            }
        }

        return updated;
    }

    private async Task ProcessMatchingQueueAsync()
    {
        try
        {
            long now = (await GetMatchingTimeAsync()).ToUnixTimeSeconds();
            long cutoffTime = now - MatchingTimeoutSeconds;

            byte[][] allEntries = await _cacheHelper.SortedSetRangeByScoreAsync(
                MatchingQueueKey,
                double.NegativeInfinity,
                cutoffTime
            );

            if (allEntries.Length == 0) return;

            allEntries = await SanitizeMatchingEntriesAsync(allEntries);
            if (allEntries.Length == 0) return;
            allEntries = SortEntriesByRequestTime(allEntries);

            int matchableCount = allEntries.Length / PlayersPerMatch * PlayersPerMatch;
            if (matchableCount < PlayersPerMatch) return;

            byte[][] entriesToMatch = allEntries.Take(matchableCount).ToArray();
            for (int i = 0; i < entriesToMatch.Length; i += PlayersPerMatch)
            {
                if (Volatile.Read(ref _stopping) != 0)
                    return;
                if (!await RenewMatchingLeadershipAsync())
                    return;

                byte[][] groupEntries = entriesToMatch.Skip(i).Take(PlayersPerMatch).ToArray();
                var claimLease = await TryAcquireMatchingClaimsAsync(groupEntries);
                if (claimLease == null)
                {
                    _logger.LogInformation("Matching group skipped because another worker owns a player claim");
                    continue;
                }

                bool matchCommitted = false;
                long matchingId = 0;
                GameServerAllocation? gameServerAllocation = null;
                MatchingAdmissionRecoveryRecord? admissionRecovery = null;
                bool deliveryAttempted = false;
                int deliveredPlayerCount = 0;
                int expectedHumanCount = 0;
                MatchingQueueData[] batchPlayers = groupEntries
                    .Select(entry => MessagePackSerializer.Deserialize<MatchingQueueData>(entry))
                    .Where(data => data.PlayerId > 0)
                    .DistinctBy(data => data.PlayerId)
                    .ToArray();
                try
                {
                    matchingId = await _cacheHelper.StringIncrementAsync(MatchingIdKey);
                    gameServerAllocation = await TryAllocateGameServerAsync(matchingId);
                    if (gameServerAllocation == null)
                    {
                        _logger.LogWarning(
                            "Matching deferred because no healthy GameServer capacity is available: MatchingId={MatchingId}, Players={PlayerCount}",
                            matchingId,
                            batchPlayers.Length);
                        continue;
                    }

                    if (!await RenewMatchingLeadershipAsync())
                        continue;
                    await CommitMatchingClaimsAsync(claimLease, matchingId);
                    if (IsAdmissionRecoveryEnabled)
                    {
                        admissionRecovery = await RegisterMatchingAdmissionRecoveryAsync(
                            matchingId,
                            groupEntries,
                            gameServerAllocation.Owner);
                    }

                    // Fill vacant slots with bots. Solo validation immediately creates a self-contained match.
                    int botsNeeded = Math.Max(0, GamePlayersPerMatch - groupEntries.Length);
                    var allGroupEntries = new List<byte[]>(groupEntries);
                    for (int b = 0; b < botsNeeded; b++)
                    {
                        long botId = Interlocked.Decrement(ref _botIdCounter);
                        var botData = new MatchingQueueData
                        {
                            PlayerId = botId,
                            RequestTime = DateTime.UtcNow,
                            UserChannel = "bot"
                        };
                        allGroupEntries.Add(MessagePackSerializer.Serialize(botData));
                    }

                    _logger.LogInformation("Bot-filled matching: MatchingId={MatchingId}, Real={Real}, Bots={Bot}",
                        matchingId, groupEntries.Length, botsNeeded);

                    // Build a circular Manitto target chain and assign authoritative spawns.
                    var chain = await BuildRosterChain(allGroupEntries.ToArray());
                    ApplySpawnAssignments(matchingId, chain);
                    await ApplyTwoPlayerTestTargetOutfitAsync(chain);
                    var playerRoster = await BuildPlayerRosterAsync(chain);
                    var humanHandoffRoster = BuildHumanHandoffRoster(chain);
                    expectedHumanCount = humanHandoffRoster.Count;

                    // Store bot handoff data for GameServer to load once per match.
                    var botInfoList = new List<BotMatchingInfo>();
                    foreach (var link in chain)
                    {
                        var data = MessagePackSerializer.Deserialize<MatchingQueueData>(link.Entry);
                        if (data.PlayerId >= 0) continue;
                        botInfoList.Add(new BotMatchingInfo
                        {
                            PlayerId = data.PlayerId,
                            TargetPlayerId = link.TargetPlayerId,
                            MyJobTitle = link.MyJobTitle,
                            TargetJobTitle = link.TargetJobTitle,
                            Persona = PersonaType.None,
                            StartArea = link.StartArea,
                            SpawnCell = Cell.Clone(link.SpawnCell),
                            ActiveBuffIds = new List<int>()
                        });
                    }

                    if (botInfoList.Count > 0)
                    {
                        string handoffKey = MatchingHandoffRedisKeys.Key(matchingId);
                        byte[] serialized = MessagePackSerializer.Serialize(botInfoList);
                        await _cacheHelper.HashSetAsync(handoffKey, MatchingHandoffRedisKeys.BotsField, serialized);
                        await _cacheHelper.KeyExpireAsync(handoffKey, MatchingHandoffRedisKeys.Lifetime);
                    }

                    if (admissionRecovery != null)
                    {
                        admissionRecovery = await PromoteMatchingAdmissionCleanupModeAsync(
                            admissionRecovery);
                    }

                    // Notify real players and remove their committed queue entries.
                    foreach (var link in chain)
                    {
                        var data = MessagePackSerializer.Deserialize<MatchingQueueData>(link.Entry);
                        if (data.PlayerId < 0) continue; // Skip bots.

                        bool delivered = false;
                        try
                        {
                            if (!await RenewMatchingLeadershipAsync())
                                break;
                            deliveryAttempted = true;
                            delivered = await ProcessMatchedEntry(link.Entry, matchingId, link.TargetPlayerId,
                                link.TargetJobTitle, link.MyJobTitle, playerRoster, humanHandoffRoster, link.SpawnCell,
                                gameServerAllocation);
                            if (delivered)
                            {
                                deliveredPlayerCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to commit matching entry; removing it from this match");
                        }
                        finally
                        {
                            if (!delivered)
                                await ReleaseMatchingClaimAsync(data.PlayerId, matchingId);
                            await _cacheHelper.SortedSetRemoveAsync(MatchingQueueKey, link.Entry);
                        }
                    }

                    // A handoff roster is an all-human contract. Publish the admission marker only
                    // after every live human accepted its success packet; GameServer refuses tickets
                    // until this marker exists, so a partial delivery cannot create a stuck match.
                    bool deliveryComplete = deliveredPlayerCount == expectedHumanCount;
                    if (deliveryComplete && await RenewMatchingLeadershipAsync())
                    {
                        await MarkMatchingHandoffReadyAsync(matchingId);
                        if (!StartMatchingAdmissionWatchdog(
                                matchingId,
                                batchPlayers,
                                gameServerAllocation.Owner))
                            throw new OperationCanceledException(
                                "Matching admission watchdog could not start during shutdown.");
                        matchCommitted = true;
                    }
                    if (!deliveryComplete)
                    {
                        _logger.LogWarning(
                            "Matching rolled back because delivery was incomplete: MatchingId={MatchingId}, Delivered={Delivered}, Expected={Expected}",
                            matchingId,
                            deliveredPlayerCount,
                            expectedHumanCount);
                    }
                }
                finally
                {
                    if (!matchCommitted)
                    {
                        if (gameServerAllocation == null)
                        {
                            await RollbackMatchingClaimsAsync(claimLease);
                        }
                        else if (admissionRecovery != null)
                        {
                            bool rollbackWon = await TryCancelAdmissionForRollbackAsync(matchingId);
                            if (rollbackWon)
                            {
                                await CleanupCanceledMatchingAdmissionAsync(
                                    admissionRecovery);
                            }
                        }
                        else if (!deliveryAttempted &&
                                 (IsUserServerScalingEnabled || IsGameServerRoutingEnabled))
                        {
                            await DeleteMatchingHandoffBestEffortAsync(matchingId);
                            await RollbackMatchingClaimsAsync(claimLease);
                            await ReleaseGameServerOwnerBestEffortAsync(gameServerAllocation.Owner);
                        }
                        else
                        {
                            bool rollbackWon = await TryCancelAdmissionForRollbackAsync(matchingId);
                            if (rollbackWon)
                            {
                                await DeleteMatchingHandoffBestEffortAsync(matchingId);
                                await NotifyMatchingBatchFailedAsync(batchPlayers, matchingId);
                                if (IsUserServerScalingEnabled || IsGameServerRoutingEnabled)
                                    await RemoveMatchingEntriesBestEffortAsync(groupEntries);
                                await RollbackMatchingClaimsAsync(claimLease);
                                await ReleaseGameServerOwnerBestEffortAsync(gameServerAllocation.Owner);
                            }
                        }
                    }
                }
            }

            // Fill long-waiting groups with bots.
            if (Volatile.Read(ref _stopping) != 0)
                return;
            if (!await RenewMatchingLeadershipAsync())
                return;
            await CheckBotFillAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while processing the matching queue");
        }
    }

    /// <summary>
    ///     Fills a group that has waited at least 30 seconds with bots.
    /// </summary>
    private async Task CheckBotFillAsync()
    {
        if (Volatile.Read(ref _stopping) != 0 || IsTwoPlayerTestMatch || IsSoloMapValidation) return;

        long now = (await GetMatchingTimeAsync()).ToUnixTimeSeconds();
        long botCutoff = now - BotFillTimeoutSeconds;

        byte[][] longWaitEntries = await _cacheHelper.SortedSetRangeByScoreAsync(
            MatchingQueueKey, double.NegativeInfinity, botCutoff);
        longWaitEntries = await SanitizeMatchingEntriesAsync(longWaitEntries);

        if (longWaitEntries.Length < PlayersPerMatch || longWaitEntries.Length >= GamePlayersPerMatch) return;

        var claimLease = await TryAcquireMatchingClaimsAsync(longWaitEntries);
        if (claimLease == null)
        {
            _logger.LogInformation("Bot-fill match skipped because another worker owns a player claim");
            return;
        }

        bool matchCommitted = false;
        long matchingId = 0;
        GameServerAllocation? gameServerAllocation = null;
        MatchingAdmissionRecoveryRecord? admissionRecovery = null;
        bool deliveryAttempted = false;
        int deliveredPlayerCount = 0;
        int expectedHumanCount = 0;
        MatchingQueueData[] batchPlayers = longWaitEntries
            .Select(entry => MessagePackSerializer.Deserialize<MatchingQueueData>(entry))
            .Where(data => data.PlayerId > 0)
            .DistinctBy(data => data.PlayerId)
            .ToArray();
        try
        {
            int botsNeeded = GamePlayersPerMatch - longWaitEntries.Length;
            var allEntries = new List<byte[]>(longWaitEntries);

            // Create bot queue entries.
            for (int i = 0; i < botsNeeded; i++)
            {
                long botId = Interlocked.Decrement(ref _botIdCounter); // -1, -2, ...
                var botData = new MatchingQueueData
                {
                    PlayerId = botId,
                    RequestTime = DateTime.UtcNow,
                    UserChannel = "bot"
                };
                allEntries.Add(MessagePackSerializer.Serialize(botData));
            }

            matchingId = await _cacheHelper.StringIncrementAsync(MatchingIdKey);
            gameServerAllocation = await TryAllocateGameServerAsync(matchingId);
            if (gameServerAllocation == null)
            {
                _logger.LogWarning(
                    "Bot-fill matching deferred because no healthy GameServer capacity is available: MatchingId={MatchingId}, Players={PlayerCount}",
                    matchingId,
                    batchPlayers.Length);
                return;
            }

            if (!await RenewMatchingLeadershipAsync())
                return;
            await CommitMatchingClaimsAsync(claimLease, matchingId);
            if (IsAdmissionRecoveryEnabled)
            {
                admissionRecovery = await RegisterMatchingAdmissionRecoveryAsync(
                    matchingId,
                    longWaitEntries,
                    gameServerAllocation.Owner);
            }
            _logger.LogInformation("Bot-filled matching: MatchingId={MatchingId}, Real={Real}, Bots={Bot}",
                matchingId, longWaitEntries.Length, botsNeeded);

            var chain = await BuildRosterChain(allEntries.ToArray());
            ApplySpawnAssignments(matchingId, chain);
            var playerRoster = await BuildPlayerRosterAsync(chain);
            var humanHandoffRoster = BuildHumanHandoffRoster(chain);
            expectedHumanCount = humanHandoffRoster.Count;

            // Store bot handoff data for GameServer to load once per match.
            var botInfoList = new List<BotMatchingInfo>();
            foreach (var link in chain)
            {
                var data = MessagePackSerializer.Deserialize<MatchingQueueData>(link.Entry);
                if (data.PlayerId >= 0) continue;
                botInfoList.Add(new BotMatchingInfo
                {
                    PlayerId = data.PlayerId,
                    TargetPlayerId = link.TargetPlayerId,
                    MyJobTitle = link.MyJobTitle,
                    TargetJobTitle = link.TargetJobTitle,
                    Persona = PersonaType.None,
                    StartArea = link.StartArea,
                    SpawnCell = Cell.Clone(link.SpawnCell),
                    ActiveBuffIds = new List<int>()
                });
            }

            if (botInfoList.Count > 0)
            {
                string handoffKey = MatchingHandoffRedisKeys.Key(matchingId);
                byte[] serialized = MessagePackSerializer.Serialize(botInfoList);
                await _cacheHelper.HashSetAsync(handoffKey, MatchingHandoffRedisKeys.BotsField, serialized);
                await _cacheHelper.KeyExpireAsync(handoffKey, MatchingHandoffRedisKeys.Lifetime);
            }

            if (admissionRecovery != null)
            {
                admissionRecovery = await PromoteMatchingAdmissionCleanupModeAsync(
                    admissionRecovery);
            }

            // Notify real players of the completed bot-filled match.
            foreach (var link in chain)
            {
                var data = MessagePackSerializer.Deserialize<MatchingQueueData>(link.Entry);
                if (data.PlayerId < 0) continue; // Skip bots.

                bool delivered = false;
                try
                {
                    if (!await RenewMatchingLeadershipAsync())
                        break;
                    deliveryAttempted = true;
                    delivered = await ProcessMatchedEntry(link.Entry, matchingId, link.TargetPlayerId,
                        link.TargetJobTitle, link.MyJobTitle, playerRoster, humanHandoffRoster, link.SpawnCell,
                        gameServerAllocation);
                    if (delivered)
                    {
                        deliveredPlayerCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to commit bot-fill matching entry: PlayerId={PlayerId}", data.PlayerId);
                }
                finally
                {
                    if (!delivered)
                        await ReleaseMatchingClaimAsync(data.PlayerId, matchingId);
                    await _cacheHelper.SortedSetRemoveAsync(MatchingQueueKey, link.Entry);
                }
            }

            bool deliveryComplete = deliveredPlayerCount == expectedHumanCount;
            if (deliveryComplete && await RenewMatchingLeadershipAsync())
            {
                await MarkMatchingHandoffReadyAsync(matchingId);
                if (!StartMatchingAdmissionWatchdog(
                        matchingId,
                        batchPlayers,
                        gameServerAllocation.Owner))
                    throw new OperationCanceledException(
                        "Bot-fill admission watchdog could not start during shutdown.");
                matchCommitted = true;
            }
            if (!deliveryComplete)
            {
                _logger.LogWarning(
                    "Bot-fill matching rolled back because delivery was incomplete: MatchingId={MatchingId}, Delivered={Delivered}, Expected={Expected}",
                    matchingId,
                    deliveredPlayerCount,
                    expectedHumanCount);
            }
        }
        finally
        {
            if (!matchCommitted)
            {
                if (gameServerAllocation == null)
                {
                    await RollbackMatchingClaimsAsync(claimLease);
                }
                else if (admissionRecovery != null)
                {
                    bool rollbackWon = await TryCancelAdmissionForRollbackAsync(matchingId);
                    if (rollbackWon)
                    {
                        await CleanupCanceledMatchingAdmissionAsync(
                            admissionRecovery);
                    }
                }
                else if (!deliveryAttempted &&
                         (IsUserServerScalingEnabled || IsGameServerRoutingEnabled))
                {
                    await DeleteMatchingHandoffBestEffortAsync(matchingId);
                    await RollbackMatchingClaimsAsync(claimLease);
                    await ReleaseGameServerOwnerBestEffortAsync(gameServerAllocation.Owner);
                }
                else
                {
                    bool rollbackWon = await TryCancelAdmissionForRollbackAsync(matchingId);
                    if (rollbackWon)
                    {
                        await DeleteMatchingHandoffBestEffortAsync(matchingId);
                        await NotifyMatchingBatchFailedAsync(batchPlayers, matchingId);
                        if (IsUserServerScalingEnabled || IsGameServerRoutingEnabled)
                            await RemoveMatchingEntriesBestEffortAsync(longWaitEntries);
                        await RollbackMatchingClaimsAsync(claimLease);
                        await ReleaseGameServerOwnerBestEffortAsync(gameServerAllocation.Owner);
                    }
                }
            }
        }
    }

    private async Task<GameServerAllocation?> TryAllocateGameServerAsync(long matchingId)
    {
        if (!IsGameServerRoutingEnabled)
        {
            string gameServerIp = Environment.GetEnvironmentVariable("GAME_SERVER_IP") ?? "127.0.0.1";
            int gameServerPort = int.TryParse(
                Environment.GetEnvironmentVariable("GAME_SERVER_PORT"),
                out int configuredPort)
                ? configuredPort
                : 9001;
            return new GameServerAllocation(gameServerIp, gameServerPort, null);
        }

        if (!await RenewMatchingLeadershipAsync())
            return null;

        MatchingGameServerRoutingOptions routingOptions = _scalingContext!.GameServerRoutingOptions;
        IReadOnlyList<GameServerNodeDescriptor> healthyNodes = await _scalingContext.GameServerRoutingStore
            .DiscoverHealthyNodesAsync(routingOptions.MaximumNodeAge);
        var candidates = new List<(GameServerNodeDescriptor Node, int OwnedMatchCount)>(healthyNodes.Count);
        foreach (GameServerNodeDescriptor node in healthyNodes)
        {
            int ownedMatchCount = await _scalingContext.GameServerRoutingStore.GetOwnedMatchCountAsync(
                new GameServerNodeIdentity(node.NodeId, node.Generation));
            candidates.Add((node, ownedMatchCount));
        }

        foreach (var candidate in candidates
                     .OrderBy(item => (double)item.OwnedMatchCount / item.Node.MaxConcurrentMatches)
                     .ThenBy(item => item.Node.NodeId, StringComparer.Ordinal))
        {
            if (!await RenewMatchingLeadershipAsync())
                return null;

            GameServerNodeDescriptor node = candidate.Node;
            GameServerMatchOwner? owner = await _scalingContext.GameServerRoutingStore.TryReserveMatchAsync(
                matchingId,
                node,
                routingOptions.ReservationLifetime);
            if (owner == null)
                continue;

            _logger.LogInformation(
                "GameServer reserved for match: MatchingId={MatchingId}, NodeId={NodeId}, Generation={Generation}, Fence={Fence}, Endpoint={Host}:{Port}",
                matchingId,
                owner.NodeId,
                owner.Generation,
                owner.Fence,
                node.PublicHost,
                node.PublicPort);
            return new GameServerAllocation(node.PublicHost, node.PublicPort, owner);
        }

        return null;
    }

    private async Task RemoveMatchingEntriesBestEffortAsync(IEnumerable<byte[]> entries)
    {
        foreach (byte[] entry in entries)
        {
            try
            {
                await _cacheHelper.SortedSetRemoveAsync(MatchingQueueKey, entry);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove a rolled-back matching queue entry");
            }
        }
    }

    private async Task ReleaseGameServerOwnerBestEffortAsync(GameServerMatchOwner? owner)
    {
        if (owner == null || !IsGameServerRoutingEnabled)
            return;

        try
        {
            bool released = await _scalingContext!.GameServerRoutingStore.ReleaseMatchOwnerAsync(owner);
            if (!released)
            {
                _logger.LogWarning(
                    "GameServer owner was not released because the exact fence no longer owns the match: MatchingId={MatchingId}, Fence={Fence}",
                    owner.MatchingId,
                    owner.Fence);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "GameServer owner release failed; reservation TTL remains as fallback: MatchingId={MatchingId}, Fence={Fence}",
                owner.MatchingId,
                owner.Fence);
        }
    }

    /// <summary>
    ///     Builds a circular target chain: player i targets player (i+1) modulo N.
    ///     Jobs are shuffled unless Redis supplies an explicit pool.
    ///     Two-player validation uses a deterministic chain instead.
    /// </summary>
    private async Task<List<RosterChainLink>> BuildRosterChain(byte[][] groupEntries)
    {
        if (IsTwoPlayerTestMatch && groupEntries.Length == DefaultGamePlayersPerMatch)
            return BuildTwoPlayerTestRosterChain(groupEntries);


        // Shuffle entries.
        var entries = groupEntries.ToList();
        var rng = Random.Shared;
        for (int i = entries.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (entries[i], entries[j]) = (entries[j], entries[i]);
        }

        // Resolve job pool.
        List<JobTitle> jobs;
        try
        {
            var raw = await _cacheHelper.HashGetAsync(
                MatchingConfigRedisKeys.Key,
                MatchingConfigRedisKeys.JobPoolField);

            if (raw.HasValue)
            {
                var ints = System.Text.Json.JsonSerializer.Deserialize<List<int>>((string)raw!);
                if (ints != null && ints.Count > 0)
                {
                    jobs = ints.Select(v => (JobTitle)v).ToList();
                    _logger.LogInformation("Applied configured matching job pool: {Jobs}", string.Join(",", jobs));
                }
                else
                {
                    jobs = Enum.GetValues<JobTitle>().Where(j => j != JobTitle.NONE).ToList();
                }
            }
            else
            {
                jobs = Enum.GetValues<JobTitle>().Where(j => j != JobTitle.NONE).ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read matching job pool; using the default shuffled pool");
            jobs = Enum.GetValues<JobTitle>().Where(j => j != JobTitle.NONE).ToList();
        }

        // Shuffle the job pool.
        for (int i = jobs.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (jobs[i], jobs[j]) = (jobs[j], jobs[i]);
        }

        // Supplement a short configured pool with unused non-NONE jobs.
        if (jobs.Count < entries.Count)
        {
            var fillPool = Enum.GetValues<JobTitle>()
                .Where(j => j != JobTitle.NONE && !jobs.Contains(j))
                .OrderBy(_ => rng.Next())
                .ToList();
            int needed = entries.Count - jobs.Count;
            jobs.AddRange(fillPool.Take(needed));
            _logger.LogInformation("Supplemented matching job pool with {Needed} jobs", needed);
        }

        // 직업 종수(8) < 정원(10)이면 전 직업을 써도 모자란다 — 순환 중복 배정 (#223 10인 전환).
        if (jobs.Count < entries.Count)
        {
            int baseJobCount = jobs.Count;
            for (int fillIndex = 0; jobs.Count < entries.Count; fillIndex++)
                jobs.Add(jobs[fillIndex % baseJobCount]);
        }

        // Deserialize PlayerIds once before building the chain.
        var players = entries.Select(e => MessagePackSerializer.Deserialize<MatchingQueueData>(e)).ToList();
        ApplyForcedPlayerJob(players, jobs);

        var chain = new List<RosterChainLink>();
        for (int i = 0; i < entries.Count; i++)
        {
            int targetIndex = (i + 1) % entries.Count;
            chain.Add(new RosterChainLink
            {
                Entry = entries[i],
                TargetPlayerId = players[targetIndex].PlayerId,
                MyJobTitle = jobs[i],
                TargetJobTitle = jobs[targetIndex]
            });
        }

        _logger.LogInformation("Manitto chain created: {Chain}",
            string.Join(" ??", players.Select((p, i) => $"{p.PlayerId}({jobs[i]})")) + $" ??{players[0].PlayerId}");

        return chain;
    }

    private static JobTitle? ParseForcedPlayerJob()
    {
        string? raw = Environment.GetEnvironmentVariable("FORCE_PLAYER_JOB");
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (Enum.TryParse(raw, true, out JobTitle byName) && byName != JobTitle.NONE)
            return byName;

        return short.TryParse(raw, out short byValue) && Enum.IsDefined(typeof(JobTitle), byValue)
            ? (JobTitle)byValue
            : null;
    }

    private void ApplySpawnAssignments(long matchingId, List<RosterChainLink> chain)
    {
        if (chain.Count == 0) return;

        var playerIds = chain
            .Select(link => MessagePackSerializer.Deserialize<MatchingQueueData>(link.Entry).PlayerId)
            .ToList();
        // 스웜: 시작방 분산 스폰을 그대로 쓴다.
        IReadOnlyDictionary<long, Cell> assignments =
            MatchSpawnData.CreatePhaseRoomAssignments(matchingId, playerIds);

        // 교차사격 샌드박스 (#232 2단계): DEV_CROSSFIRE_SANDBOX=1 이면 전원 운동장 스폰 —
        // 게임서버가 첫 틱에 봇 하나를 더미로 세우고 나머지를 퇴장시킨다. 방 문이 잠긴 채
        // 시작하는 정식 흐름에서는 사람이 운동장까지 나오는 데 100초가 걸린다.
        bool crossfireSandbox = Environment.GetEnvironmentVariable("DEV_CROSSFIRE_SANDBOX") == "1";
        Cell? sandboxCell = crossfireSandbox
            ? GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, Config.SWARM_MATCH_GROUND_AREA)
            : null;

        foreach (var link in chain)
        {
            var playerId = MessagePackSerializer.Deserialize<MatchingQueueData>(link.Entry).PlayerId;
            link.Persona = PersonaType.None;
            link.SpawnCell = Cell.Clone(sandboxCell ?? assignments[playerId]);
            link.StartArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, link.SpawnCell);

            _logger.LogInformation(
                "Survivor Royale spawn assigned: MatchingId={MatchingId}, PlayerId={PlayerId}, Area={Area}, Cell=({X},{Y})",
                matchingId, playerId, link.StartArea, link.SpawnCell.X, link.SpawnCell.Y);
        }
    }
    private void ApplyForcedPlayerJob(List<MatchingQueueData> players, List<JobTitle> jobs)
    {
        var forcedJob = ForcedPlayerJob;
        if (!forcedJob.HasValue) return;

        int playerIndex = players.FindIndex(player => player.PlayerId >= 0);
        if (playerIndex < 0 || playerIndex >= jobs.Count) return;

        int forcedJobIndex = jobs.IndexOf(forcedJob.Value);
        if (forcedJobIndex >= 0)
            (jobs[playerIndex], jobs[forcedJobIndex]) = (jobs[forcedJobIndex], jobs[playerIndex]);
        else
            jobs[playerIndex] = forcedJob.Value;

        _logger.LogInformation("Applied forced player job: PlayerId={PlayerId}, Job={Job}",
            players[playerIndex].PlayerId, forcedJob.Value);
    }

    private async Task ApplyTwoPlayerTestTargetOutfitAsync(List<RosterChainLink> chain)
    {
        if (!IsTwoPlayerTestMatch) return;

        var targetOutfitItemIds = new[]
        {
            101000005, // Hair
            102000005, // Face accessory
            103000003, // Glasses
            104000007, // Top
            105000007, // Bottom
            106000004  // Shoes
        };

        var realPlayers = chain
            .Select(link => MessagePackSerializer.Deserialize<MatchingQueueData>(link.Entry))
            .Where(data => data.PlayerId >= 0)
            .OrderBy(data => data.RequestTime)
            .ThenBy(data => data.PlayerId)
            .ToList();

        if (realPlayers.Count < 2) return;

        long targetPlayerId = realPlayers[1].PlayerId;

        await using (await PlayerInfo.Lock(_redLock, targetPlayerId))
        {
            var targetPlayer = await PlayerInfo.Load(_cacheHelper, targetPlayerId);
            if (targetPlayer == null)
            {
                _logger.LogWarning("Two-player outfit setup failed: target player load failed ({PlayerId})", targetPlayerId);
                return;
            }

            foreach (var item in targetPlayer.InventoryInfo.ItemDict.Values) item.IsWear = false;

            targetPlayer.WearItemIdList.Clear();
            foreach (int itemId in targetOutfitItemIds)
            {
                var targetItem = targetPlayer.InventoryInfo.ItemDict.Values.FirstOrDefault(item => item.ItemId == itemId);
                if (targetItem == null)
                {
                    long itemUid = await _cacheHelper.StringIncrementAsync("item_uid_counter");
                    targetItem = new ItemInfo(itemUid, itemId, 1);
                    targetPlayer.InventoryInfo.ItemDict.Add(targetItem.ItemUid, targetItem);
                }

                targetItem.IsWear = true;
                targetPlayer.WearItemIdList.Add(itemId);
            }

            await targetPlayer.Save(_cacheHelper);
        }

        _logger.LogInformation("Two-player target outfit fixed: Player2={TargetPlayerId}, Items={Items}",
            targetPlayerId, string.Join(", ", targetOutfitItemIds));
    }

    /// <summary>
    ///     Sorts queue entries by request time and PlayerId for a stable matching order.
    /// </summary>
    private static byte[][] SortEntriesByRequestTime(byte[][] entries)
    {
        return entries
            .Select(entry => new
            {
                Entry = entry,
                Data = MessagePackSerializer.Deserialize<MatchingQueueData>(entry)
            })
            .OrderBy(x => x.Data.RequestTime)
            .ThenBy(x => x.Data.PlayerId)
            .Select(x => x.Entry)
            .ToArray();
    }

    private List<RosterChainLink> BuildTwoPlayerTestRosterChain(byte[][] groupEntries)
    {
        var entries = groupEntries
            .Select(entry => new
            {
                Entry = entry,
                Data = MessagePackSerializer.Deserialize<MatchingQueueData>(entry)
            })
            .ToList();

        var realPlayers = entries
            .Where(x => x.Data.PlayerId >= 0)
            .OrderBy(x => x.Data.RequestTime)
            .ThenBy(x => x.Data.PlayerId)
            .ToList();
        var bots = entries.Where(x => x.Data.PlayerId < 0).ToList();

        const int expectedRealPlayerCount = 2;
        int expectedBotCount = DefaultGamePlayersPerMatch - expectedRealPlayerCount;
        if (realPlayers.Count != expectedRealPlayerCount || bots.Count != expectedBotCount)
        {
            _logger.LogWarning(
                "TEST_TWO_PLAYER_MATCH composition invalid: Real={Real}, Bots={Bot}; using normal-chain validation",
                realPlayers.Count, bots.Count);
            throw new InvalidOperationException("TEST_TWO_PLAYER_MATCH requires two real players and six bots.");
        }

        var ordered = realPlayers.Concat(bots).ToList();
        var players = ordered.Select(x => x.Data).ToList();
        // NONE을 배정하면 그 플레이어는 직책 없이 매치에 들어가 인게임 진입에서 막힌다.
        // 실제 플레이어가 배열 앞에 오므로, 먼저 큐를 잡은 사람이 항상 걸렸다.
        // JobTitle은 NONE을 빼고 정확히 8개라 8인 매치에 그대로 맞는다.
        var jobs = new[]
        {
            JobTitle.STUDENT_PRESIDENT,
            JobTitle.DISCIPLINE_MEMBER,
            JobTitle.BROADCAST_MEMBER,
            JobTitle.SCIENCE_MEMBER,
            JobTitle.HEALTH_MEMBER,
            JobTitle.LIBRARY_COMMITTEE,
            JobTitle.SPORTS_CAPTAIN,
            JobTitle.CLEANING_MEMBER
        };

        var chain = new List<RosterChainLink>();
        for (int i = 0; i < ordered.Count; i++)
        {
            int targetIndex = (i + 1) % ordered.Count;
            chain.Add(new RosterChainLink
            {
                Entry = ordered[i].Entry,
                TargetPlayerId = players[targetIndex].PlayerId,
                MyJobTitle = jobs[i],
                TargetJobTitle = jobs[targetIndex]
            });
        }

        _logger.LogInformation(
            "TEST_TWO_PLAYER_MATCH deterministic chain: {Chain}",
            string.Join(" -> ", players.Select((p, i) => $"{p.PlayerId}({jobs[i]})")) + $" -> {players[0].PlayerId}");

        return chain;
    }

    private async Task<List<PlayerInfo>> BuildPlayerRosterAsync(List<RosterChainLink> chain)
    {
        var roster = new List<PlayerInfo>();

        foreach (var link in chain)
        {
            var data = MessagePackSerializer.Deserialize<MatchingQueueData>(link.Entry);
            if (data.PlayerId < 0)
            {
                roster.Add(CreateBotRosterInfo(data.PlayerId));
                continue;
            }

            var playerInfo = await PlayerInfo.Load(_cacheHelper, data.PlayerId);
            if (playerInfo == null)
            {
                _logger.LogWarning("Matching roster fallback: PlayerInfo load failed ({PlayerId})", data.PlayerId);
                roster.Add(new PlayerInfo
                {
                    PlayerId = data.PlayerId,
                    Name = $"Player{data.PlayerId}",
                    WearItemIdList = new List<int>()
                });
                continue;
            }

            roster.Add(new PlayerInfo
            {
                PlayerId = playerInfo.PlayerId,
                Name = playerInfo.Name,
                WearItemIdList = playerInfo.WearItemIdList != null
                    ? new List<int>(playerInfo.WearItemIdList)
                    : new List<int>()
            });
        }

        return roster;
    }

    private static PlayerInfo CreateBotRosterInfo(long playerId)
    {
        return new PlayerInfo
        {
            PlayerId = playerId,
            Name = $"Player{Math.Abs(playerId)}",
            WearItemIdList = BuildBotRosterWearItems(playerId)
        };
    }

    private static List<int> BuildBotRosterWearItems(long playerId)
    {
        var list = new List<int>
        {
            101000003,
            102000003,
            104000005,
            105000005,
            106000003
        };

        int accessoryId = (Math.Abs((int)playerId) % 4) switch
        {
            0 => 103000001,
            1 => 103000004,
            2 => 103000005,
            _ => 103000006
        };

        list.Add(accessoryId);
        return list;
    }

    private static List<GameHandoffRosterEntry> BuildHumanHandoffRoster(IEnumerable<RosterChainLink> chain)
    {
        return chain
            .Select(link => new
            {
                Link = link,
                Data = MessagePackSerializer.Deserialize<MatchingQueueData>(link.Entry)
            })
            .Where(item => item.Data.PlayerId > 0)
            .Select(item => new GameHandoffRosterEntry
            {
                PlayerId = item.Data.PlayerId,
                TargetPlayerId = item.Link.TargetPlayerId,
                TargetJobTitle = item.Link.TargetJobTitle,
                MyJobTitle = item.Link.MyJobTitle
            })
            .ToList();
    }

    private async Task<MatchingAdmissionRecoveryRecord> RegisterMatchingAdmissionRecoveryAsync(
        long matchingId,
        IReadOnlyCollection<byte[]> queueEntries,
        GameServerMatchOwner? gameServerOwner)
    {
        if (!IsAdmissionRecoveryEnabled)
            throw new InvalidOperationException("Persistent admission recovery requires a scaling mode.");
        if (matchingId <= 0)
            throw new ArgumentOutOfRangeException(nameof(matchingId));
        if (gameServerOwner != null &&
            (!gameServerOwner.IsValid || gameServerOwner.MatchingId != matchingId))
        {
            throw new ArgumentException(
                "GameServer owner does not match the recovery matching id.",
                nameof(gameServerOwner));
        }

        var routes = new List<MatchingAdmissionRecoveryRoute>(queueEntries.Count);
        foreach (byte[] entry in queueEntries)
        {
            MatchingQueueData data = MessagePackSerializer.Deserialize<MatchingQueueData>(entry);
            if (data.PlayerId <= 0)
                continue;
            if (IsUserServerScalingEnabled && !HasValidDeliveryRoute(data))
            {
                throw new InvalidOperationException(
                    $"Matching recovery route is invalid for player {data.PlayerId}.");
            }

            routes.Add(new MatchingAdmissionRecoveryRoute
            {
                PlayerId = data.PlayerId,
                OwnerNodeId = data.OwnerNodeId,
                OwnerNodeGeneration = data.OwnerNodeGeneration,
                OwnerSessionId = data.OwnerSessionId,
                OwnerSessionGeneration = data.OwnerSessionGeneration,
                RequestId = data.RequestId,
                QueueEntry = entry.ToArray()
            });
        }

        if (routes.Count == 0 || routes.Select(route => route.PlayerId).Distinct().Count() != routes.Count)
            throw new InvalidOperationException("Matching recovery requires a unique route for every human player.");

        DateTimeOffset now = await GetMatchingTimeAsync();
        var record = new MatchingAdmissionRecoveryRecord
        {
            MatchingId = matchingId,
            DeadlineUnixMilliseconds = now.Add(MatchingHandoffRedisKeys.AdmissionTimeout)
                .ToUnixTimeMilliseconds(),
            Players = routes,
            GameServerNodeId = gameServerOwner?.NodeId ?? string.Empty,
            GameServerGeneration = gameServerOwner?.Generation ?? string.Empty,
            GameServerFence = gameServerOwner?.Fence ?? 0,
            AdmissionCompleted = false,
            GameServerOwnerLossObservedUnixMilliseconds = 0,
            CleanupMode = MatchingAdmissionCleanupMode.KeepQueue
        };

        MatchingLeaderLease? leaderLease = IsUserServerScalingEnabled
            ? Volatile.Read(ref _matchingLeaderLease)
            : null;
        if (IsUserServerScalingEnabled && leaderLease == null)
            throw new InvalidOperationException("Matching leader lease was lost before recovery registration.");

        try
        {
            bool registered = await _scalingContext!.CoordinationStore
                .TryRegisterMatchingAdmissionRecoveryAsync(
                    leaderLease,
                    record,
                    MatchingHandoffRedisKeys.Lifetime);
            if (!registered)
            {
                Interlocked.CompareExchange(ref _matchingLeaderLease, null, leaderLease);
                throw new InvalidOperationException(
                    "Matching leader lease was lost before recovery registration committed.");
            }
        }
        catch (Exception registrationError)
        {
            try
            {
                MatchingAdmissionRecoveryRecord? existing = await _scalingContext!.CoordinationStore
                    .GetMatchingAdmissionRecoveryAsync(matchingId);
                if (existing != null && MatchingAdmissionRecoveryRecordsEqual(existing, record))
                {
                    _logger.LogWarning(
                        registrationError,
                        "Admission recovery registration response was lost; exact read-back confirmed commit: MatchingId={MatchingId}",
                        matchingId);
                    return existing;
                }
            }
            catch (Exception readBackError)
            {
                _logger.LogWarning(
                    readBackError,
                    "Admission recovery registration read-back failed: MatchingId={MatchingId}",
                    matchingId);
            }

            throw;
        }

        return record;
    }

    private static MatchingAdmissionRecoveryRecord CopyMatchingAdmissionRecovery(
        MatchingAdmissionRecoveryRecord source,
        DateTimeOffset nextDeadline,
        bool admissionCompleted,
        long? gameServerOwnerLossObservedUnixMilliseconds = null,
        MatchingAdmissionCleanupMode? cleanupMode = null)
    {
        return new MatchingAdmissionRecoveryRecord
        {
            MatchingId = source.MatchingId,
            DeadlineUnixMilliseconds = nextDeadline.ToUnixTimeMilliseconds(),
            Players = source.Players,
            GameServerNodeId = source.GameServerNodeId,
            GameServerGeneration = source.GameServerGeneration,
            GameServerFence = source.GameServerFence,
            AdmissionCompleted = admissionCompleted,
            GameServerOwnerLossObservedUnixMilliseconds =
                gameServerOwnerLossObservedUnixMilliseconds ??
                source.GameServerOwnerLossObservedUnixMilliseconds,
            CleanupMode = cleanupMode ?? source.CleanupMode
        };
    }

    private async Task<MatchingAdmissionRecoveryRecord> PromoteMatchingAdmissionCleanupModeAsync(
        MatchingAdmissionRecoveryRecord record)
    {
        if (record.CleanupMode == MatchingAdmissionCleanupMode.AbortAndNotify)
            return record;

        MatchingAdmissionRecoveryRecord updatedRecord = CopyMatchingAdmissionRecovery(
            record,
            DateTimeOffset.FromUnixTimeMilliseconds(record.DeadlineUnixMilliseconds),
            record.AdmissionCompleted,
            cleanupMode: MatchingAdmissionCleanupMode.AbortAndNotify);
        try
        {
            if (await TryUpdateMatchingAdmissionRecoveryAsync(record, updatedRecord))
                return updatedRecord;
        }
        catch (Exception updateError)
        {
            MatchingAdmissionRecoveryRecord? current = await TryReadAbortCleanupModeAsync(
                record.MatchingId,
                updateError);
            if (current != null)
                return current;
            throw;
        }

        MatchingAdmissionRecoveryRecord? reconciled = await TryReadAbortCleanupModeAsync(
            record.MatchingId);
        if (reconciled != null)
            return reconciled;

        throw new InvalidOperationException(
            $"Could not persist abort cleanup mode before delivering match {record.MatchingId}.");
    }

    private async Task<MatchingAdmissionRecoveryRecord?> TryReadAbortCleanupModeAsync(
        long matchingId,
        Exception? updateError = null)
    {
        try
        {
            MatchingAdmissionRecoveryRecord? current = await _scalingContext!.CoordinationStore
                .GetMatchingAdmissionRecoveryAsync(matchingId);
            if (current?.CleanupMode == MatchingAdmissionCleanupMode.AbortAndNotify)
            {
                if (updateError != null)
                {
                    _logger.LogWarning(
                        updateError,
                        "Abort cleanup-mode update response was lost; monotonic read-back confirmed promotion: MatchingId={MatchingId}",
                        matchingId);
                }

                return current;
            }
        }
        catch (Exception readBackError)
        {
            _logger.LogWarning(
                readBackError,
                "Abort cleanup-mode read-back failed: MatchingId={MatchingId}",
                matchingId);
        }

        return null;
    }

    private static bool MatchingAdmissionRecoveryRecordsEqual(
        MatchingAdmissionRecoveryRecord first,
        MatchingAdmissionRecoveryRecord second)
    {
        if (first.MatchingId != second.MatchingId ||
            first.DeadlineUnixMilliseconds != second.DeadlineUnixMilliseconds ||
            first.AdmissionCompleted != second.AdmissionCompleted ||
            first.GameServerOwnerLossObservedUnixMilliseconds !=
            second.GameServerOwnerLossObservedUnixMilliseconds ||
            first.CleanupMode != second.CleanupMode ||
            !string.Equals(first.GameServerNodeId, second.GameServerNodeId, StringComparison.Ordinal) ||
            !string.Equals(first.GameServerGeneration, second.GameServerGeneration, StringComparison.Ordinal) ||
            first.GameServerFence != second.GameServerFence ||
            first.Players.Count != second.Players.Count)
        {
            return false;
        }

        for (int index = 0; index < first.Players.Count; index++)
        {
            MatchingAdmissionRecoveryRoute firstRoute = first.Players[index];
            MatchingAdmissionRecoveryRoute secondRoute = second.Players[index];
            if (firstRoute.PlayerId != secondRoute.PlayerId ||
                !string.Equals(firstRoute.OwnerNodeId, secondRoute.OwnerNodeId, StringComparison.Ordinal) ||
                !string.Equals(firstRoute.OwnerNodeGeneration, secondRoute.OwnerNodeGeneration,
                    StringComparison.Ordinal) ||
                !string.Equals(firstRoute.OwnerSessionId, secondRoute.OwnerSessionId, StringComparison.Ordinal) ||
                firstRoute.OwnerSessionGeneration != secondRoute.OwnerSessionGeneration ||
                !string.Equals(firstRoute.RequestId, secondRoute.RequestId, StringComparison.Ordinal) ||
                !firstRoute.QueueEntry.AsSpan().SequenceEqual(secondRoute.QueueEntry))
            {
                return false;
            }
        }

        return true;
    }

    private async Task MarkMatchingHandoffReadyAsync(long matchingId)
    {
        await EnsureAdmissionStatePendingAsync(matchingId);
        string handoffKey = MatchingHandoffRedisKeys.Key(matchingId);
        Exception? lastError = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await _cacheHelper.HashSetWithExpiryAsync(
                    handoffKey,
                    MatchingHandoffRedisKeys.AdmissionReadyField,
                    [MatchingHandoffRedisKeys.AdmissionReadyValue],
                    MatchingHandoffRedisKeys.Lifetime);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                try
                {
                    var marker = await _cacheHelper.HashGetAsync(
                        handoffKey,
                        MatchingHandoffRedisKeys.AdmissionReadyField);
                    if (!marker.IsNullOrEmpty &&
                        ((byte[])marker!).AsSpan().SequenceEqual([MatchingHandoffRedisKeys.AdmissionReadyValue]))
                    {
                        _logger.LogWarning(
                            ex,
                            "Matching admission marker write response was lost; read-back confirmed commit: MatchingId={MatchingId}",
                            matchingId);
                        return;
                    }
                }
                catch (Exception readBackError)
                {
                    _logger.LogWarning(
                        readBackError,
                        "Matching admission marker read-back failed: MatchingId={MatchingId}, Attempt={Attempt}",
                        matchingId,
                        attempt + 1);
                }
            }
        }

        throw new InvalidOperationException(
            $"Could not confirm the admission marker for match {matchingId}.",
            lastError);
    }

    private async Task EnsureAdmissionStatePendingAsync(long matchingId)
    {
        string stateKey = MatchingHandoffRedisKeys.AdmissionStateKey(matchingId);
        Exception? lastError = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            string? conflictingState = null;
            try
            {
                bool created = await _cacheHelper.StringSetIfNotExistsAsync(
                    stateKey,
                    MatchingHandoffRedisKeys.AdmissionPendingState,
                    MatchingHandoffRedisKeys.Lifetime);
                if (created)
                    return;

                var existing = await _cacheHelper.StringGetAsync(stateKey);
                if (!existing.IsNullOrEmpty &&
                    string.Equals(existing.ToString(), MatchingHandoffRedisKeys.AdmissionPendingState,
                        StringComparison.Ordinal))
                    return;
                conflictingState = existing.ToString();
            }
            catch (Exception ex)
            {
                lastError = ex;
                try
                {
                    var existing = await _cacheHelper.StringGetAsync(stateKey);
                    if (!existing.IsNullOrEmpty &&
                        string.Equals(existing.ToString(), MatchingHandoffRedisKeys.AdmissionPendingState,
                            StringComparison.Ordinal))
                    {
                        _logger.LogWarning(
                            ex,
                            "Admission state creation response was lost; read-back confirmed pending: MatchingId={MatchingId}",
                            matchingId);
                        return;
                    }
                }
                catch (Exception readBackError)
                {
                    _logger.LogWarning(
                        readBackError,
                        "Admission state read-back failed: MatchingId={MatchingId}, Attempt={Attempt}",
                        matchingId,
                        attempt + 1);
                }

                continue;
            }

            // The Redis operations completed successfully, so a non-pending value is a
            // definitive state conflict rather than a transient infrastructure failure.
            throw new InvalidOperationException(
                $"Admission state for match {matchingId} is already '{conflictingState}'.");
        }

        throw new InvalidOperationException(
            $"Could not initialize admission state for match {matchingId}.",
            lastError);
    }

    private async Task<bool> TryCancelAdmissionForRollbackAsync(long matchingId)
    {
        if (matchingId <= 0)
            return true;

        string stateKey = MatchingHandoffRedisKeys.AdmissionStateKey(matchingId);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                bool canceled = await _cacheHelper.StringSetIfEqualsAsync(
                    stateKey,
                    MatchingHandoffRedisKeys.AdmissionPendingState,
                    MatchingHandoffRedisKeys.AdmissionCanceledState,
                    MatchingHandoffRedisKeys.Lifetime);
                if (canceled)
                    return true;

                var state = await _cacheHelper.StringGetAsync(stateKey);
                if (state.IsNullOrEmpty ||
                    string.Equals(state.ToString(), MatchingHandoffRedisKeys.AdmissionCanceledState,
                        StringComparison.Ordinal))
                    return true;
                if (string.Equals(state.ToString(), MatchingHandoffRedisKeys.AdmissionCompletedState,
                        StringComparison.Ordinal))
                {
                    _logger.LogInformation(
                        "Skipped matching rollback because GameServer completed admission first: MatchingId={MatchingId}",
                        matchingId);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not confirm admission cancellation: MatchingId={MatchingId}, Attempt={Attempt}",
                    matchingId,
                    attempt + 1);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        // An unknown terminal state is not authority to roll back a match that GameServer may
        // already have completed. Admission/claim TTLs remain the recovery fallback.
        _logger.LogError(
            "Skipped ambiguous matching rollback after bounded admission-state reconciliation: MatchingId={MatchingId}",
            matchingId);
        return false;
    }

    private async Task DeleteMatchingHandoffBestEffortAsync(long matchingId)
    {
        if (matchingId <= 0)
            return;

        try
        {
            await _cacheHelper.KeyDeleteAsync(MatchingHandoffRedisKeys.Key(matchingId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to delete rolled-back matching handoff; TTL remains as fallback: MatchingId={MatchingId}",
                matchingId);
        }
    }

    private async Task<bool> CleanupCanceledMatchingAdmissionAsync(
        MatchingAdmissionRecoveryRecord record,
        bool? notifyPlayersOverride = null,
        bool protectDurableLifecycleIntent = false)
    {
        try
        {
            MatchingAdmissionRecoveryRecord? persistedRecord = await _scalingContext!.CoordinationStore
                .GetMatchingAdmissionRecoveryAsync(record.MatchingId);
            if (persistedRecord == null)
                return true;
            record = persistedRecord;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Persistent matching cleanup could not read its authoritative recovery intent; cleanup remains indexed: MatchingId={MatchingId}",
                record.MatchingId);
            return false;
        }

        bool notifyPlayers = notifyPlayersOverride ??
                             record.CleanupMode == MatchingAdmissionCleanupMode.AbortAndNotify;
        bool cleanupComplete = true;
        bool hasProtectedUnresolvedRoute = false;
        try
        {
            await _cacheHelper.KeyDeleteAsync(MatchingHandoffRedisKeys.Key(record.MatchingId));
        }
        catch (Exception ex)
        {
            cleanupComplete = false;
            _logger.LogWarning(
                ex,
                "Persistent matching cleanup could not delete the handoff: MatchingId={MatchingId}",
                record.MatchingId);
        }

        int failedProtocolId = 0;
        byte[] failedPayload = Array.Empty<byte>();
        if (notifyPlayers)
        {
            using var packet = PacketMaker.U_TO_C_MATCHING_FAILED(
                ErrorCode.MATCHING_FAILED,
                record.MatchingId);
            packet.RecordSize();
            failedProtocolId = packet.ProtocolId;
            failedPayload = packet.ToBytes();
        }

        DateTimeOffset cleanupOccurredAt = await GetMatchingTimeAsync();
        foreach (MatchingAdmissionRecoveryRoute route in record.Players)
        {
            if (protectDurableLifecycleIntent)
            {
                try
                {
                    if (await HasMatchingPlayerTerminalAsync(record, route))
                        continue;
                    MatchingLifecycleAbortFenceAcquireResult abortFence =
                        await _scalingContext!.LifecycleOutboxStore.TryAcquireAbortFenceAsync(
                            route.PlayerId,
                            record.MatchingId,
                            MatchingLifecycleMarkerLifetime);
                    if (abortFence == MatchingLifecycleAbortFenceAcquireResult.Protected)
                    {
                        hasProtectedUnresolvedRoute = true;
                        continue;
                    }
                    if (abortFence != MatchingLifecycleAbortFenceAcquireResult.Acquired)
                    {
                        throw new InvalidOperationException(
                            $"Unexpected lifecycle abort-fence result '{abortFence}'.");
                    }
                }
                catch (Exception ex)
                {
                    cleanupComplete = false;
                    hasProtectedUnresolvedRoute = true;
                    _logger.LogWarning(
                        ex,
                        "Could not classify a roster lifecycle route; preserving it for retry: MatchingId={MatchingId}, PlayerId={PlayerId}",
                        record.MatchingId,
                        route.PlayerId);
                    continue;
                }
            }

            bool routeCleanupComplete = true;
            if (notifyPlayers)
            {
                try
                {
                    bool resolved = await DeliverMatchingRecoveryFailureAsync(
                        record.MatchingId,
                        route,
                        failedProtocolId,
                        failedPayload);
                    if (!resolved)
                        routeCleanupComplete = false;
                }
                catch (Exception ex)
                {
                    routeCleanupComplete = false;
                    _logger.LogWarning(
                        ex,
                        "Persistent matching failure delivery will be retried: MatchingId={MatchingId}, PlayerId={PlayerId}",
                        record.MatchingId,
                        route.PlayerId);
                }

                try
                {
                    await _cacheHelper.SortedSetRemoveAsync(
                        MatchingQueueKey,
                        route.QueueEntry);
                }
                catch (Exception ex)
                {
                    routeCleanupComplete = false;
                    _logger.LogWarning(
                        ex,
                        "Persistent matching queue cleanup will be retried: MatchingId={MatchingId}, PlayerId={PlayerId}",
                        record.MatchingId,
                        route.PlayerId);
                }
            }

            if (routeCleanupComplete)
            {
                try
                {
                    string eventId =
                        $"matching-recovery-release:{record.MatchingId}:{route.PlayerId}:{route.RequestId}";
                    await _scalingContext!.CoordinationStore.ApplyMatchingLifecycleOnceAsync(
                        eventId,
                        MatchingLifecycleEffect.PlayerReleased,
                        route.PlayerId,
                        record.MatchingId,
                        cleanupOccurredAt,
                        MatchingLifecycleMarkerLifetime);
                }
                catch (Exception ex)
                {
                    routeCleanupComplete = false;
                    _logger.LogWarning(
                        ex,
                        "Persistent terminal fence and exact matching claim release will be retried: MatchingId={MatchingId}, PlayerId={PlayerId}",
                        record.MatchingId,
                        route.PlayerId);
                }
            }

            cleanupComplete &= routeCleanupComplete;
        }

        GameServerMatchOwner? gameServerOwner = record.GetGameServerOwner();
        if (gameServerOwner != null)
        {
            try
            {
                await _scalingContext!.GameServerRoutingStore.ReleaseMatchOwnerAsync(
                    gameServerOwner);
            }
            catch (Exception ex)
            {
                cleanupComplete = false;
                _logger.LogWarning(
                    ex,
                    "Persistent exact GameServer owner release will be retried: MatchingId={MatchingId}, Fence={Fence}",
                    gameServerOwner.MatchingId,
                    gameServerOwner.Fence);
            }
        }

        if (cleanupComplete && !hasProtectedUnresolvedRoute)
        {
            try
            {
                await _scalingContext!.CoordinationStore.RemoveMatchingAdmissionRecoveryAsync(
                    record.MatchingId);
                _logger.LogInformation(
                    "Persistent matching recovery cleanup completed: MatchingId={MatchingId}, NotifiedPlayers={NotifyPlayers}",
                    record.MatchingId,
                    notifyPlayers);
                return true;
            }
            catch (Exception ex)
            {
                cleanupComplete = false;
                _logger.LogWarning(
                    ex,
                    "Persistent matching recovery metadata removal will be retried: MatchingId={MatchingId}",
                    record.MatchingId);
            }
        }

        try
        {
            DateTimeOffset now = await GetMatchingTimeAsync();
            TimeSpan retryDelay = hasProtectedUnresolvedRoute
                ? GetGameServerRecoveryProbeInterval()
                : TimeSpan.FromSeconds(1);
            MatchingAdmissionRecoveryRecord retryRecord = CopyMatchingAdmissionRecovery(
                record,
                now.Add(retryDelay),
                record.AdmissionCompleted);
            await TryUpdateMatchingAdmissionRecoveryAsync(record, retryRecord);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Persistent matching recovery retry reindex failed; the existing expired index remains: MatchingId={MatchingId}",
                record.MatchingId);
        }

        return false;
    }

    private async Task<bool> DeliverMatchingRecoveryFailureAsync(
        long matchingId,
        MatchingAdmissionRecoveryRoute route,
        int protocolId,
        byte[] payload)
    {
        if (Volatile.Read(ref _stopping) != 0)
            return false;

        var request = new MatchingDeliveryRequest
        {
            DeliveryId = MakeMatchingDeliveryId(
                MatchingDeliveryKind.MatchingFailed,
                matchingId,
                route.PlayerId,
                route.RequestId),
            Kind = MatchingDeliveryKind.MatchingFailed,
            PlayerId = route.PlayerId,
            MatchingId = matchingId,
            RequestId = route.RequestId,
            OwnerNodeId = route.OwnerNodeId,
            OwnerNodeGeneration = route.OwnerNodeGeneration,
            OwnerSessionId = route.OwnerSessionId,
            OwnerSessionGeneration = route.OwnerSessionGeneration,
            ProtocolId = protocolId,
            Payload = payload
        };
        MatchingDeliveryResponse response;
        if (IsUserServerScalingEnabled)
        {
            UserSessionOwner? expectedOwner = route.GetSessionOwner();
            UserSessionOwner? currentOwner = await _scalingContext!.CoordinationStore
                .GetSessionOwnerAsync(route.PlayerId);
            if (expectedOwner == null || currentOwner != expectedOwner)
                return true;

            response = await _scalingContext.DeliveryRouter.DeliverAsync(
                request,
                _shutdownCts.Token);
        }
        else
        {
            GameSession? session = _getSession(route.PlayerId);
            if (session == null)
                return true;
            response = session.HandleLocalMatchingDelivery(request);
        }

        return response.Status is
            MatchingDeliveryStatus.Accepted or
            MatchingDeliveryStatus.StaleOwner or
            MatchingDeliveryStatus.SessionUnavailable;
    }

    private bool StartMatchingAdmissionWatchdog(
        long matchingId,
        IReadOnlyCollection<MatchingQueueData> players,
        GameServerMatchOwner? gameServerOwner)
    {
        if (IsAdmissionRecoveryEnabled)
        {
            return TryRunBackgroundOperation(
                () => MonitorPersistentMatchingAdmissionAsync(matchingId),
                $"persistent-matching-admission-watchdog:{matchingId}");
        }

        MatchingQueueData[] snapshot = players
            .Where(data => data.PlayerId > 0)
            .DistinctBy(data => data.PlayerId)
            .ToArray();
        return snapshot.Length > 0 && TryRunBackgroundOperation(
            () => MonitorMatchingAdmissionAsync(matchingId, snapshot, gameServerOwner),
            $"matching-admission-watchdog:{matchingId}");
    }

    private async Task MonitorPersistentMatchingAdmissionAsync(long matchingId)
    {
        MatchingAdmissionRecoveryRecord? record = await _scalingContext!.CoordinationStore
            .GetMatchingAdmissionRecoveryAsync(matchingId);
        if (record == null)
        {
            _logger.LogError(
                "Persistent admission watchdog could not find its recovery metadata: MatchingId={MatchingId}",
                matchingId);
            return;
        }

        DateTimeOffset now = await GetMatchingTimeAsync();
        TimeSpan remaining = TimeSpan.FromMilliseconds(
            Math.Max(0, record.DeadlineUnixMilliseconds - now.ToUnixTimeMilliseconds()));
        try
        {
            await Task.Delay(remaining, _shutdownCts.Token);
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
            // The persistent record deliberately remains for the next matching leader.
            return;
        }

        if (!await RenewMatchingLeadershipAsync())
            return;
        await RecoverMatchingAdmissionAsync(matchingId);
    }

    private async Task MonitorMatchingAdmissionAsync(
        long matchingId,
        IReadOnlyCollection<MatchingQueueData> players,
        GameServerMatchOwner? gameServerOwner)
    {
        try
        {
            await Task.Delay(MatchingHandoffRedisKeys.AdmissionTimeout, _shutdownCts.Token);
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
            return;
        }

        string stateKey = MatchingHandoffRedisKeys.AdmissionStateKey(matchingId);
        while (Volatile.Read(ref _stopping) == 0)
        {
            try
            {
                bool canceled = await _cacheHelper.StringSetIfEqualsAsync(
                    stateKey,
                    MatchingHandoffRedisKeys.AdmissionPendingState,
                    MatchingHandoffRedisKeys.AdmissionCanceledState,
                    MatchingHandoffRedisKeys.Lifetime);
                if (!canceled)
                {
                    var state = await _cacheHelper.StringGetAsync(stateKey);
                    if (!state.IsNullOrEmpty &&
                        string.Equals(state.ToString(), MatchingHandoffRedisKeys.AdmissionCompletedState,
                            StringComparison.Ordinal))
                        return;
                    if (state.IsNullOrEmpty ||
                        !string.Equals(state.ToString(), MatchingHandoffRedisKeys.AdmissionCanceledState,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Admission state for match {matchingId} is ambiguous: '{state}'.");
                    }
                }

                _logger.LogWarning(
                    "Matching admission timed out; rolling back the whole human roster: MatchingId={MatchingId}, Players={PlayerCount}",
                    matchingId,
                    players.Count);
                await DeleteMatchingHandoffBestEffortAsync(matchingId);
                foreach (MatchingQueueData player in players)
                {
                    try
                    {
                        await NotifyMatchingAdmissionFailedAsync(player, matchingId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Matching admission timeout notification failed: PlayerId={PlayerId}, MatchingId={MatchingId}",
                            player.PlayerId,
                            matchingId);
                    }

                    if (!IsUserServerScalingEnabled)
                        _getSession(player.PlayerId)?.ClearMatchingAssignment(matchingId);
                    await ReleaseMatchingClaimAsync(player.PlayerId, matchingId);
                }
                await ReleaseGameServerOwnerBestEffortAsync(gameServerOwner);
                return;
            }
            catch (Exception ex)
            {
                // A failed read is ambiguous: GameServer may have committed admission. Retry instead
                // of issuing a false rollback while Redis connectivity is degraded.
                _logger.LogWarning(
                    ex,
                    "Could not verify matching admission timeout; retrying: MatchingId={MatchingId}",
                    matchingId);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), _shutdownCts.Token);
                }
                catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private async Task NotifyMatchingBatchFailedAsync(
        IEnumerable<MatchingQueueData> players,
        long matchingId)
    {
        try
        {
            using var packet = PacketMaker.U_TO_C_MATCHING_FAILED(ErrorCode.MATCHING_FAILED, matchingId);
            packet.RecordSize();
            byte[] payload = packet.ToBytes();
            if (!IsUserServerScalingEnabled)
            {
                foreach (MatchingQueueData player in players.DistinctBy(data => data.PlayerId))
                {
                    GameSession? session = _getSession(player.PlayerId);
                    if (session == null)
                        continue;

                    MatchingDeliveryResponse response = session.HandleLocalMatchingDelivery(
                        new MatchingDeliveryRequest
                        {
                            DeliveryId = Guid.NewGuid().ToString("N"),
                            Kind = MatchingDeliveryKind.MatchingFailed,
                            PlayerId = player.PlayerId,
                            MatchingId = matchingId,
                            RequestId = player.RequestId,
                            ProtocolId = packet.ProtocolId,
                            Payload = payload
                        });
                    if (response.Status != MatchingDeliveryStatus.Accepted)
                    {
                        _logger.LogWarning(
                            "Local matching rollback notification was rejected: PlayerId={PlayerId}, MatchingId={MatchingId}, Status={Status}",
                            player.PlayerId,
                            matchingId,
                            response.Status);
                    }
                }
                return;
            }

            foreach (MatchingQueueData player in players.DistinctBy(data => data.PlayerId))
            {
                try
                {
                    bool delivered = await DeliverMatchingPacketAsync(
                        player,
                        matchingId,
                        MatchingDeliveryKind.MatchingFailed,
                        packet.ProtocolId,
                        payload,
                        CancellationToken.None);
                    if (!delivered)
                    {
                        _logger.LogWarning(
                            "Matching rollback notification was not accepted by the session owner: PlayerId={PlayerId}, MatchingId={MatchingId}",
                            player.PlayerId,
                            matchingId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Matching rollback notification delivery failed: PlayerId={PlayerId}, MatchingId={MatchingId}",
                        player.PlayerId,
                        matchingId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send matching batch rollback notification");
        }
    }

    private async Task<bool> DeliverMatchingPacketAsync(
        MatchingQueueData data,
        long matchingId,
        MatchingDeliveryKind kind,
        int protocolId,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        if (!HasValidDeliveryRoute(data))
            return false;

        var request = new MatchingDeliveryRequest
        {
            DeliveryId = MakeMatchingDeliveryId(
                kind,
                matchingId,
                data.PlayerId,
                data.RequestId),
            Kind = kind,
            PlayerId = data.PlayerId,
            MatchingId = matchingId,
            RequestId = data.RequestId,
            OwnerNodeId = data.OwnerNodeId,
            OwnerNodeGeneration = data.OwnerNodeGeneration,
            OwnerSessionId = data.OwnerSessionId,
            OwnerSessionGeneration = data.OwnerSessionGeneration,
            ProtocolId = protocolId,
            Payload = payload
        };
        MatchingDeliveryResponse response = await _scalingContext!.DeliveryRouter.DeliverAsync(
            request,
            cancellationToken);
        return response.Status == MatchingDeliveryStatus.Accepted;
    }

    private async Task NotifyMatchingAdmissionFailedAsync(
        MatchingQueueData player,
        long matchingId)
    {
        using var packet = PacketMaker.U_TO_C_MATCHING_FAILED(ErrorCode.MATCHING_FAILED, matchingId);
        packet.RecordSize();
        byte[] payload = packet.ToBytes();
        if (IsUserServerScalingEnabled)
        {
            bool accepted = await DeliverMatchingPacketAsync(
                player,
                matchingId,
                MatchingDeliveryKind.MatchingAdmissionFailed,
                packet.ProtocolId,
                payload,
                CancellationToken.None);
            if (!accepted)
            {
                _logger.LogWarning(
                    "Matching admission failure was rejected by the session owner: PlayerId={PlayerId}, MatchingId={MatchingId}",
                    player.PlayerId,
                    matchingId);
            }
            return;
        }

        GameSession? session = _getSession(player.PlayerId);
        MatchingDeliveryResponse? response = session?.HandleLocalMatchingDelivery(new MatchingDeliveryRequest
        {
            DeliveryId = Guid.NewGuid().ToString("N"),
            Kind = MatchingDeliveryKind.MatchingAdmissionFailed,
            PlayerId = player.PlayerId,
            MatchingId = matchingId,
            RequestId = player.RequestId,
            ProtocolId = packet.ProtocolId,
            Payload = payload
        });
        if (response is { Status: not MatchingDeliveryStatus.Accepted })
        {
            _logger.LogWarning(
                "Local matching admission failure was rejected: PlayerId={PlayerId}, MatchingId={MatchingId}, Status={Status}",
                player.PlayerId,
                matchingId,
                response.Status);
        }
    }

    private async Task<bool> ProcessMatchedEntry(byte[] entry, long matchingId,
        long targetPlayerId, JobTitle targetJobTitle, JobTitle myJobTitle, List<PlayerInfo> playerRoster,
        List<GameHandoffRosterEntry> humanHandoffRoster, Cell spawnCell,
        GameServerAllocation gameServerAllocation)
    {
        var data = MessagePackSerializer.Deserialize<MatchingQueueData>(entry);
        _logger.LogInformation("Processing matched player {DataPlayerId} (Target={TargetPlayerId})", data.PlayerId, targetPlayerId);

        GameSession? session = null;
        if (IsUserServerScalingEnabled)
        {
            if (!await HasExactSessionOwnerAsync(data))
            {
                _logger.LogWarning(
                    "Matched player owner route changed before handoff issuance: PlayerId={DataPlayerId}",
                    data.PlayerId);
                return false;
            }
        }
        else
        {
            session = _getSession(data.PlayerId);
            if (session?.PlayerInfo == null || !session.IsConnected)
            {
                _logger.LogWarning(
                    "Matched player session or PlayerInfo is unavailable: PlayerId={DataPlayerId}",
                    data.PlayerId);
                return false;
            }
        }

        MapId mapId = Config.SWARM_MATCH_MAP;
        var spawnPosition = Cell.Clone(spawnCell);
        if (spawnPosition.X == 0 && spawnPosition.Y == 0)
        {
            throw new InvalidOperationException($"Missing Survivor Royale spawn assignment for player {data.PlayerId}.");
        }
        // Protect the PlayerInfo update with its distributed lock.
        await using var playerLock = await PlayerInfo.Lock(_redLock, data.PlayerId);
        var playerInfo = await PlayerInfo.Load(_cacheHelper, data.PlayerId);
        if (playerInfo == null)
        {
            _logger.LogError("Failed to reload matched PlayerInfo: PlayerId={DataPlayerId}", data.PlayerId);
            return false;
        }

        if (IsUserServerScalingEnabled)
        {
            if (!await HasExactSessionOwnerAsync(data))
            {
                _logger.LogWarning(
                    "Matched player owner route changed while preparing handoff: PlayerId={DataPlayerId}",
                    data.PlayerId);
                return false;
            }
        }
        else
        {
            if (!ReferenceEquals(session, _getSession(data.PlayerId)) ||
                session!.PlayerInfo == null ||
                !session.IsConnected)
            {
                _logger.LogWarning(
                    "Matched player session changed before handoff issuance: PlayerId={DataPlayerId}",
                    data.PlayerId);
                return false;
            }

            if (!session.TryAssignMatching(matchingId, data.RequestId))
                return false;
        }

        bool delivered = false;
        try
        {
            playerInfo.LastMapId = mapId;
            playerInfo.LastMapSubId = matchingId;
            playerInfo.LastCell = spawnPosition;
            playerInfo.ObjectInfo.MapId = mapId;
            playerInfo.ObjectInfo.MapSubId = matchingId;
            playerInfo.ObjectInfo.Cell = Cell.Clone(spawnPosition);
            playerInfo.ObjectInfo.Position = CellToWorldPosition(mapId, spawnPosition);
            playerInfo.ObjectInfo.Velocity = new Vector3f(0f, 0f, 0f);
            playerInfo.ObjectInfo.MoveTimestamp = DateTime.UtcNow;
            string gameHandoffTicket = await _gameHandoffTicketService.IssueAsync(new GameHandoffContext
            {
                PlayerId = data.PlayerId,
                MatchingId = matchingId,
                MapId = mapId,
                MapSubId = matchingId,
                SpawnPosition = Cell.Clone(spawnPosition),
                TargetPlayerId = targetPlayerId,
                TargetJobTitle = targetJobTitle,
                MyJobTitle = myJobTitle,
                ActiveBuffIds = new List<int>(),
                HumanRoster = humanHandoffRoster,
                GameServerNodeId = gameServerAllocation.Owner?.NodeId ?? string.Empty,
                GameServerGeneration = gameServerAllocation.Owner?.Generation ?? string.Empty,
                GameServerFence = gameServerAllocation.Owner?.Fence ?? 0
            });

            // Rolling-deployment bridge for the previous GameServer, which still reads the
            // authoritative spawn from the shared matching handoff hash. Disable this only after
            // every old GameServer instance has drained.
            if (_writeLegacySpawnFields)
            {
                string legacyHandoffKey = MatchingHandoffRedisKeys.Key(matchingId);
                await _cacheHelper.HashSetAsync(
                    legacyHandoffKey,
                    MatchingHandoffRedisKeys.SpawnField(data.PlayerId),
                    MessagePackSerializer.Serialize(spawnPosition));
                await _cacheHelper.KeyExpireAsync(legacyHandoffKey, MatchingHandoffRedisKeys.Lifetime);
            }

            long gameEndTimestamp = DateTimeOffset.UtcNow.AddMinutes(Config.GAME_DURATION_MINUTES)
                .ToUnixTimeMilliseconds();

            using var packet = PacketMaker.U_TO_C_MATCHING_SUCCESS(
                matchingId, mapId, matchingId, spawnPosition,
                gameServerAllocation.PublicHost, gameServerAllocation.PublicPort, gameEndTimestamp,
                gameHandoffTicket, targetPlayerId, targetJobTitle, myJobTitle, playerRoster, new List<int>()
            );

            bool accepted;
            if (IsUserServerScalingEnabled)
            {
                packet.RecordSize();
                accepted = await DeliverMatchingPacketAsync(
                    data,
                    matchingId,
                    MatchingDeliveryKind.MatchingSucceeded,
                    packet.ProtocolId,
                    packet.ToBytes(),
                    _shutdownCts.Token);
            }
            else
            {
                accepted = session!.TrySend(packet);
            }

            if (!accepted)
            {
                _logger.LogWarning(
                    "Matching success was not accepted by the exact session owner: PlayerId={DataPlayerId}",
                    data.PlayerId);
                return false;
            }

            delivered = true;
            _logger.LogInformation("Matching success sent: PlayerId={DataPlayerId}, Target={TargetPlayerId}, MyJob={MyJob}, TargetJob={TargetJob}",
                data.PlayerId, targetPlayerId, myJobTitle, targetJobTitle);
            return true;
        }
        finally
        {
            if (!delivered && !IsUserServerScalingEnabled)
                session?.ClearMatchingAssignment(matchingId);
        }
    }

    private async Task<bool> HasExactSessionOwnerAsync(MatchingQueueData data)
    {
        if (!HasValidDeliveryRoute(data))
            return false;

        UserSessionOwner? currentOwner = await _scalingContext!.CoordinationStore
            .GetSessionOwnerAsync(data.PlayerId);
        return currentOwner != null &&
               string.Equals(currentOwner.NodeId, data.OwnerNodeId, StringComparison.Ordinal) &&
               string.Equals(currentOwner.NodeGeneration, data.OwnerNodeGeneration, StringComparison.Ordinal) &&
               string.Equals(currentOwner.SessionId, data.OwnerSessionId, StringComparison.Ordinal) &&
               currentOwner.SessionGeneration == data.OwnerSessionGeneration;
    }

    private static Vector3f CellToWorldPosition(MapId mapId, Cell cell) =>
        MapCoordinateConverter.CellToWorld(mapId, cell);

    /// <summary>
    ///     Returns the queue delay: 30 seconds per leave, capped at 300 seconds.
    ///     One leave is removed for each elapsed 24-hour interval.
    /// </summary>
    private async Task<long> GetLeavePenaltyDelayAsync(long playerId)
    {
        try
        {
            var value = await _cacheHelper.HashGetAsync(LeavePenaltyKey, playerId);
            if (value.IsNullOrEmpty) return 0;

            long leaveCount = BitConverter.ToInt64((byte[])value!);
            if (leaveCount <= 0) return 0;

            // Apply 24-hour time decay before calculating the delay.
            leaveCount = await ApplyTimeDecayAsync(playerId, leaveCount);
            if (leaveCount <= 0) return 0;

            long penalty = Math.Min(leaveCount * LeavePenaltySeconds, MaxLeavePenaltySeconds);
            return penalty;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    ///     Removes one leave count for every complete 24-hour interval.
    ///     Initializes the decay anchor on the first lookup when none exists.
    /// </summary>
    private async Task<long> ApplyTimeDecayAsync(long playerId, long leaveCount)
    {
        try
        {
            var decayAtValue = await _cacheHelper.HashGetAsync(LeavePenaltyDecayAtKey, playerId);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (decayAtValue.IsNullOrEmpty)
            {
                // Initialize the decay anchor to the current time.
                await _cacheHelper.HashSetAsync(LeavePenaltyDecayAtKey, playerId, BitConverter.GetBytes(now));
                return leaveCount;
            }

            long decayAt = BitConverter.ToInt64((byte[])decayAtValue!);
            long elapsedSeconds = now - decayAt;
            long decayIntervalSeconds = PenaltyDecayIntervalHours * 3600L;

            if (elapsedSeconds < decayIntervalSeconds) return leaveCount;

            // Remove one count per elapsed 24-hour interval.
            long decayCount = elapsedSeconds / decayIntervalSeconds;
            leaveCount = Math.Max(0, leaveCount - decayCount);

            // Advance the decay anchor while preserving any fractional interval.
            long newDecayAt = decayAt + decayCount * decayIntervalSeconds;

            if (leaveCount <= 0)
            {
                // Remove both keys when the leave penalty has fully decayed.
                await _cacheHelper.HashDeleteAsync(LeavePenaltyKey, playerId);
                await _cacheHelper.HashDeleteAsync(LeavePenaltyDecayAtKey, playerId);
                _logger.LogInformation("Leave penalty fully decayed: PlayerId={PlayerId}", playerId);
            }
            else
            {
                await _cacheHelper.HashSetAsync(LeavePenaltyKey, playerId, BitConverter.GetBytes(leaveCount));
                await _cacheHelper.HashSetAsync(LeavePenaltyDecayAtKey, playerId, BitConverter.GetBytes(newDecayAt));
                _logger.LogInformation("Leave penalty decayed: PlayerId={PlayerId}, RemainingCount={Count}", playerId, leaveCount);
            }

            return leaveCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decay leave penalty: PlayerId={PlayerId}", playerId);
            return leaveCount;
        }
    }

    /// <summary>
    ///     Records a leave and releases the exact active matching claim reported by GameServer.
    /// </summary>
    public async Task RecordLeaveAsync(long playerId, long matchingId)
    {
        await ReleaseMatchingClaimAsync(playerId, matchingId);
        try
        {
            var existing = await _cacheHelper.HashGetAsync(LeavePenaltyKey, playerId);
            long count = existing.IsNullOrEmpty ? 1 : BitConverter.ToInt64((byte[])existing!) + 1;
            await _cacheHelper.HashSetAsync(LeavePenaltyKey, playerId, BitConverter.GetBytes(count));

            if (existing.IsNullOrEmpty)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                await _cacheHelper.HashSetAsync(LeavePenaltyDecayAtKey, playerId, BitConverter.GetBytes(now));
            }

            _logger.LogInformation("Leave penalty recorded: PlayerId={PlayerId}, Count={Count}", playerId, count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Leave penalty record failed: PlayerId={PlayerId}", playerId);
        }
    }

    public async Task RecordGameCompletionAsync(long playerId, long matchingId)
    {
        await ReleaseMatchingClaimAsync(playerId, matchingId);
        try
        {
            var value = await _cacheHelper.HashGetAsync(LeavePenaltyKey, playerId);
            if (value.IsNullOrEmpty) return;

            long leaveCount = BitConverter.ToInt64((byte[])value!);
            if (leaveCount <= 0) return;

            leaveCount = Math.Max(0, leaveCount - 1);

            if (leaveCount == 0)
            {
                await _cacheHelper.HashDeleteAsync(LeavePenaltyKey, playerId);
                await _cacheHelper.HashDeleteAsync(LeavePenaltyDecayAtKey, playerId);
            }
            else
            {
                await _cacheHelper.HashSetAsync(LeavePenaltyKey, playerId, BitConverter.GetBytes(leaveCount));
            }

            _logger.LogInformation("Leave penalty reduced after normal completion: PlayerId={PlayerId}, RemainingCount={Count}", playerId, leaveCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reduce leave penalty after normal completion: PlayerId={PlayerId}", playerId);
        }
    }

    public async Task AbortMatchingAdmissionAsync(long playerId, long matchingId)
    {
        MatchingQueueData? player = null;
        if (IsUserServerScalingEnabled)
        {
            UserSessionOwner? owner = await _scalingContext!.CoordinationStore.GetSessionOwnerAsync(playerId);
            if (owner != null)
            {
                player = new MatchingQueueData
                {
                    PlayerId = playerId,
                    RequestTime = DateTime.UtcNow,
                    OwnerNodeId = owner.NodeId,
                    OwnerNodeGeneration = owner.NodeGeneration,
                    OwnerSessionId = owner.SessionId,
                    OwnerSessionGeneration = owner.SessionGeneration,
                    RequestId = Guid.NewGuid().ToString("N")
                };
            }
        }
        else
        {
            GameSession? session = _getSession(playerId);
            string? requestId = session?.ActiveMatchingRequestId;
            if (UserServerClusterOptions.IsSafeTokenComponent(requestId))
            {
                player = new MatchingQueueData
                {
                    PlayerId = playerId,
                    RequestId = requestId!
                };
            }
        }

        if (player != null)
            await NotifyMatchingAdmissionFailedAsync(player, matchingId);
        await ReleaseMatchingClaimAsync(playerId, matchingId);
    }

    public async Task ReleaseMatchingClaimAsync(long playerId, long matchingId)
    {
        try
        {
            if (matchingId > 0)
            {
                await _cacheHelper.StringDeleteIfEqualsAsync(
                    MakeMatchingClaimKey(playerId),
                    matchingId.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                // Rolling compatibility for the previous 8-byte lifecycle payload.
                await _cacheHelper.KeyDeleteAsync(MakeMatchingClaimKey(playerId));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to release matching claim; TTL remains as fallback: PlayerId={PlayerId}, MatchingId={MatchingId}",
                playerId,
                matchingId);
        }
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
            _matchingTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

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

        await _matchingTimer.DisposeAsync();
        await _matchingLeaderHeartbeatTask;

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

        MatchingLeaderLease? leaderLease = Interlocked.Exchange(ref _matchingLeaderLease, null);
        if (leaderLease != null && _scalingContext != null)
        {
            try
            {
                await _scalingContext.CoordinationStore.ReleaseMatchingLeaderAsync(leaderLease);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Matching leader release failed; lease TTL remains as fallback: Fence={Fence}",
                    leaderLease.Fence);
            }
        }

        _shutdownCts.Dispose();

        _logger.LogInformation("MatchingManager stopped");
    }

    private sealed class MatchingClaimLease(string claimId, List<long> playerIds)
    {
        public string ClaimId { get; } = claimId;
        public List<long> PlayerIds { get; } = playerIds;
        public long? MatchingId { get; set; }
    }

    private sealed record GameServerAllocation(
        string PublicHost,
        int PublicPort,
        GameServerMatchOwner? Owner);
}

[MessagePackObject]
public class MatchingQueueData
{
    [Key(0)]
    public long PlayerId { get; set; }

    [Key(1)]
    public DateTime RequestTime { get; set; }

    [Key(2)]
    public string UserChannel { get; set; } = string.Empty;

    [Key(3)]
    public string OwnerNodeId { get; set; } = string.Empty;

    [Key(4)]
    public string OwnerNodeGeneration { get; set; } = string.Empty;

    [Key(5)]
    public string OwnerSessionId { get; set; } = string.Empty;

    [Key(6)]
    public long OwnerSessionGeneration { get; set; }

    [Key(7)]
    public string RequestId { get; set; } = string.Empty;
}

/// <summary>
///     One player-to-target link in the circular Manitto chain, including roles and spawn data.
/// </summary>
public class RosterChainLink
{
    public byte[] Entry { get; set; } = Array.Empty<byte>();
    public long TargetPlayerId { get; set; }
    public JobTitle MyJobTitle { get; set; }
    public JobTitle TargetJobTitle { get; set; }
    public PersonaType Persona { get; set; } = PersonaType.None;
    public AreaType StartArea { get; set; } = AreaType.None;
    public Cell SpawnCell { get; set; } = new(0, 0);

}
