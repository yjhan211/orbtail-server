using System.Collections.Concurrent;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;
using network.interfaces;
using network.packets;
using user_server.infrastructure.network;

namespace user_server.application.services;

public class MatchingManager
{
    private readonly ILogger _logger;
    private readonly ICacheHelper _cacheHelper;
    private readonly INatsClient _natsClient;
    private readonly Func<long, GameSession?> _getSession;
    private readonly Timer _matchingTimer;
    private const string MatchingQueueKey = "matching_queue";
    private const string MatchingIdKey = "matching_id";
    private const int MatchingTimeoutSeconds = 5;
    private const int PlayersPerMatch = 2; // 매칭 인원수

    public MatchingManager(ILogger logger, ICacheHelper cacheHelper, INatsClient natsClient, Func<long, GameSession?> getSession)
    {
        _logger = logger;
        _cacheHelper = cacheHelper;
        _natsClient = natsClient;
        _getSession = getSession;

        // 매칭 타이머: 1초마다 큐 체크
        _matchingTimer = new Timer(ProcessMatchingQueue, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _logger.LogInformation("MatchingManager 초기화 완료");
    }

    /// <summary>
    /// 매칭 요청 추가
    /// </summary>
    public async Task<ErrorCode> AddToQueue(long playerId, GameSession user)
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
            var allEntries = await _cacheHelper.SortedSetRangeByScoreAsync(
                MatchingQueueKey,
                double.NegativeInfinity,
                cutoffTime
            );

            if (allEntries.Length == 0)
            {
                return;
            }

            // 2인 매칭이 가능한 만큼 처리
            int matchableCount = (allEntries.Length / PlayersPerMatch) * PlayersPerMatch;
            if (matchableCount < PlayersPerMatch)
            {
                return; // 2명 미만이면 매칭 대기
            }

            // 매칭 가능한 플레이어들만 처리
            var entriesToMatch = allEntries.Take(matchableCount).ToArray();

            // PlayersPerMatch명씩 그룹화하여 매칭
            for (int i = 0; i < entriesToMatch.Length; i += PlayersPerMatch)
            {
                var groupEntries = entriesToMatch.Skip(i).Take(PlayersPerMatch).ToArray();

                // 매칭 ID 생성
                var matchingId = await _cacheHelper.StringIncrementAsync(MatchingIdKey);

                _logger.LogInformation($"매칭 성공! matching_id: {matchingId}, 참가자: {groupEntries.Length}명");

                // 각 플레이어에게 매칭 성공 알림
                foreach (var entry in groupEntries)
                {
                    var data = MessagePackSerializer.Deserialize<MatchingQueueData>(entry);
                    _logger.LogInformation($"플레이어 {data.PlayerId} 처리 중...");

                    var session = _getSession(data.PlayerId);
                    _logger.LogInformation($"세션 조회 결과: {(session != null ? "있음" : "없음")}");

                    if (session?.Player != null)
                    {
                        _logger.LogInformation($"플레이어 {data.PlayerId} 매칭 성공 알림 전송");

                        // 매칭 맵 정보
                        var mapId = MapId.School;
                        var mapSubId = matchingId; // 매칭 ID를 인스턴스 ID로 사용
                        var mapInfo = GameMapData.GetMapInfo(mapId);
                        var (spawnPosition, _) = mapInfo.GetInitialPosition();

                        // PlayerInfo LastCell 업데이트
                        var playerInfo = await PlayerInfo.Load(_cacheHelper, data.PlayerId);
                        if (playerInfo != null)
                        {
                            playerInfo.LastMapId = mapId;
                            playerInfo.LastMapSubId = mapSubId;
                            playerInfo.LastCell = spawnPosition;
                            await playerInfo.Save(_cacheHelper);
                            _logger.LogInformation($"플레이어 {data.PlayerId} LastCell 업데이트: {spawnPosition}");
                        }

                        // 게임서버 정보 (TODO: 추후 동적 할당)
                        var gameServerIp = "127.0.0.1";
                        var gameServerPort = 9001;

                        // 게임 종료 시간 계산 (15분 후)
                        var gameEndTimestamp = DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeMilliseconds();

                        // 매칭 성공 패킷 전송
                        using var packet = PacketMaker.U_TO_C_MATCHING_SUCCESS(
                            matchingId,
                            mapId,
                            mapSubId,
                            spawnPosition,
                            gameServerIp,
                            gameServerPort,
                            gameEndTimestamp
                        );
                        session.Send(packet);

                        _logger.LogInformation($"플레이어 {data.PlayerId} 매칭 성공 패킷 전송 완료");
                    }
                    else
                    {
                        _logger.LogWarning($"플레이어 {data.PlayerId} 세션 또는 Player가 null (session: {session != null}, Player: {session?.Player != null})");
                    }

                    // 큐에서 제거
                    await _cacheHelper.SortedSetRemoveAsync(MatchingQueueKey, entry);
                }
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
