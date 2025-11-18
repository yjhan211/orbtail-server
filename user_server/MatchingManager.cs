using System.Collections.Concurrent;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;
using network.interfaces;
using network.packets;

namespace user_server;

public class MatchingManager
{
    private readonly ILogger _logger;
    private readonly ICacheHelper _cacheHelper;
    private readonly INatsClient _natsClient;
    private readonly Timer _matchingTimer;
    private const string MatchingQueueKey = "matching_queue";
    private const string MatchingIdKey = "matching_id";
    private const int MatchingTimeoutSeconds = 5;

    public MatchingManager(ILogger logger, ICacheHelper cacheHelper, INatsClient natsClient)
    {
        _logger = logger;
        _cacheHelper = cacheHelper;
        _natsClient = natsClient;

        // 매칭 타이머: 1초마다 큐 체크
        _matchingTimer = new Timer(ProcessMatchingQueue, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _logger.LogInformation("MatchingManager 초기화 완료");
    }

    /// <summary>
    /// 매칭 요청 추가
    /// </summary>
    public async Task<ErrorCode> AddToQueue(long playerId, GameUser user)
    {
        try
        {
            var queueData = new MatchingQueueData
            {
                PlayerId = playerId,
                RequestTime = DateTime.UtcNow,
                UserChannel = user.GetChannelName()
            };

            var serialized = MessagePackSerializer.Serialize(queueData);
            var score = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Redis Sorted Set에 추가 (score = 요청 시간)
            await _cacheHelper.SortedSetAddAsync(MatchingQueueKey, serialized, score);

            _logger.LogInformation($"플레이어 {playerId} 매칭 큐 추가");
            return ErrorCode.SUCCESS;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"매칭 큐 추가 실패: {playerId}");
            return ErrorCode.FATAL;
        }
    }

    /// <summary>
    /// 매칭 취소
    /// </summary>
    public async Task<ErrorCode> CancelMatching(long playerId)
    {
        try
        {
            // Sorted Set에서 해당 플레이어 제거
            var allEntries = await _cacheHelper.SortedSetRangeByScoreAsync(MatchingQueueKey);

            foreach (var entry in allEntries)
            {
                var data = MessagePackSerializer.Deserialize<MatchingQueueData>(entry);
                if (data.PlayerId == playerId)
                {
                    await _cacheHelper.SortedSetRemoveAsync(MatchingQueueKey, entry);
                    _logger.LogInformation($"플레이어 {playerId} 매칭 취소");
                    return ErrorCode.SUCCESS;
                }
            }

            return ErrorCode.SUCCESS;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"매칭 취소 실패: {playerId}");
            return ErrorCode.FATAL;
        }
    }

    /// <summary>
    /// 매칭 큐 처리 (타이머 콜백)
    /// </summary>
    private async void ProcessMatchingQueue(object? state)
    {
        try
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var cutoffTime = now - MatchingTimeoutSeconds;

            // 5초 이상 지난 매칭 요청 가져오기
            var entries = await _cacheHelper.SortedSetRangeByScoreAsync(
                MatchingQueueKey,
                double.NegativeInfinity,
                cutoffTime
            );

            if (entries.Length == 0)
            {
                return;
            }

            // 매칭 ID 생성
            var matchingId = await _cacheHelper.StringIncrementAsync(MatchingIdKey);

            _logger.LogInformation($"매칭 성공! matching_id: {matchingId}, 참가자: {entries.Length}명");

            // 각 플레이어에게 매칭 성공 알림
            var mapSubId = matchingId;
            var spawnPosition = new Cell(50, 50); // TODO: 적절한 스폰 위치로 변경

            foreach (var entry in entries)
            {
                var data = MessagePackSerializer.Deserialize<MatchingQueueData>(entry);

                // 매칭 성공 패킷 전송
                using var packet = PacketMaker.U_TO_C_MATCHING_SUCCESS(matchingId, MapId.School, mapSubId, spawnPosition);
                _natsClient.Publish(data.UserChannel, packet.ToBytes());

                // 큐에서 제거
                await _cacheHelper.SortedSetRemoveAsync(MatchingQueueKey, entry);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "매칭 큐 처리 중 오류 발생");
        }
    }

    public void Dispose()
    {
        _matchingTimer?.Dispose();
        _logger.LogInformation("MatchingManager 종료");
    }
}

[MessagePackObject]
public class MatchingQueueData
{
    [Key(0)] public long PlayerId { get; set; }
    [Key(1)] public DateTime RequestTime { get; set; }
    [Key(2)] public string UserChannel { get; set; }
}
