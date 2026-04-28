using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.interfaces;
using network.packets;
using user_server.network;

namespace user_server.services;

/// <summary>
///     game_server MatchingConfigService와 공유하는 Redis 키 상수.
///     값 변경 시 양쪽 동시 수정 필요.
/// </summary>
internal static class MatchingConfigRedisKeys
{
    internal const string Key = "matching_config";
    internal const string JobPoolField = "job_pool";
}

public class MatchingManager : IMatchingManager
{
    private const string MatchingQueueKey = "matching_queue";
    private const string MatchingIdKey = "matching_id";
    private const string BotInfoKeyPrefix = "matching_bots"; // Redis Hash: field=matchingId
    private const string LeavePenaltyKey = "leave_penalties"; // Redis Hash: field=playerId, value=int64 count
    private const string LeavePenaltyDecayAtKey = "leave_penalty_decay_at"; // Redis Hash: field=playerId, value=int64 unix timestamp
    private const int MatchingTimeoutSeconds = 3;
    private const int BotFillTimeoutSeconds = 30; // 봇 채움 대기 시간
    private const int LeavePenaltySeconds = 30; // 이탈 1회당 추가 대기 시간
    private const int MaxLeavePenaltySeconds = 300; // 최대 페널티 대기 시간 (5분)
    private const int PenaltyDecayIntervalHours = 24; // 24시간 경과 시 이탈 횟수 1 감소
    private const int PlayersPerMatch = 1; // 매칭 인원수 (테스트용)
    private static long _botIdCounter; // 봇 PlayerId (음수)
    private readonly ICacheHelper _cacheHelper;
    private readonly Func<long, GameSession?> _getSession;
    private readonly ILogger _logger;
    private readonly Timer _matchingTimer;
    private readonly IRedLockFactory _redLock;
    private int _isProcessing;

