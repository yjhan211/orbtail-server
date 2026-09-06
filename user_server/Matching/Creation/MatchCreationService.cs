using user_server.matching;
using user_server.matching.queue;
using Microsoft.Extensions.Logging;
using network.common.data.models;
using network.infrastructure.redis;

namespace user_server.matching.creation;

/// <summary>
///     매치 생성 경로의 출처. 로그·롤백 메시지 구분에만 쓴다.
/// </summary>
internal enum MatchCreationOrigin
{
    Queue,
    BotFill
}

/// <summary>
///     1초 timer가 호출하는 매칭 pass 한 번의 본체. 큐에서 3초 이상 기다린 entry를 PlayersPerMatch 단위로 묶고,
///     끝으로 30초 이상 기다린 미달 그룹을 봇으로 채운다. 두 경로는 <see cref="CreateMatchAsync" /> 하나로 합쳐졌으며,
///     reservation 획득 → matchingId 발급 → reservation commit → 로스터 조립 → 매치 manifest → 사람별 성공 전달 →
///     <c>admission_ready</c> → watchdog 순서와, 실패 시 pending→canceled CAS 뒤 handoff 삭제·실패 통지·reservation 롤백을 지킨다.
///     봇 PlayerId는 process-wide 음수 카운터에서 발급한다. 그 외 프로세스 상태는 없다.
/// </summary>
internal sealed class MatchCreationService(
    IRedisOperations redisOperations,
    MatchingQueue queue,
    MatchingReservationCoordinator reservations,
    MatchRosterBuilder rosterBuilder,
    IMatchEntryService handoff,
    IGameServerAllocator gameServers,
    DevMatchOverrides overrides,
    CancellationToken shutdownToken,
    ILogger logger)
{
    internal const string MatchingIdKey = "matching_id";
    internal const int MatchingTimeoutSeconds = 3;
    internal const int BotFillTimeoutSeconds = 30;

    private static long _botIdCounter; // 봇은 음수 PlayerId를 쓴다.

    /// <summary>
    ///     pass 한 번: 정규 그룹 매칭 뒤 봇 채움. 예외는 여기서 삼키고 로그로 남긴다 (다음 tick에 재시도).
    /// </summary>
    public async Task RunAsync()
    {
        try
        {
            await RunQueueGroupsAsync();

            if (shutdownToken.IsCancellationRequested)
                return;
            await RunBotFillAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while processing the matching queue");
        }
    }

    /// <summary>
    ///     3초 이상 기다린 entry를 요청 시각 순으로 PlayersPerMatch씩 묶어 매치를 만든다.
    /// </summary>
    private async Task RunQueueGroupsAsync()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        MatchingQueueEntry[] waiting = await queue.ReadWaitingEntriesAsync(now - MatchingTimeoutSeconds);
        if (waiting.Length == 0) return;
        waiting = MatchingQueue.SortByRequestTime(waiting);

        int playersPerMatch = overrides.PlayersPerMatch;
        int matchableCount = waiting.Length / playersPerMatch * playersPerMatch;
        if (matchableCount < playersPerMatch) return;

        for (int i = 0; i < matchableCount; i += playersPerMatch)
        {
            if (shutdownToken.IsCancellationRequested)
                return;

            MatchingQueueEntry[] groupEntries = waiting.Skip(i).Take(playersPerMatch).ToArray();
            // 빈자리는 봇으로 채운다. 솔로 검증은 즉시 1인 자족 매치를 만든다.
            int botsNeeded = Math.Max(0, overrides.GamePlayersPerMatch - groupEntries.Length);
            await CreateMatchAsync(groupEntries, botsNeeded, MatchCreationOrigin.Queue);
        }
    }

    /// <summary>
    ///     30초 이상 기다린 미달 그룹(PlayersPerMatch 이상, 정원 미만) 전원을 하나의 봇 채움 매치로 만든다.
    ///     정규 경로와 달리 정렬하지 않고 Redis score 순서를 그대로 쓴다.
    /// </summary>
    private async Task RunBotFillAsync()
    {
        if (shutdownToken.IsCancellationRequested || !overrides.AllowsBotFill) return;

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        MatchingQueueEntry[] longWaitEntries = await queue.ReadWaitingEntriesAsync(now - BotFillTimeoutSeconds);

        int gamePlayersPerMatch = overrides.GamePlayersPerMatch;
        if (longWaitEntries.Length < overrides.PlayersPerMatch || longWaitEntries.Length >= gamePlayersPerMatch) return;

        await CreateMatchAsync(longWaitEntries, gamePlayersPerMatch - longWaitEntries.Length, MatchCreationOrigin.BotFill);
    }

    /// <summary>
    ///     그룹 하나를 매치로 확정한다. 받아 줄 Game Server가 없거나 reservation을 얻지 못하면 건너뛰고(false),
    ///     전달이 불완전하면 되돌린다(false). matchingId 발급 뒤 예외는 롤백 후 호출자에게 전파돼 이번 pass를 끝낸다.
    /// </summary>
    internal async Task<bool> CreateMatchAsync(
        MatchingQueueEntry[] groupEntries,
        int botsNeeded,
        MatchCreationOrigin origin)
    {
        // 배정은 부작용이 없으므로 reservation보다 먼저 — 노드가 없으면 큐를 그대로 두고 다음 pass에 다시 본다.
        GameServerAllocation? gameServer = await gameServers.TryAllocateAsync();
        if (gameServer == null)
            return false;

        MatchingReservationLease? reservationLease = await reservations.TryAcquireAsync(groupEntries);
        if (reservationLease == null)
        {
            logger.LogInformation("Matching group skipped because another worker owns a player reservation: Origin={Origin}", origin);
            return false;
        }

        bool matchCommitted = false;
        long matchingId = 0;
        int deliveredPlayerCount = 0;
        int expectedHumanCount = 0;
        MatchingQueueEntry[] batchPlayers = groupEntries
            .Where(entry => entry.IsHuman)
            .DistinctBy(entry => entry.PlayerId)
            .ToArray();
        try
        {
            matchingId = await redisOperations.StringIncrementAsync(MatchingIdKey);
            await reservations.CommitAsync(reservationLease, matchingId);

            var allGroupEntries = new List<MatchingQueueEntry>(groupEntries);
            for (int b = 0; b < botsNeeded; b++)
                allGroupEntries.Add(MatchingQueueEntry.CreateBot(Interlocked.Decrement(ref _botIdCounter)));

            logger.LogInformation(
                "Bot-filled matching: MatchingId={MatchingId}, Real={Real}, Bots={Bot}, Origin={Origin}, GameServer={NodeId}",
                matchingId, groupEntries.Length, botsNeeded, origin, gameServer.NodeId);

            await overrides.ApplyTwoPlayerTestOutfitAsync(allGroupEntries);
            List<PlayerInfo> playerRoster = await rosterBuilder.BuildPlayerRosterAsync(allGroupEntries);
            MatchManifest manifest = MatchRosterBuilder.BuildManifest(allGroupEntries, overrides.MatchMode);
            expectedHumanCount = manifest.HumanPlayerIds.Count;

            // Game Server는 이 manifest로 봇 수·입장 기대 인원·스폰을 정한다.
            await handoff.StoreMatchManifestAsync(matchingId, manifest);

            // 사람에게 통지하고 commit된 큐 entry를 제거한다.
            foreach (MatchingQueueEntry entry in allGroupEntries)
            {
                if (entry.IsBot) continue;

                bool delivered = false;
                try
                {
                    delivered = await handoff.DeliverMatchingSuccessAsync(
                        entry, matchingId, playerRoster, gameServer);
                    if (delivered)
                        deliveredPlayerCount++;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Failed to commit matching entry; removing it from this match: PlayerId={PlayerId}, Origin={Origin}",
                        entry.PlayerId, origin);
                }
                finally
                {
                    if (!delivered)
                        await reservations.ReleaseActiveBestEffortAsync(entry.PlayerId, matchingId);
                    await queue.RemoveEntryAsync(entry);
                }
            }

            // manifest의 사람 목록은 전원 계약이다. 살아 있는 인간 전원이 성공 패킷을 받아들인 뒤에만 admission
            // 마커를 쓴다. Game Server는 이 마커가 있어야 ticket을 받으므로 부분 전달이 멈춘 매치를 만들 수 없다.
            bool deliveryComplete = deliveredPlayerCount == expectedHumanCount;
            if (deliveryComplete)
            {
                await handoff.MarkHandoffReadyAsync(matchingId);
                if (!handoff.StartAdmissionWatchdog(matchingId, batchPlayers.Select(entry => entry.PlayerId).ToArray()))
                    throw new OperationCanceledException(
                        "Matching admission watchdog could not start during shutdown.");
                matchCommitted = true;
            }
            if (!deliveryComplete)
            {
                logger.LogWarning(
                    "Matching rolled back because delivery was incomplete: MatchingId={MatchingId}, Delivered={Delivered}, Expected={Expected}, Origin={Origin}",
                    matchingId,
                    deliveredPlayerCount,
                    expectedHumanCount,
                    origin);
            }
        }
        finally
        {
            if (!matchCommitted)
            {
                // matchingId 발급 전에 실패했으면 handoff·입장 상태가 아직 없으므로 reservation만 되돌린다.
                if (matchingId <= 0)
                {
                    await reservations.RollbackAsync(reservationLease);
                }
                else if (await handoff.TryCancelAdmissionForRollbackAsync(matchingId))
                {
                    await handoff.DeleteHandoffBestEffortAsync(matchingId);
                    await handoff.NotifyBatchFailedAsync(batchPlayers, matchingId);
                    await reservations.RollbackAsync(reservationLease);
                }
            }
        }

        return matchCommitted;
    }
}
