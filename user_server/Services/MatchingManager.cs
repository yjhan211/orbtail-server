using System.Collections.Concurrent;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.contracts.authentication;
using network.helpers;
using network.infrastructure;
using network.interfaces;
using network.packets;
using user_server.network;

namespace user_server.services;

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

    private static int PlayersPerMatch => IsSoloMapValidation ? 1 : IsTwoPlayerTestMatch ? 2 : DefaultPlayersPerMatch;
    private static int GamePlayersPerMatch => IsSoloMapValidation
        ? DefaultPlayersPerMatch
        : Config.SWARM_PLAYERS_PER_MATCH;

    private static bool IsTwoPlayerTestMatch => Environment.GetEnvironmentVariable("TEST_TWO_PLAYER_MATCH") == "1";
    private static bool IsSoloMapValidation =>
        Environment.GetEnvironmentVariable("SOLO_MAP_VALIDATION") == "1";

    private static long _botIdCounter; // Negative PlayerIds are reserved for bots.
    private readonly ICacheHelper _cacheHelper;
    private readonly ConcurrentDictionary<long, Task> _backgroundTasks = new();
    private readonly object _backgroundTaskLock = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Func<long, GameSession?> _getSession;
    private readonly IGameHandoffTicketService _gameHandoffTicketService;
    private readonly MatchingQueueClaimCoordinator _matchingClaims;
    private readonly ILogger _logger;
    private Timer? _matchingTimer;
    private readonly object _processingTaskLock = new();
    private readonly IRedLockFactory _redLock;
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
        _cacheHelper = cacheHelper;
        _redLock = redLock;
        _gameHandoffTicketService = gameHandoffTicketService;
        _matchingClaims = new MatchingQueueClaimCoordinator(cacheHelper, matchingClaimStore, logger);
        _getSession = getSession;
    }

    /// <summary>
    ///     Starts queue polling after the lifecycle subscriptions are ready.
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

    public async Task<ErrorCode> AddToQueue(long playerId, GameSession user)
    {
        try
        {
            await using var queueLock = await _redLock.AcquireLockAsync(
                MakeMatchingQueueLockKey(playerId),
                Config.LOCK_TTL);
            if (await _matchingClaims.HasClaimAsync(playerId))
                return ErrorCode.MATCHING_ALREADY_IN_QUEUE;

            int removedCount = await RemovePlayerEntriesFromQueueAsync(playerId);
            if (removedCount > 0)
                _logger.LogInformation("Player {PlayerId}: removed {Count} stale matching entries", playerId, removedCount);

            // A worker may have claimed the snapshot that was removed above.
            if (await _matchingClaims.HasClaimAsync(playerId))
                return ErrorCode.MATCHING_ALREADY_IN_QUEUE;

            string? requestId = user.ActiveMatchingRequestId;
            if (!MatchingRequestTokens.IsSafeTokenComponent(requestId))
            {
                _logger.LogWarning(
                    "Matching queue rejected because the session has no active request fence: PlayerId={PlayerId}",
                    playerId);
                return ErrorCode.MATCHING_FAILED;
            }

            DateTimeOffset matchingNow = DateTimeOffset.UtcNow;
            var queueData = new MatchingQueueData
            {
                PlayerId = playerId,
                RequestTime = matchingNow.UtcDateTime,
                UserChannel = user.GetChannelName(),
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
            MatchingClaimLease? cancellationClaim =
                await _matchingClaims.TryAcquireCancellationAsync(playerId);
            if (cancellationClaim == null)
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
                await _matchingClaims.ReleaseCancellationAsync(cancellationClaim);
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
                removeEntry = data.PlayerId <= 0 || !seenPlayerIds.Add(data.PlayerId);
                if (!removeEntry)
                {
                    validEntries.Add(entry);
                    continue;
                }

                _logger.LogWarning("Removed duplicate or invalid matching entry: PlayerId={PlayerId}", data.PlayerId);
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

    private static string MakeMatchingQueueLockKey(long playerId)
    {
        return MatchingQueueLockKeyPrefix + playerId;
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

    private async Task ProcessMatchingQueueAsync()
    {
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
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

                byte[][] groupEntries = entriesToMatch.Skip(i).Take(PlayersPerMatch).ToArray();
                var claimLease = await _matchingClaims.TryAcquireAsync(groupEntries);
                if (claimLease == null)
                {
                    _logger.LogInformation("Matching group skipped because another worker owns a player claim");
                    continue;
                }

                bool matchCommitted = false;
                long matchingId = 0;
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
                    GameServerAllocation gameServerAllocation = GetGameServerAllocation();
                    await _matchingClaims.CommitAsync(claimLease, matchingId);

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

                    // Notify real players and remove their committed queue entries.
                    foreach (var link in chain)
                    {
                        var data = MessagePackSerializer.Deserialize<MatchingQueueData>(link.Entry);
                        if (data.PlayerId < 0) continue; // Skip bots.

                        bool delivered = false;
                        try
                        {
                            delivered = await ProcessMatchedEntry(link.Entry, matchingId, link.TargetPlayerId,
                                playerRoster, humanHandoffRoster, link.SpawnCell,
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
                    if (deliveryComplete)
                    {
                        await MarkMatchingHandoffReadyAsync(matchingId);
                        if (!StartMatchingAdmissionWatchdog(matchingId, batchPlayers))
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
                        // matchingId 발급 전에 실패했으면 handoff·입장 상태가 아직 없으므로 claim만 되돌린다.
                        if (matchingId <= 0)
                        {
                            await _matchingClaims.RollbackAsync(claimLease);
                        }
                        else if (await TryCancelAdmissionForRollbackAsync(matchingId))
                        {
                            await DeleteMatchingHandoffBestEffortAsync(matchingId);
                            await NotifyMatchingBatchFailedAsync(batchPlayers, matchingId);
                            await _matchingClaims.RollbackAsync(claimLease);
                        }
                    }
                }
            }

            // Fill long-waiting groups with bots.
            if (Volatile.Read(ref _stopping) != 0)
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

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long botCutoff = now - BotFillTimeoutSeconds;

        byte[][] longWaitEntries = await _cacheHelper.SortedSetRangeByScoreAsync(
            MatchingQueueKey, double.NegativeInfinity, botCutoff);
        longWaitEntries = await SanitizeMatchingEntriesAsync(longWaitEntries);

        if (longWaitEntries.Length < PlayersPerMatch || longWaitEntries.Length >= GamePlayersPerMatch) return;

        var claimLease = await _matchingClaims.TryAcquireAsync(longWaitEntries);
        if (claimLease == null)
        {
            _logger.LogInformation("Bot-fill match skipped because another worker owns a player claim");
            return;
        }

        bool matchCommitted = false;
        long matchingId = 0;
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
            GameServerAllocation gameServerAllocation = GetGameServerAllocation();
            await _matchingClaims.CommitAsync(claimLease, matchingId);
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

            // Notify real players of the completed bot-filled match.
            foreach (var link in chain)
            {
                var data = MessagePackSerializer.Deserialize<MatchingQueueData>(link.Entry);
                if (data.PlayerId < 0) continue; // Skip bots.

                bool delivered = false;
                try
                {
                    delivered = await ProcessMatchedEntry(link.Entry, matchingId, link.TargetPlayerId,
                        playerRoster, humanHandoffRoster, link.SpawnCell,
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
            if (deliveryComplete)
            {
                await MarkMatchingHandoffReadyAsync(matchingId);
                if (!StartMatchingAdmissionWatchdog(matchingId, batchPlayers))
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
                // matchingId 발급 전에 실패했으면 handoff·입장 상태가 아직 없으므로 claim만 되돌린다.
                if (matchingId <= 0)
                {
                    await _matchingClaims.RollbackAsync(claimLease);
                }
                else if (await TryCancelAdmissionForRollbackAsync(matchingId))
                {
                    await DeleteMatchingHandoffBestEffortAsync(matchingId);
                    await NotifyMatchingBatchFailedAsync(batchPlayers, matchingId);
                    await _matchingClaims.RollbackAsync(claimLease);
                }
            }
        }
    }

    /// <summary>
    ///     Unity가 직접 접속할 Game Server 주소. 단일 Game Server 구성이라 환경 변수로 고정한다.
    /// </summary>
    private static GameServerAllocation GetGameServerAllocation()
    {
        string gameServerIp = Environment.GetEnvironmentVariable("GAME_SERVER_IP") ?? "127.0.0.1";
        int gameServerPort = int.TryParse(
            Environment.GetEnvironmentVariable("GAME_SERVER_PORT"),
            out int configuredPort)
            ? configuredPort
            : 9001;
        return new GameServerAllocation(gameServerIp, gameServerPort);
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

        // Deserialize PlayerIds once before building the chain.
        var players = entries.Select(e => MessagePackSerializer.Deserialize<MatchingQueueData>(e)).ToList();

        var chain = new List<RosterChainLink>();
        for (int i = 0; i < entries.Count; i++)
        {
            int targetIndex = (i + 1) % entries.Count;
            chain.Add(new RosterChainLink
            {
                Entry = entries[i],
                TargetPlayerId = players[targetIndex].PlayerId
            });
        }

        _logger.LogInformation("Target chain created: {Chain}",
            string.Join(" -> ", players.Select(p => p.PlayerId.ToString())) + $" -> {players[0].PlayerId}");

        return chain;
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

        var chain = new List<RosterChainLink>();
        for (int i = 0; i < ordered.Count; i++)
        {
            int targetIndex = (i + 1) % ordered.Count;
            chain.Add(new RosterChainLink
            {
                Entry = ordered[i].Entry,
                TargetPlayerId = players[targetIndex].PlayerId
            });
        }

        _logger.LogInformation(
            "TEST_TWO_PLAYER_MATCH deterministic chain: {Chain}",
            string.Join(" -> ", players.Select(p => p.PlayerId.ToString())) + $" -> {players[0].PlayerId}");

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
                TargetPlayerId = item.Link.TargetPlayerId
            })
            .ToList();
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

    private bool StartMatchingAdmissionWatchdog(
        long matchingId,
        IReadOnlyCollection<MatchingQueueData> players)
    {
        MatchingQueueData[] snapshot = players
            .Where(data => data.PlayerId > 0)
            .DistinctBy(data => data.PlayerId)
            .ToArray();
        return snapshot.Length > 0 && TryRunBackgroundOperation(
            () => MonitorMatchingAdmissionAsync(matchingId, snapshot),
            $"matching-admission-watchdog:{matchingId}");
    }

    private async Task MonitorMatchingAdmissionAsync(
        long matchingId,
        IReadOnlyCollection<MatchingQueueData> players)
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
                        NotifyMatchingAdmissionFailed(player, matchingId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Matching admission timeout notification failed: PlayerId={PlayerId}, MatchingId={MatchingId}",
                            player.PlayerId,
                            matchingId);
                    }

                    _getSession(player.PlayerId)?.ClearMatchingAssignment(matchingId);
                    await ReleaseMatchingClaimAsync(player.PlayerId, matchingId);
                }
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
            foreach (MatchingQueueData player in players.DistinctBy(data => data.PlayerId))
            {
                GameSession? session = _getSession(player.PlayerId);
                if (session == null)
                    continue;

                using var packet = PacketMaker.U_TO_C_MATCHING_FAILED(ErrorCode.MATCHING_FAILED, matchingId);
                if (!session.TryDeliverMatchingFailed(matchingId, player.RequestId, packet))
                {
                    _logger.LogWarning(
                        "Local matching rollback notification was rejected: PlayerId={PlayerId}, MatchingId={MatchingId}",
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

    private void NotifyMatchingAdmissionFailed(MatchingQueueData player, long matchingId)
    {
        GameSession? session = _getSession(player.PlayerId);
        if (session == null)
            return;

        using var packet = PacketMaker.U_TO_C_MATCHING_FAILED(ErrorCode.MATCHING_FAILED, matchingId);
        if (!session.TryDeliverAdmissionFailed(matchingId, packet))
        {
            _logger.LogWarning(
                "Local matching admission failure was rejected: PlayerId={PlayerId}, MatchingId={MatchingId}",
                player.PlayerId,
                matchingId);
        }
    }

    private async Task<bool> ProcessMatchedEntry(byte[] entry, long matchingId,
        long targetPlayerId, List<PlayerInfo> playerRoster,
        List<GameHandoffRosterEntry> humanHandoffRoster, Cell spawnCell,
        GameServerAllocation gameServerAllocation)
    {
        var data = MessagePackSerializer.Deserialize<MatchingQueueData>(entry);
        _logger.LogInformation("Processing matched player {DataPlayerId} (Target={TargetPlayerId})", data.PlayerId, targetPlayerId);

        GameSession? session = _getSession(data.PlayerId);
        if (session?.PlayerInfo == null || !session.IsConnected)
        {
            _logger.LogWarning(
                "Matched player session or PlayerInfo is unavailable: PlayerId={DataPlayerId}",
                data.PlayerId);
            return false;
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

        if (!ReferenceEquals(session, _getSession(data.PlayerId)) ||
            session.PlayerInfo == null ||
            !session.IsConnected)
        {
            _logger.LogWarning(
                "Matched player session changed before handoff issuance: PlayerId={DataPlayerId}",
                data.PlayerId);
            return false;
        }

        if (!session.TryAssignMatching(matchingId, data.RequestId))
            return false;

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
                ActiveBuffIds = new List<int>(),
                HumanRoster = humanHandoffRoster
            });

            long gameEndTimestamp = DateTimeOffset.UtcNow.AddMinutes(Config.GAME_DURATION_MINUTES)
                .ToUnixTimeMilliseconds();

            using var packet = PacketMaker.U_TO_C_MATCHING_SUCCESS(
                matchingId, mapId, matchingId, spawnPosition,
                gameServerAllocation.PublicHost, gameServerAllocation.PublicPort, gameEndTimestamp,
                gameHandoffTicket, targetPlayerId, playerRoster, new List<int>()
            );

            if (!session.TryDeliverMatchingSuccess(matchingId, data.RequestId, packet))
            {
                _logger.LogWarning(
                    "Matching success was not accepted by the exact session owner: PlayerId={DataPlayerId}",
                    data.PlayerId);
                return false;
            }

            delivered = true;
            _logger.LogInformation("Matching success sent: PlayerId={DataPlayerId}, Target={TargetPlayerId}",
                data.PlayerId, targetPlayerId);
            return true;
        }
        finally
        {
            if (!delivered)
                session.ClearMatchingAssignment(matchingId);
        }
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
        GameSession? session = _getSession(playerId);
        string? requestId = session?.ActiveMatchingRequestId;
        if (MatchingRequestTokens.IsSafeTokenComponent(requestId))
        {
            NotifyMatchingAdmissionFailed(
                new MatchingQueueData
                {
                    PlayerId = playerId,
                    RequestId = requestId!
                },
                matchingId);
        }

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

    private sealed record GameServerAllocation(string PublicHost, int PublicPort);
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

    // Key 3~6은 #320에서 삭제된 세션 owner 경로. 큐 entry 호환을 위해 번호를 유지한다.
    [Key(7)]
    public string RequestId { get; set; } = string.Empty;
}

/// <summary>
///     One player-to-target link in the circular target chain, including spawn data.
/// </summary>
public class RosterChainLink
{
    public byte[] Entry { get; set; } = Array.Empty<byte>();
    public long TargetPlayerId { get; set; }
    public PersonaType Persona { get; set; } = PersonaType.None;
    public AreaType StartArea { get; set; } = AreaType.None;
    public Cell SpawnCell { get; set; } = new(0, 0);

}
