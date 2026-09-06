using user_server.matching.queue;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.infrastructure.redis;

namespace user_server.matching.creation;

/// <summary>
///     매칭 대기열에서 요청들을 모아 매치를 만든다.
///     일반 매칭과 장기 대기자의 부족한 인원을 봇으로 채우는 매칭을 처리한다.
///     GameServer 배정 → 플레이어 예약 → 매치 번호 발급 → 구성 정보 저장 → 매칭 결과 전달 순으로 진행한다.
///     사람 목록과 필요한 봇 수를 전달하며, 실제 봇 생성과 최종 참가자 구성은 GameServer가 담당한다.
///     결과 전달 후 입장 대기를 시작하고, 생성 과정이 실패하면 예약과 입장 관련 상태를 정리한다.
/// </summary>
internal sealed class MatchCreationService(
    IRedisOperations redisOperations,
    MatchingQueue queue,
    MatchingReservationService reservations,
    IMatchEntryService handoff,
    IGameServerAllocator gameServers,
    DevMatchOverrides overrides,
    ILogger logger,
    CancellationToken shutdownToken)
{
    private const int MatchingTimeoutSeconds = 3;
    private const int BotFillTimeoutSeconds = 30;

    public async Task RunAsync()
    {
        try
        {
            await RunQueueGroupsAsync();
            if (shutdownToken.IsCancellationRequested)
            {
                return;
            }
            await RunBotFillAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while processing the matching queue");
        }
    }

    private async Task RunQueueGroupsAsync()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var waiting = await queue.ReadWaitingRequestsAsync(now - MatchingTimeoutSeconds);
        if (waiting.Length == 0) return;
        waiting = MatchingQueue.SortByRequestTime(waiting);

        int playersPerMatch = overrides.PlayersPerMatch;
        int matchableCount = waiting.Length / playersPerMatch * playersPerMatch;
        if (matchableCount < playersPerMatch) return;

        for (int i = 0; i < matchableCount; i += playersPerMatch)
        {
            if (shutdownToken.IsCancellationRequested)
                return;

            var groupRequests = waiting.Skip(i).Take(playersPerMatch).ToArray();
            // 빈자리는 봇으로 채운다. 솔로 검증은 즉시 1인 자족 매치를 만든다.
            int botsNeeded = Math.Max(0, overrides.GamePlayersPerMatch - groupRequests.Length);
            await CreateMatchAsync(groupRequests, botsNeeded);
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
        MatchingQueueData[] longWaitingRequests = await queue.ReadWaitingRequestsAsync(now - BotFillTimeoutSeconds);

        int gamePlayersPerMatch = overrides.GamePlayersPerMatch;
        if (longWaitingRequests.Length < overrides.PlayersPerMatch || longWaitingRequests.Length >= gamePlayersPerMatch) return;

        await CreateMatchAsync(longWaitingRequests, gamePlayersPerMatch - longWaitingRequests.Length);
    }

    /// <summary>
    ///     그룹 하나를 매치로 확정한다. 받아 줄 Game Server가 없거나 reservation을 얻지 못하면 건너뛰고(false),
    ///     전달이 불완전하면 되돌린다(false). matchingId 발급 뒤 예외는 롤백 후 호출자에게 전파돼 이번 pass를 끝낸다.
    /// </summary>
    internal async Task<bool> CreateMatchAsync(
        MatchingQueueData[] groupRequests,
        int botsNeeded)
    {
        // 배정은 부작용이 없으므로 reservation보다 먼저 — 노드가 없으면 큐를 그대로 두고 다음 pass에 다시 본다.
        GameServerAllocation? gameServer = await gameServers.TryAllocateAsync();
        if (gameServer == null)
            return false;

        MatchingReservationLease? reservationLease = await reservations.TryAcquireAsync(groupRequests);
        if (reservationLease == null)
        {
            logger.LogInformation("Matching group skipped because another worker owns a player reservation");
            return false;
        }

        bool matchCommitted = false;
        long matchingId = 0;
        int deliveredPlayerCount = 0;
        int expectedHumanCount = 0;
        MatchingQueueData[] batchPlayers = groupRequests
            .Where(request => request.PlayerId > 0)
            .DistinctBy(request => request.PlayerId)
            .ToArray();
        try
        {
            matchingId = await redisOperations.StringIncrementAsync(MatchingRedisKeys.MatchingIdKey);
            await reservations.CommitAsync(reservationLease, matchingId);


            logger.LogInformation(
                "Matching created: MatchingId={MatchingId}, Real={Real}, Bots={Bot}, GameServer={NodeId}",
                matchingId, groupRequests.Length, botsNeeded, gameServer.NodeId);

            await overrides.ApplyTwoPlayerTestOutfitAsync(batchPlayers);
            var manifest = new MatchManifest
            {
                HumanPlayerIds = batchPlayers.Select(request => request.PlayerId).ToList(),
                BotCount = botsNeeded,
                Mode = overrides.MatchMode
            };
            expectedHumanCount = manifest.HumanPlayerIds.Count;

            // Game Server는 이 manifest로 봇 수·입장 기대 인원·스폰을 정한다.
            await handoff.StoreMatchManifestAsync(matchingId, manifest);

            // 사람에게 통지하고 commit된 큐 entry를 제거한다.
            foreach (MatchingQueueData request in batchPlayers)
            {

                bool delivered = false;
                try
                {
                    delivered = await handoff.DeliverMatchingSuccessAsync(request, matchingId, gameServer);
                    if (delivered)
                        deliveredPlayerCount++;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Failed to commit matching request; removing it from this match: PlayerId={PlayerId}",
                        request.PlayerId);
                }
                finally
                {
                    if (!delivered)
                        await reservations.ReleaseMatchingReservationAsync(request.PlayerId, matchingId);
                    await queue.RemoveRequestAsync(request);
                }
            }

            // manifest의 사람 목록은 전원 계약이다. 살아 있는 인간 전원이 성공 패킷을 받아들인 뒤에만 admission
            // 마커를 쓴다. Game Server는 이 마커가 있어야 ticket을 받으므로 부분 전달이 멈춘 매치를 만들 수 없다.
            bool deliveryComplete = deliveredPlayerCount == expectedHumanCount;
            if (deliveryComplete)
            {
                await handoff.MarkHandoffReadyAsync(matchingId);
                if (!handoff.StartAdmissionWatchdog(matchingId, batchPlayers.Select(request => request.PlayerId).ToArray()))
                    throw new OperationCanceledException(
                        "Matching admission watchdog could not start during shutdown.");
                matchCommitted = true;
            }
            if (!deliveryComplete)
            {
                logger.LogWarning(
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
