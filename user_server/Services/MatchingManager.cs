using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;
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
    private const int DefaultPlayersPerMatch = 1; // 매칭 트리거 최소 인원 (#22 디버그 — 1인 트리거)
    private const int DefaultGamePlayersPerMatch = 5; // 실제 게임 인원 (#22 디버그 — 1인 + 봇 4명, 각 층별 1명)

    /// <summary>매칭 트리거 최소 인원. DEMO_MODE 활성 시 1명만으로 트리거(즉시 봇 4명 채움).</summary>
    private static int PlayersPerMatch => IsTwoPlayerTestMatch ? 2 : DemoMode.IsActive ? 1 : DefaultPlayersPerMatch;

    /// <summary>실제 게임 인원. TEST_TWO_PLAYER_MATCH 시 실플레이어 2명 + 봇 3명.</summary>
    private static int GamePlayersPerMatch =>
        IsTwoPlayerTestMatch ? DefaultGamePlayersPerMatch : DemoMode.IsActive ? DemoMode.MatchPlayerCount : DefaultGamePlayersPerMatch;

    private static bool IsTwoPlayerTestMatch => Environment.GetEnvironmentVariable("TEST_TWO_PLAYER_MATCH") == "1";
    private static JobTitle? ForcedPlayerJob => ParseForcedPlayerJob();
    private static AreaType? ForcedPlayerSpawnArea => ParseForcedPlayerSpawnArea();
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
            int removedCount = await RemovePlayerEntriesFromQueueAsync(playerId);
            if (removedCount > 0)
                _logger.LogInformation("플레이어 {PlayerId} 기존 매칭 큐 엔트리 {Count}개 정리", playerId, removedCount);

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
                _logger.LogError(ex, "매칭 entry 역직렬화 실패, 큐에서 제거");
                if (await _cacheHelper.SortedSetRemoveAsync(MatchingQueueKey, entry))
                    removedCount++;
            }
        }

        return removedCount;
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

            allEntries = SortEntriesByRequestTime(allEntries);

            int matchableCount = allEntries.Length / PlayersPerMatch * PlayersPerMatch;
            if (matchableCount < PlayersPerMatch) return;

            byte[][] entriesToMatch = allEntries.Take(matchableCount).ToArray();
            for (int i = 0; i < entriesToMatch.Length; i += PlayersPerMatch)
            {
                byte[][] groupEntries = entriesToMatch.Skip(i).Take(PlayersPerMatch).ToArray();
                long matchingId = await _cacheHelper.StringIncrementAsync(MatchingIdKey);

                // 부족한 인원은 봇으로 즉시 채움 — 1인 즉시 매칭에서 자기자신 타겟 방지
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

                _logger.LogInformation("매칭 성공! matching_id: {MatchingId}, 실제 {Real}명 + 봇 {Bot}명",
                    matchingId, groupEntries.Length, botsNeeded);

                // 원형 체인 생성: 셔플 후 A→B→C→D→E→A (화살표 = 마니또 관계)
                var chain = await BuildManittoChain(allGroupEntries.ToArray());
                await ApplyTwoPlayerTestTargetOutfitAsync(chain);

                // 봇 정보 Redis 저장 (game_server에서 로드). 5인 원형 체인 정합 — 봇 타겟은 체인 다음 노드.
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

                // 실제 플레이어만 매칭 성공 패킷 전송 + 큐 제거
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
                        _logger.LogError(ex, "매칭 entry 처리 실패, 스킵");
                    }
                    finally
                    {
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
        if (IsTwoPlayerTestMatch) return;

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long botCutoff = now - BotFillTimeoutSeconds;

        byte[][] longWaitEntries = await _cacheHelper.SortedSetRangeByScoreAsync(
            MatchingQueueKey, double.NegativeInfinity, botCutoff);

        if (longWaitEntries.Length == 0 || longWaitEntries.Length >= GamePlayersPerMatch) return;

        int botsNeeded = GamePlayersPerMatch - longWaitEntries.Length;
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

        // 봇 정보를 Redis에 저장 (game_server에서 로드). 원형 체인 정합 — 봇 타겟은 체인 다음 노드.
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
    ///     DEMO_MODE 활성 시 시연자+봇4명(BR/DC/SC/HE) 체인 강제 — 셔플 없음.
    /// </summary>
    private async Task<List<ManittoChainLink>> BuildManittoChain(byte[][] groupEntries)
    {
        if (IsTwoPlayerTestMatch && groupEntries.Length == DefaultGamePlayersPerMatch)
            return BuildTwoPlayerTestManittoChain(groupEntries);

        if (DemoMode.IsActive && groupEntries.Length == DemoMode.MatchPlayerCount)
            return BuildDemoManittoChain(groupEntries);

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

        // 강제 풀이 인원보다 적으면 무작위 직책으로 부족분 보충 (NONE 제외, 풀 직책 우선 유지)
        if (jobs.Count < entries.Count)
        {
            var fillPool = Enum.GetValues<JobTitle>()
                .Where(j => j != JobTitle.NONE && !jobs.Contains(j))
                .OrderBy(_ => rng.Next())
                .ToList();
            int needed = entries.Count - jobs.Count;
            jobs.AddRange(fillPool.Take(needed));
            _logger.LogInformation("직책 풀 부족 — 무작위로 {Needed}개 보충", needed);
        }

        // PlayerId 역직렬화
        var players = entries.Select(e => MessagePackSerializer.Deserialize<MatchingQueueData>(e)).ToList();
        ApplyForcedPlayerJob(players, jobs);

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

    private static AreaType? ParseForcedPlayerSpawnArea()
    {
        string? raw = Environment.GetEnvironmentVariable("FORCE_PLAYER_SPAWN_AREA");
        if (string.IsNullOrWhiteSpace(raw)) return null;

        if (Enum.TryParse(raw, true, out AreaType byName) && byName != AreaType.None)
            return byName;

        return short.TryParse(raw, out short byValue) && Enum.IsDefined(typeof(AreaType), byValue)
            ? (AreaType)byValue
            : null;
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

        _logger.LogInformation("플레이어 직책 강제 지정 적용: PlayerId={PlayerId}, Job={Job}",
            players[playerIndex].PlayerId, forcedJob.Value);
    }

    private async Task ApplyTwoPlayerTestTargetOutfitAsync(List<ManittoChainLink> chain)
    {
        if (!IsTwoPlayerTestMatch) return;

        var targetOutfitItemIds = new[]
        {
            101000005, // 하늘 바람머리
            102000005, // 조용한 친구 얼굴
            103000003, // 동그란 뿔테 안경
            104000007, // 넥타이 하복 상의
            105000007, // 하복 바지
            106000004  // 로퍼
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
                _logger.LogWarning("2인 매칭 외형 복사 실패: 두 번째 플레이어 로드 실패 ({PlayerId})", targetPlayerId);
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

        _logger.LogInformation("2인 매칭 타겟 외형 고정: Player2={TargetPlayerId}, Items={Items}",
            targetPlayerId, string.Join(", ", targetOutfitItemIds));
    }

    /// <summary>
    ///     매칭 신청 순서를 보존하기 위해 큐 엔트리를 RequestTime 기준으로 정렬한다.
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

    private List<ManittoChainLink> BuildTwoPlayerTestManittoChain(byte[][] groupEntries)
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

        if (realPlayers.Count != 2 || bots.Count != 3)
        {
            _logger.LogWarning(
                "TEST_TWO_PLAYER_MATCH 매칭 구성 비정상 (실 {Real}명 / 봇 {Bot}명) — 일반 체인 폴백",
                realPlayers.Count, bots.Count);
            return BuildDemoFallback(groupEntries);
        }

        var ordered = realPlayers.Concat(bots).ToList();
        var players = ordered.Select(x => x.Data).ToList();
        var jobs = new[]
        {
            DemoMode.PlayerJob,
            JobTitle.DISCIPLINE_MEMBER,
            JobTitle.BROADCAST_MEMBER,
            JobTitle.SCIENCE_MEMBER,
            JobTitle.HEALTH_MEMBER
        };

        var chain = new List<ManittoChainLink>();
        for (int i = 0; i < ordered.Count; i++)
        {
            int targetIndex = (i + 1) % ordered.Count;
            chain.Add(new ManittoChainLink
            {
                Entry = ordered[i].Entry,
                TargetPlayerId = players[targetIndex].PlayerId,
                MyJobTitle = jobs[i],
                TargetJobTitle = jobs[targetIndex]
            });
        }

        _logger.LogInformation(
            "TEST_TWO_PLAYER_MATCH 체인 강제: {Chain}",
            string.Join(" -> ", players.Select((p, i) => $"{p.PlayerId}({jobs[i]})")) + $" -> {players[0].PlayerId}");

        return chain;
    }

    /// <summary>
    ///     시연 모드 체인 강제 생성. 시연자(실 PlayerId)는 인덱스 1에,
    ///     봇 4명은 인덱스 0/2/3/4에 BR/DC/SC/HE 순서로 고정 배치.
    ///     체인: BR → 시연자 → DC → SC → HE → BR.
    ///     셔플하지 않음 — 결정론 시드 + 영상 비트 정합성 보장.
    /// </summary>
    private List<ManittoChainLink> BuildDemoManittoChain(byte[][] groupEntries)
    {
        var realEntries = new List<byte[]>();
        var botEntries = new List<byte[]>();

        foreach (var entry in groupEntries)
        {
            var data = MessagePackSerializer.Deserialize<MatchingQueueData>(entry);
            if (data.PlayerId >= 0) realEntries.Add(entry);
            else botEntries.Add(entry);
        }

        if (realEntries.Count != 1 || botEntries.Count != 4)
        {
            _logger.LogWarning(
                "DEMO_MODE 매칭 구성 비정상 (실 {Real}명 / 봇 {Bot}명) — 일반 체인 폴백",
                realEntries.Count, botEntries.Count);
            return BuildDemoFallback(groupEntries);
        }

        // 인덱스 0/2/3/4 = 봇, 인덱스 1 = 시연자
        var ordered = new byte[DemoMode.MatchPlayerCount][];
        ordered[DemoMode.PlayerChainIndex] = realEntries[0];
        int botCursor = 0;
        for (int i = 0; i < DemoMode.MatchPlayerCount; i++)
        {
            if (i == DemoMode.PlayerChainIndex) continue;
            ordered[i] = botEntries[botCursor++];
        }

        var players = ordered.Select(e => MessagePackSerializer.Deserialize<MatchingQueueData>(e)).ToList();
        var jobs = DemoMode.ChainJobOrder;

        var chain = new List<ManittoChainLink>();
        for (int i = 0; i < ordered.Length; i++)
        {
            int targetIndex = (i + 1) % ordered.Length;
            chain.Add(new ManittoChainLink
            {
                Entry = ordered[i],
                TargetPlayerId = players[targetIndex].PlayerId,
                MyJobTitle = jobs[i],
                TargetJobTitle = jobs[targetIndex]
            });
        }

        _logger.LogInformation(
            "DEMO_MODE 체인 강제: {Chain}",
            string.Join(" → ", players.Select((p, i) => $"{p.PlayerId}({jobs[i]})")) + $" → {players[0].PlayerId}");

        return chain;
    }

    /// <summary>
    ///     DEMO_MODE 매칭 구성이 비정상일 때 (실 0명 등) 일반 체인 알고리즘으로 폴백.
    /// </summary>
    private List<ManittoChainLink> BuildDemoFallback(byte[][] groupEntries)
    {
        var entries = groupEntries.ToList();
        var rng = new Random(DemoMode.Seed);
        for (int i = entries.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (entries[i], entries[j]) = (entries[j], entries[i]);
        }

        var jobs = DemoMode.ChainJobOrder.ToList();
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
        var forcedSpawnArea = ForcedPlayerSpawnArea;
        if (forcedSpawnArea.HasValue)
        {
            spawnPosition = GameMapData.GetAreaSpawnCell(mapId, forcedSpawnArea.Value);
            _logger.LogInformation("플레이어 시작 위치 강제 지정 적용: PlayerId={PlayerId}, Area={Area}, Cell=({X},{Y})",
                data.PlayerId, forcedSpawnArea.Value, spawnPosition.X, spawnPosition.Y);
        }

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
        if (DemoMode.IsActive) return 0;

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
