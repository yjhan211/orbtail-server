using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.interfaces;
using network.packets;
using user_server.network;

namespace user_server.services;

public class MatchingManager : IMatchingManager
{
    private const string MatchingQueueKey = "matching_queue";
    private const string MatchingIdKey = "matching_id";
    private const int MatchingTimeoutSeconds = 5;
    private const int PlayersPerMatch = 2; // 매칭 인원수
    private readonly ICacheHelper _cacheHelper;
    private readonly Func<long, GameSession?> _getSession;
    private readonly ILogger _logger;
    private readonly Timer _matchingTimer;

    public MatchingManager(ILogger logger, ICacheHelper cacheHelper, Func<long, GameSession?> getSession)
    {
        _logger = logger;
        _cacheHelper = cacheHelper;
        _getSession = getSession;

        // 매칭 타이머: 1초마다 큐 체크
        _matchingTimer = new Timer(ProcessMatchingQueue, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _logger.LogInformation("MatchingManager 초기화 완료");
    }

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

            byte[] serialized = MessagePackSerializer.Serialize(queueData);
            long score = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            await _cacheHelper.SortedSetAddAsync(MatchingQueueKey, serialized, score);

            _logger.LogInformation("플레이어 {PlayerId} 매칭 큐 추가", playerId);
            return ErrorCode.SUCCESS;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"매칭 큐 추가 실패: {playerId}");
            return ErrorCode.FATAL;
        }
    }

    public async Task<ErrorCode> CancelMatching(long playerId)
    {
        try
        {
            // Sorted Set에서 해당 플레이어 제거
            byte[][] allEntries = await _cacheHelper.SortedSetRangeByScoreAsync(MatchingQueueKey);

            foreach (byte[] entry in allEntries)
            {
                var data = MessagePackSerializer.Deserialize<MatchingQueueData>(entry);
                if (data.PlayerId != playerId) continue;
                await _cacheHelper.SortedSetRemoveAsync(MatchingQueueKey, entry);
                _logger.LogInformation("플레이어 {PlayerId} 매칭 취소", playerId);
                return ErrorCode.SUCCESS;
            }

            return ErrorCode.SUCCESS;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "매칭 취소 실패: {PlayerId}", playerId);
            return ErrorCode.FATAL;
        }
    }

    private async void ProcessMatchingQueue(object? state)
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

            int matchableCount = allEntries.Length / PlayersPerMatch * PlayersPerMatch;
            if (matchableCount < PlayersPerMatch) return;

            byte[][] entriesToMatch = allEntries.Take(matchableCount).ToArray();
            for (int i = 0; i < entriesToMatch.Length; i += PlayersPerMatch)
            {
                byte[][] groupEntries = entriesToMatch.Skip(i).Take(PlayersPerMatch).ToArray();
                long matchingId = await _cacheHelper.StringIncrementAsync(MatchingIdKey);

                _logger.LogInformation("매칭 성공! matching_id: {MatchingId}, 참가자: {GroupEntriesLength}명", matchingId,
                    groupEntries.Length);
                foreach (byte[] entry in groupEntries)
                {
                    var data = MessagePackSerializer.Deserialize<MatchingQueueData>(entry);
                    _logger.LogInformation("플레이어 {DataPlayerId} 처리 중...", data.PlayerId);

                    var session = _getSession(data.PlayerId);
                    _logger.LogInformation(
                        "세션 조회 결과: PlayerId={PlayerId}, Session={SessionExists}, SessionPlayerId={SessionPlayerId}",
                        data.PlayerId, session != null ? "있음" : "없음", session?.PlayerId);

                    if (session?.PlayerInfo != null)
                    {
                        _logger.LogInformation("플레이어 {DataPlayerId} 매칭 성공 알림 전송 시작", data.PlayerId);

                        // 매칭 맵 정보
                        const MapId mapId = MapId.School;

                        // 맵별 초기 스폰 위치 가져오기 (CSV의 init_cell_x, init_cell_y)
                        var mapInfo = GameMapData.GetMapInfo(mapId);
                        _logger.LogInformation("MapInfo - InitCell.position: ({X}, {Y}, {Z})",
                            mapInfo.InitCell.position.x, mapInfo.InitCell.position.y, mapInfo.InitCell.position.z);
                        var (spawnPosition, _) = mapInfo.GetInitialPosition();
                        _logger.LogInformation("플레이어 {DataPlayerId} 스폰 위치: {SpawnPosition}", data.PlayerId,
                            spawnPosition);

                        // PlayerInfo LastCell 업데이트
                        var playerInfo = await PlayerInfo.Load(_cacheHelper, data.PlayerId);
                        if (playerInfo != null)
                        {
                            playerInfo.LastMapId = mapId;
                            playerInfo.LastMapSubId = matchingId;
                            playerInfo.LastCell = spawnPosition;
                            await playerInfo.Save(_cacheHelper);
                            _logger.LogInformation("플레이어 {DataPlayerId} LastCell 업데이트 완료: {SpawnPosition}",
                                data.PlayerId, spawnPosition);
                        }
                        else
                        {
                            _logger.LogError("플레이어 {DataPlayerId} PlayerInfo 로드 실패!", data.PlayerId);
                        }

                        // 게임서버 정보 (환경변수에서 가져오기, 없으면 기본값)
                        string gameServerIp = Environment.GetEnvironmentVariable("GAME_SERVER_IP") ?? "127.0.0.1";
                        int gameServerPort =
                            int.TryParse(Environment.GetEnvironmentVariable("GAME_SERVER_PORT"), out int port)
                                ? port
                                : 9001;

                        // 게임 종료 시간 계산
                        long gameEndTimestamp = DateTimeOffset.UtcNow.AddMinutes(Config.GAME_DURATION_MINUTES)
                            .ToUnixTimeMilliseconds();

                        // 매칭 성공 패킷 전송
                        _logger.LogInformation(
                            "플레이어 {DataPlayerId} 매칭 성공 패킷 생성 중 (MatchingId={MatchingId}, SpawnPosition={SpawnPosition})",
                            data.PlayerId, matchingId, spawnPosition);

                        using var packet = PacketMaker.U_TO_C_MATCHING_SUCCESS(
                            matchingId,
                            mapId,
                            matchingId,
                            spawnPosition,
                            gameServerIp,
                            gameServerPort,
                            gameEndTimestamp
                        );

                        _logger.LogInformation("플레이어 {DataPlayerId} 패킷 전송 중... (Size={Size})", data.PlayerId,
                            packet.ToBytes().Length);
                        session.Send(packet);
                        _logger.LogInformation("플레이어 {DataPlayerId} 매칭 성공 패킷 전송 완료", data.PlayerId);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "플레이어 {DataPlayerId} 세션 또는 PlayerInfo가 null (session: {SessionExists}, PlayerInfo: {PlayerInfoExists})",
                            data.PlayerId, session != null, session?.PlayerInfo != null);
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
        _matchingTimer.Dispose();
        _logger.LogInformation("MatchingManager 종료");
    }
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
}
