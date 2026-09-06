using user_server.matching.queue;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;
using network.infrastructure.redis;

namespace user_server.matching.creation;

/// <summary>
///     매칭 대기열에서 요청들을 모아 매치를 만든다.
///     5초 이상 기다린 요청을 정원 단위로 묶고, 부족한 인원은 봇 수로 지정한다.
///     GameServer 배정 → 플레이어 예약 → 매치 번호 발급 → 구성 정보 저장 → 매칭 결과 전달 순으로 진행한다.
///     사람 목록과 필요한 봇 수를 전달하며, 실제 봇 생성과 최종 참가자 구성은 GameServer가 담당한다.
///     결과 전달 후 입장 대기를 시작하고, 생성 과정이 실패하면 예약과 입장 관련 상태를 정리한다.
/// </summary>
internal sealed class MatchCreationService(
    IRedisOperations redisOperations,
    MatchingQueue matchingQueue,
    MatchingReservationService matchingReservationService,
    IMatchEntryService matchEntryService,
    IGameServerAllocator gameServerAllocator,
    bool soloMapValidation,
    ILogger logger,
    CancellationToken shutdownToken)
{
    private const int MatchingTimeoutSeconds = 5;

    public async Task RunAsync()
    {
        try
        {
            await RunQueueGroupsAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while processing the matching queue");
        }
    }

    private async Task RunQueueGroupsAsync()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var waiting = await matchingQueue.ReadWaitingRequestsAsync(now - MatchingTimeoutSeconds);
        if (waiting.Length == 0)
        {
            return;
        }

        waiting = MatchingQueue.SortByRequestTime(waiting);
        int playersPerMatch = soloMapValidation ? 1 : Config.SWARM_PLAYERS_PER_MATCH;

        for (int i = 0; i < waiting.Length; i += playersPerMatch)
        {
            if (shutdownToken.IsCancellationRequested)
            {
                return;
            }

            var groupRequests = waiting.Skip(i).Take(playersPerMatch).ToArray();
            int botsNeeded = Math.Max(0, playersPerMatch - groupRequests.Length);
            await CreateMatchAsync(groupRequests, botsNeeded);
        }
    }

    internal async Task<bool> CreateMatchAsync(MatchingQueueData[] groupRequests, int botsNeeded)
    {
        var gameServer = await gameServerAllocator.TryAllocateAsync();
        if (gameServer == null)
        {
            return false;
        }

        var reservationLease = await matchingReservationService.TryAcquireAsync(groupRequests);
        if (reservationLease == null)
        {
            logger.LogInformation("Matching group skipped because another worker owns a player reservation");
            return false;
        }

        bool matchCommitted = false;
        long matchingId = 0;
        int deliveredPlayerCount = 0;
        var batchPlayers = groupRequests
            .Where(request => request.PlayerId > 0)
            .DistinctBy(request => request.PlayerId)
            .ToArray();
        try
        {
            matchingId = await redisOperations.StringIncrementAsync(MatchingRedisKeys.MatchingIdKey);
            await matchingReservationService.CommitAsync(reservationLease, matchingId);

            logger.LogInformation("Matching created: MatchingId={MatchingId}, Real={Real}, Bots={Bot}, GameServer={NodeId}",
                matchingId, groupRequests.Length, botsNeeded, gameServer.NodeId);

            var manifest = new MatchManifest
            {
                HumanPlayerIds = batchPlayers.Select(request => request.PlayerId).ToList(),
                BotCount = botsNeeded,
                Mode = soloMapValidation ? MatchMode.SoloMapValidation : MatchMode.Normal
            };

            int expectedHumanCount = manifest.HumanPlayerIds.Count;
            await matchEntryService.StoreMatchManifestAsync(matchingId, manifest);
            foreach (var request in batchPlayers)
            {

                bool delivered = false;
                try
                {
                    delivered = await matchEntryService.DeliverMatchingSuccessAsync(request, matchingId, gameServer);
                    if (delivered)
                    {
                        deliveredPlayerCount++;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to commit matching request; removing it from this match: PlayerId={PlayerId}",
                        request.PlayerId);
                }
                finally
                {
                    if (!delivered)
                    {
                        await matchingReservationService.ReleaseMatchingReservationAsync(request.PlayerId, matchingId);
                    }
                    await matchingQueue.RemoveRequestAsync(request);
                }
            }

            bool deliveryComplete = deliveredPlayerCount == expectedHumanCount;
            if (deliveryComplete)
            {
                await matchEntryService.MarkEntryReadyAsync(matchingId);
                if (!matchEntryService.StartEntryWatchdog(matchingId,
                        batchPlayers.Select(request => request.PlayerId).ToArray()))
                {
                    throw new OperationCanceledException("Matching entry watchdog could not start during shutdown.");
                }
                matchCommitted = true;
            }
            else
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
                if (matchingId <= 0)
                {
                    await matchingReservationService.RollbackAsync(reservationLease);
                }
                else if (await matchEntryService.TryCancelEntryForRollbackAsync(matchingId))
                {
                    await matchEntryService.DeleteHandoffBestEffortAsync(matchingId);
                    await matchEntryService.NotifyBatchFailedAsync(batchPlayers, matchingId);
                    await matchingReservationService.RollbackAsync(reservationLease);
                }
            }
        }

        return matchCommitted;
    }
}