    public MatchingManager(ILogger logger, ICacheHelper cacheHelper, IRedLockFactory redLock,
        Func<long, GameSession?> getSession)
    {
        _logger = logger;
        _cacheHelper = cacheHelper;
        _redLock = redLock;
        _getSession = getSession;

        // 매칭 타이머: 1초마다 큐 체크
        _matchingTimer = new Timer(OnMatchingTimerTick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
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

            // 이탈 페널티: 이탈 횟수에 비례한 추가 대기 시간
            long penaltyDelay = await GetLeavePenaltyDelayAsync(playerId);
            long score = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + penaltyDelay;

            await _cacheHelper.SortedSetAddAsync(MatchingQueueKey, serialized, score);

            if (penaltyDelay > 0)
                _logger.LogInformation("플레이어 {PlayerId} 매칭 큐 추가 (이탈 페널티 {Penalty}초)", playerId, penaltyDelay);
            else
                _logger.LogInformation("플레이어 {PlayerId} 매칭 큐 추가", playerId);

            return ErrorCode.SUCCESS;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "매칭 큐 추가 실패: {PlayerId}", playerId);
            return ErrorCode.SERVER_INTERNAL_ERROR;
        }
    }

    public async Task<ErrorCode> CancelMatching(long playerId)
    {
        try
        {
            byte[][] allEntries = await _cacheHelper.SortedSetRangeByScoreAsync(MatchingQueueKey);

            foreach (byte[] entry in allEntries)
            {
                try
                {
                    var data = MessagePackSerializer.Deserialize<MatchingQueueData>(entry);
                    if (data.PlayerId != playerId) continue;
                    await _cacheHelper.SortedSetRemoveAsync(MatchingQueueKey, entry);
                    _logger.LogInformation("플레이어 {PlayerId} 매칭 취소", playerId);
                    return ErrorCode.SUCCESS;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "매칭 entry 역직렬화 실패, 스킵");
                    await _cacheHelper.SortedSetRemoveAsync(MatchingQueueKey, entry);
                }
            }

            return ErrorCode.SUCCESS;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "매칭 취소 실패: {PlayerId}", playerId);
            return ErrorCode.SERVER_INTERNAL_ERROR;
        }
    }

    /// <summary>
    ///     타이머 콜백 — 재진입 방지 후 비동기 처리 위임
    /// </summary>
    private void OnMatchingTimerTick(object? state)
    {
        if (Interlocked.CompareExchange(ref _isProcessing, 1, 0) != 0) return;
        _ = ProcessMatchingQueueAsync()
            .ContinueWith(_ => Interlocked.Exchange(ref _isProcessing, 0));
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

            int matchableCount = allEntries.Length / PlayersPerMatch * PlayersPerMatch;
            if (matchableCount < PlayersPerMatch) return;

            byte[][] entriesToMatch = allEntries.Take(matchableCount).ToArray();
            for (int i = 0; i < entriesToMatch.Length; i += PlayersPerMatch)
            {
                byte[][] groupEntries = entriesToMatch.Skip(i).Take(PlayersPerMatch).ToArray();
                long matchingId = await _cacheHelper.StringIncrementAsync(MatchingIdKey);

                _logger.LogInformation("매칭 성공! matching_id: {MatchingId}, 참가자: {GroupEntriesLength}명", matchingId,
                    groupEntries.Length);

                // 원형 체인 생성: 셔플 후 A→B→C→D→E→A (화살표 = 마니또 관계)
                var chain = await BuildManittoChain(groupEntries);

                foreach (var link in chain)
                {
                    try
                    {
                        await ProcessMatchedEntry(link.Entry, matchingId, link.TargetPlayerId,
                            link.TargetJobTitle, link.MyJobTitle);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "매칭 entry 처리 실패, 스킵");
                    }
                    finally
                    {
                        // 성공/실패 관계없이 큐에서 제거
                        await _cacheHelper.SortedSetRemoveAsync(MatchingQueueKey, link.Entry);
                    }
                }
            }

            // 봇 채움: 30초 이상 대기 중인 플레이어가 있으면 봇으로 채움
            await CheckBotFillAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "매칭 큐 처리 중 오류 발생");
        }
    }

    /// <summary>
    ///     30초 이상 대기 중인 플레이어가 5명 미만이면 봇으로 채워서 매칭
    /// </summary>
    private async Task CheckBotFillAsync()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long botCutoff = now - BotFillTimeoutSeconds;

        byte[][] longWaitEntries = await _cacheHelper.SortedSetRangeByScoreAsync(
            MatchingQueueKey, double.NegativeInfinity, botCutoff);

        if (longWaitEntries.Length == 0 || longWaitEntries.Length >= PlayersPerMatch) return;

        int botsNeeded = PlayersPerMatch - longWaitEntries.Length;
        var allEntries = new List<byte[]>(longWaitEntries);

        // 봇 데이터 생성
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

        long matchingId = await _cacheHelper.StringIncrementAsync(MatchingIdKey);
        _logger.LogInformation("봇 채움 매칭: MatchingId={MatchingId}, 실제 {Real}명 + 봇 {Bot}명",
            matchingId, longWaitEntries.Length, botsNeeded);

        var chain = await BuildManittoChain(allEntries.ToArray());

        // 봇 정보를 Redis에 저장 (game_server에서 로드)
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
                TargetJobTitle = link.TargetJobTitle
            });
        }

        if (botInfoList.Count > 0)
        {
            byte[] serialized = MessagePackSerializer.Serialize(botInfoList);
            await _cacheHelper.HashSetAsync(BotInfoKeyPrefix, matchingId, serialized);
        }

        // 실제 플레이어만 매칭 성공 패킷 전송
        foreach (var link in chain)
        {
            var data = MessagePackSerializer.Deserialize<MatchingQueueData>(link.Entry);
            if (data.PlayerId < 0) continue; // 봇은 스킵

            try
            {
                await ProcessMatchedEntry(link.Entry, matchingId, link.TargetPlayerId,
                    link.TargetJobTitle, link.MyJobTitle);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "봇 채움 매칭 entry 처리 실패: {PlayerId}", data.PlayerId);
            }
            finally
            {
                await _cacheHelper.SortedSetRemoveAsync(MatchingQueueKey, link.Entry);
            }
        }
    }

    /// <summary>
    ///     원형 체인 생성: 셔플 후 i번째 플레이어의 타겟 = (i+1)%N번째 플레이어
    ///     직책(JobTitle)도 무작위 배정. Redis 직책 풀 강제 지정이 있으면 우선 사용.
    /// </summary>
    private async Task<List<ManittoChainLink>> BuildManittoChain(byte[][] groupEntries)
    {
        // 셔플
        var entries = groupEntries.ToList();
        var rng = Random.Shared;
        for (int i = entries.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (entries[i], entries[j]) = (entries[j], entries[i]);
        }

        // 직책 풀 결정 — Redis 강제 지정 우선, 없으면 무작위
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
                    _logger.LogInformation("직책 풀 강제 지정 적용: {Jobs}", string.Join(",", jobs));
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
            _logger.LogWarning(ex, "Redis 직책 풀 config 읽기 실패, 무작위 사용");
            jobs = Enum.GetValues<JobTitle>().Where(j => j != JobTitle.NONE).ToList();
        }

        // 직책 셔플 배정
        for (int i = jobs.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (jobs[i], jobs[j]) = (jobs[j], jobs[i]);
        }

        // PlayerId 역직렬화
        var players = entries.Select(e => MessagePackSerializer.Deserialize<MatchingQueueData>(e)).ToList();

        var chain = new List<ManittoChainLink>();
        for (int i = 0; i < entries.Count; i++)
        {
            int targetIndex = (i + 1) % entries.Count;
            chain.Add(new ManittoChainLink
            {
                Entry = entries[i],
                TargetPlayerId = players[targetIndex].PlayerId,
                MyJobTitle = jobs[i],
                TargetJobTitle = jobs[targetIndex]
            });
        }

        _logger.LogInformation("마니또 체인 생성: {Chain}",
            string.Join(" → ", players.Select((p, i) => $"{p.PlayerId}({jobs[i]})")) + $" → {players[0].PlayerId}");

        return chain;
    }

    private async Task ProcessMatchedEntry(byte[] entry, long matchingId,
        long targetPlayerId, JobTitle targetJobTitle, JobTitle myJobTitle)
    {
        var data = MessagePackSerializer.Deserialize<MatchingQueueData>(entry);
        _logger.LogInformation("플레이어 {DataPlayerId} 처리 중... (타겟: {TargetPlayerId})", data.PlayerId, targetPlayerId);

        var session = _getSession(data.PlayerId);
        if (session?.PlayerInfo == null)
        {
            _logger.LogWarning("플레이어 {DataPlayerId} 세션 또는 PlayerInfo가 null", data.PlayerId);
            return;
        }

        const MapId mapId = MapId.School;
        var mapInfo = GameMapData.GetMapInfo(mapId);
        var (spawnPosition, _) = mapInfo.GetInitialPosition();

        // RedLock으로 PlayerInfo 수정 보호
        await using var playerLock = await PlayerInfo.Lock(_redLock, data.PlayerId);
        var playerInfo = await PlayerInfo.Load(_cacheHelper, data.PlayerId);
        if (playerInfo == null)
        {
            _logger.LogError("플레이어 {DataPlayerId} PlayerInfo 로드 실패!", data.PlayerId);
            return;
        }

        playerInfo.LastMapId = mapId;
        playerInfo.LastMapSubId = matchingId;
        playerInfo.LastCell = spawnPosition;
        await playerInfo.Save(_cacheHelper);

        // 게임서버 정보
        string gameServerIp = Environment.GetEnvironmentVariable("GAME_SERVER_IP") ?? "127.0.0.1";
        int gameServerPort =
            int.TryParse(Environment.GetEnvironmentVariable("GAME_SERVER_PORT"), out int port) ? port : 9001;

        long gameEndTimestamp = DateTimeOffset.UtcNow.AddMinutes(Config.GAME_DURATION_MINUTES)
            .ToUnixTimeMilliseconds();

        using var packet = PacketMaker.U_TO_C_MATCHING_SUCCESS(
            matchingId, mapId, matchingId, spawnPosition,
            gameServerIp, gameServerPort, gameEndTimestamp,
            targetPlayerId, targetJobTitle, myJobTitle
        );

        session.Send(packet);
        _logger.LogInformation("플레이어 {DataPlayerId} 매칭 성공 패킷 전송 (타겟: {TargetPlayerId}, 내 직책: {MyJob}, 타겟 직책: {TargetJob})",
            data.PlayerId, targetPlayerId, myJobTitle, targetJobTitle);
    }

    /// <summary>
    ///     이탈 페널티 대기 시간 조회: 이탈 횟수 × 30초 (최대 300초).
    ///     24시간 경과 시 이탈 횟수 1 감소 (시간 경과 감쇠).
    /// </summary>
    private async Task<long> GetLeavePenaltyDelayAsync(long playerId)
    {
        try
        {
            var value = await _cacheHelper.HashGetAsync(LeavePenaltyKey, playerId);
            if (value.IsNullOrEmpty) return 0;

            long leaveCount = BitConverter.ToInt64((byte[])value!);
            if (leaveCount <= 0) return 0;

            // 24시간 경과 감쇠: decayAt 이후 24h가 지났으면 leaveCount 1 감소
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
    ///     24시간 경과마다 이탈 횟수 1 감소 (반복 적용).
    ///     decayAt 기록이 없으면 첫 조회 시점으로 초기화.
    /// </summary>
    private async Task<long> ApplyTimeDecayAsync(long playerId, long leaveCount)
    {
        try
        {
            var decayAtValue = await _cacheHelper.HashGetAsync(LeavePenaltyDecayAtKey, playerId);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (decayAtValue.IsNullOrEmpty)
            {
                // 최초 조회 시 기준 시각 설정 (현재 시각)
                await _cacheHelper.HashSetAsync(LeavePenaltyDecayAtKey, playerId, BitConverter.GetBytes(now));
                return leaveCount;
            }

            long decayAt = BitConverter.ToInt64((byte[])decayAtValue!);
            long elapsedSeconds = now - decayAt;
            long decayIntervalSeconds = PenaltyDecayIntervalHours * 3600L;

            if (elapsedSeconds < decayIntervalSeconds) return leaveCount;

            // 경과된 24h 단위 횟수만큼 감소
            long decayCount = elapsedSeconds / decayIntervalSeconds;
            leaveCount = Math.Max(0, leaveCount - decayCount);

            // 다음 decayAt 갱신 (경과분 제외)
            long newDecayAt = decayAt + decayCount * decayIntervalSeconds;

            if (leaveCount <= 0)
            {
                // 페널티 완전 소멸 → 두 키 모두 삭제
                await _cacheHelper.HashDeleteAsync(LeavePenaltyKey, playerId);
                await _cacheHelper.HashDeleteAsync(LeavePenaltyDecayAtKey, playerId);
                _logger.LogInformation("이탈 페널티 감쇠 소멸: PlayerId={PlayerId}", playerId);
            }
            else
            {
                await _cacheHelper.HashSetAsync(LeavePenaltyKey, playerId, BitConverter.GetBytes(leaveCount));
                await _cacheHelper.HashSetAsync(LeavePenaltyDecayAtKey, playerId, BitConverter.GetBytes(newDecayAt));
                _logger.LogInformation("이탈 페널티 감쇠: PlayerId={PlayerId}, 남은횟수={Count}", playerId, leaveCount);
            }

            return leaveCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "이탈 페널티 감쇠 처리 실패: PlayerId={PlayerId}", playerId);
            return leaveCount;
        }
    }

    /// <summary>
    ///     정상 게임 완료 시 이탈 횟수 1 감소. game_server에서 NATS로 호출.
    /// </summary>
    public async Task RecordGameCompletionAsync(long playerId)
    {
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

            _logger.LogInformation("정상 완료 페널티 감소: PlayerId={PlayerId}, 남은횟수={Count}", playerId, leaveCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "정상 완료 페널티 감소 실패: PlayerId={PlayerId}", playerId);
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

/// <summary>
///     원형 체인의 한 링크: 플레이어 → 타겟 관계 + 직책
/// </summary>
public class ManittoChainLink
{
    public byte[] Entry { get; set; } = Array.Empty<byte>();
    public long TargetPlayerId { get; set; }
    public JobTitle MyJobTitle { get; set; }
    public JobTitle TargetJobTitle { get; set; }
}
