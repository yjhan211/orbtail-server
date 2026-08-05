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
    private const int MatchingTimeoutSeconds = 3;
    private const int BotFillTimeoutSeconds = 30;
    private const int LeavePenaltySeconds = 30;
    private const int MaxLeavePenaltySeconds = 300;
    private const int PenaltyDecayIntervalHours = 24;
    private const int DefaultPlayersPerMatch = 1;
    private const int DefaultGamePlayersPerMatch = 8;
    private const int SpotArenaPlayersPerMatch = 4;

    private static int PlayersPerMatch => IsSoloMapValidation ? 1 : IsTwoPlayerTestMatch ? 2 : DefaultPlayersPerMatch;
    private static int GamePlayersPerMatch => IsSoloMapValidation
        ? DefaultPlayersPerMatch
        : Config.SPOT_ARENA_P0_ENABLED
            ? SpotArenaPlayersPerMatch
            : DefaultGamePlayersPerMatch;

    private static bool IsTwoPlayerTestMatch => Environment.GetEnvironmentVariable("TEST_TWO_PLAYER_MATCH") == "1";
    private static bool IsSoloMapValidation =>
        Environment.GetEnvironmentVariable("SOLO_MAP_VALIDATION") == "1";
    private static JobTitle? ForcedPlayerJob => ParseForcedPlayerJob();

    private static long _botIdCounter; // 遊?PlayerId (?뚯닔)
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

        // 留ㅼ묶 ??대㉧: 1珥덈쭏????泥댄겕
        _matchingTimer = new Timer(OnMatchingTimerTick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _logger.LogInformation("MatchingManager 珥덇린???꾨즺");
    }

    public async Task<ErrorCode> AddToQueue(long playerId, GameSession user)
    {
        try
        {
            int removedCount = await RemovePlayerEntriesFromQueueAsync(playerId);
            if (removedCount > 0)
                _logger.LogInformation("?뚮젅?댁뼱 {PlayerId} 湲곗〈 留ㅼ묶 ???뷀듃由?{Count}媛??뺣━", playerId, removedCount);

            var queueData = new MatchingQueueData
            {
                PlayerId = playerId,
                RequestTime = DateTime.UtcNow,
                UserChannel = user.GetChannelName()
            };

            byte[] serialized = MessagePackSerializer.Serialize(queueData);

            // ?댄깉 ?섎꼸?? ?댄깉 ?잛닔??鍮꾨???異붽? ?湲??쒓컙
            long penaltyDelay = await GetLeavePenaltyDelayAsync(playerId);
            long score = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + penaltyDelay;

            await _cacheHelper.SortedSetAddAsync(MatchingQueueKey, serialized, score);

            if (penaltyDelay > 0)
                _logger.LogInformation("?뚮젅?댁뼱 {PlayerId} 留ㅼ묶 ??異붽? (?댄깉 ?섎꼸??{Penalty}珥?", playerId, penaltyDelay);
            else
                _logger.LogInformation("?뚮젅?댁뼱 {PlayerId} 留ㅼ묶 ??異붽?", playerId);

            return ErrorCode.SUCCESS;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "留ㅼ묶 ??異붽? ?ㅽ뙣: {PlayerId}", playerId);
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
                    _logger.LogInformation("?뚮젅?댁뼱 {PlayerId} 留ㅼ묶 痍⑥냼", playerId);
                    return ErrorCode.SUCCESS;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "留ㅼ묶 entry ??쭅?ы솕 ?ㅽ뙣, ?ㅽ궢");
                    await _cacheHelper.SortedSetRemoveAsync(MatchingQueueKey, entry);
                }
            }

            return ErrorCode.SUCCESS;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "留ㅼ묶 痍⑥냼 ?ㅽ뙣: {PlayerId}", playerId);
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
                _logger.LogError(ex, "留ㅼ묶 entry ??쭅?ы솕 ?ㅽ뙣, ?먯뿉???쒓굅");
                if (await _cacheHelper.SortedSetRemoveAsync(MatchingQueueKey, entry))
                    removedCount++;
            }
        }

        return removedCount;
    }

    /// <summary>
    ///     ??대㉧ 肄쒕갚 ???ъ쭊??諛⑹? ??鍮꾨룞湲?泥섎━ ?꾩엫
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

                // 遺議깊븳 ?몄썝? 遊뉗쑝濡?利됱떆 梨꾩? ??1??利됱떆 留ㅼ묶?먯꽌 ?먭린?먯떊 ?寃?諛⑹?
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

                // ?먰삎 泥댁씤 ?앹꽦: ?뷀뵆 ??A?묪?묬?묭?묮?묨 (?붿궡??= 留덈땲??愿怨?
                var chain = await BuildManittoChain(allGroupEntries.ToArray());
                ApplySurvivorRoyaleSpawnAssignments(matchingId, chain);
                await ApplyTwoPlayerTestTargetOutfitAsync(chain);
                var playerRoster = await BuildPlayerRosterAsync(chain);

                // 遊??뺣낫 Redis ???(game_server?먯꽌 濡쒕뱶). 5???먰삎 泥댁씤 ?뺥빀 ??遊??寃잛? 泥댁씤 ?ㅼ쓬 ?몃뱶.
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

                // ?ㅼ젣 ?뚮젅?댁뼱留?留ㅼ묶 ?깃났 ?⑦궥 ?꾩넚 + ???쒓굅
                foreach (var link in chain)
                {
                    var data = MessagePackSerializer.Deserialize<MatchingQueueData>(link.Entry);
                    if (data.PlayerId < 0) continue; // 遊뉗? ?ㅽ궢

                    try
                    {
                        await ProcessMatchedEntry(link.Entry, matchingId, link.TargetPlayerId,
                            link.TargetJobTitle, link.MyJobTitle, playerRoster, link.SpawnCell);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "留ㅼ묶 entry 泥섎━ ?ㅽ뙣, ?ㅽ궢");
                    }
                    finally
                    {
                        await _cacheHelper.SortedSetRemoveAsync(MatchingQueueKey, link.Entry);
                    }
                }
            }

            // 遊?梨꾩?: 30珥??댁긽 ?湲?以묒씤 ?뚮젅?댁뼱媛 ?덉쑝硫?遊뉗쑝濡?梨꾩?
            await CheckBotFillAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "留ㅼ묶 ??泥섎━ 以??ㅻ쪟 諛쒖깮");
        }
    }

    /// <summary>
    ///     30珥??댁긽 ?湲?以묒씤 ?뚮젅?댁뼱媛 5紐?誘몃쭔?대㈃ 遊뉗쑝濡?梨꾩썙??留ㅼ묶
    /// </summary>
    private async Task CheckBotFillAsync()
    {
        if (IsTwoPlayerTestMatch || IsSoloMapValidation) return;

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long botCutoff = now - BotFillTimeoutSeconds;

        byte[][] longWaitEntries = await _cacheHelper.SortedSetRangeByScoreAsync(
            MatchingQueueKey, double.NegativeInfinity, botCutoff);

        if (longWaitEntries.Length < PlayersPerMatch || longWaitEntries.Length >= GamePlayersPerMatch) return;

        int botsNeeded = GamePlayersPerMatch - longWaitEntries.Length;
        var allEntries = new List<byte[]>(longWaitEntries);

        // 遊??곗씠???앹꽦
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
        _logger.LogInformation("Bot-filled matching: MatchingId={MatchingId}, Real={Real}, Bots={Bot}",
            matchingId, longWaitEntries.Length, botsNeeded);

        var chain = await BuildManittoChain(allEntries.ToArray());
        ApplySurvivorRoyaleSpawnAssignments(matchingId, chain);
        var playerRoster = await BuildPlayerRosterAsync(chain);

        // 遊??뺣낫瑜?Redis?????(game_server?먯꽌 濡쒕뱶). ?먰삎 泥댁씤 ?뺥빀 ??遊??寃잛? 泥댁씤 ?ㅼ쓬 ?몃뱶.
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

        // ?ㅼ젣 ?뚮젅?댁뼱留?留ㅼ묶 ?깃났 ?⑦궥 ?꾩넚
        foreach (var link in chain)
        {
            var data = MessagePackSerializer.Deserialize<MatchingQueueData>(link.Entry);
            if (data.PlayerId < 0) continue; // 遊뉗? ?ㅽ궢

            try
            {
                await ProcessMatchedEntry(link.Entry, matchingId, link.TargetPlayerId,
                    link.TargetJobTitle, link.MyJobTitle, playerRoster, link.SpawnCell);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "遊?梨꾩? 留ㅼ묶 entry 泥섎━ ?ㅽ뙣: {PlayerId}", data.PlayerId);
            }
            finally
            {
                await _cacheHelper.SortedSetRemoveAsync(MatchingQueueKey, link.Entry);
            }
        }
    }

    /// <summary>
    ///     ?먰삎 泥댁씤 ?앹꽦: ?뷀뵆 ??i踰덉㎏ ?뚮젅?댁뼱???寃?= (i+1)%N踰덉㎏ ?뚮젅?댁뼱
    ///     吏곸콉(JobTitle)??臾댁옉??諛곗젙. Redis 吏곸콉 ? 媛뺤젣 吏?뺤씠 ?덉쑝硫??곗꽑 ?ъ슜.
    ///     ?쒖꽦 ???쒖뿰??遊?紐?BR/DC/SC/HE) 泥댁씤 媛뺤젣 ???뷀뵆 ?놁쓬.
    /// </summary>
    private async Task<List<ManittoChainLink>> BuildManittoChain(byte[][] groupEntries)
    {
        if (IsTwoPlayerTestMatch && groupEntries.Length == DefaultGamePlayersPerMatch)
            return BuildTwoPlayerTestManittoChain(groupEntries);


        // ?뷀뵆
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
                    _logger.LogInformation("吏곸콉 ? 媛뺤젣 吏???곸슜: {Jobs}", string.Join(",", jobs));
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
            _logger.LogWarning(ex, "Redis 吏곸콉 ? config ?쎄린 ?ㅽ뙣, 臾댁옉???ъ슜");
            jobs = Enum.GetValues<JobTitle>().Where(j => j != JobTitle.NONE).ToList();
        }

        // 吏곸콉 ?뷀뵆 諛곗젙
        for (int i = jobs.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (jobs[i], jobs[j]) = (jobs[j], jobs[i]);
        }

        // 媛뺤젣 ????몄썝蹂대떎 ?곸쑝硫?臾댁옉??吏곸콉?쇰줈 遺議깅텇 蹂댁땐 (NONE ?쒖쇅, ? 吏곸콉 ?곗꽑 ?좎?)
        if (jobs.Count < entries.Count)
        {
            var fillPool = Enum.GetValues<JobTitle>()
                .Where(j => j != JobTitle.NONE && !jobs.Contains(j))
                .OrderBy(_ => rng.Next())
                .ToList();
            int needed = entries.Count - jobs.Count;
            jobs.AddRange(fillPool.Take(needed));
            _logger.LogInformation("吏곸콉 ? 遺議???臾댁옉?꾨줈 {Needed}媛?蹂댁땐", needed);
        }

        // PlayerId ??쭅?ы솕
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

        _logger.LogInformation("留덈땲??泥댁씤 ?앹꽦: {Chain}",
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

    private void ApplySurvivorRoyaleSpawnAssignments(long matchingId, List<ManittoChainLink> chain)
    {
        if (chain.Count == 0) return;

        var playerIds = chain
            .Select(link => MessagePackSerializer.Deserialize<MatchingQueueData>(link.Entry).PlayerId)
            .ToList();
        var assignments = Config.SPOT_ARENA_P0_ENABLED
            ? SurvivorRoyaleSpawnData.CreateSpotArenaAssignments(matchingId, playerIds)
            : SurvivorRoyaleSpawnData.CreatePhaseRoomAssignments(matchingId, playerIds);

        foreach (var link in chain)
        {
            var playerId = MessagePackSerializer.Deserialize<MatchingQueueData>(link.Entry).PlayerId;
            link.Persona = PersonaType.None;
            link.SpawnCell = Cell.Clone(assignments[playerId]);
            link.StartArea = GameMapData.GetCurrentArea(MapId.School, link.SpawnCell);

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

        _logger.LogInformation("?뚮젅?댁뼱 吏곸콉 媛뺤젣 吏???곸슜: PlayerId={PlayerId}, Job={Job}",
            players[playerIndex].PlayerId, forcedJob.Value);
    }

    private async Task ApplyTwoPlayerTestTargetOutfitAsync(List<ManittoChainLink> chain)
    {
        if (!IsTwoPlayerTestMatch) return;

        var targetOutfitItemIds = new[]
        {
            101000005, // ?섎뒛 諛붾엺癒몃━
            102000005, // 議곗슜??移쒓뎄 ?쇨뎬
            103000003, // ?숆렇? 肉뷀뀒 ?덇꼍
            104000007, // ?ν????섎났 ?곸쓽
            105000007, // ?섎났 諛붿?
            106000004  // 濡쒗띁
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
                _logger.LogWarning("2??留ㅼ묶 ?명삎 蹂듭궗 ?ㅽ뙣: ??踰덉㎏ ?뚮젅?댁뼱 濡쒕뱶 ?ㅽ뙣 ({PlayerId})", targetPlayerId);
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

        _logger.LogInformation("2??留ㅼ묶 ?寃??명삎 怨좎젙: Player2={TargetPlayerId}, Items={Items}",
            targetPlayerId, string.Join(", ", targetOutfitItemIds));
    }

    /// <summary>
    ///     留ㅼ묶 ?좎껌 ?쒖꽌瑜?蹂댁〈?섍린 ?꾪빐 ???뷀듃由щ? RequestTime 湲곗??쇰줈 ?뺣젹?쒕떎.
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

        const int expectedRealPlayerCount = 2;
        int expectedBotCount = DefaultGamePlayersPerMatch - expectedRealPlayerCount;
        if (realPlayers.Count != expectedRealPlayerCount || bots.Count != expectedBotCount)
        {
            _logger.LogWarning(
                "TEST_TWO_PLAYER_MATCH 留ㅼ묶 援ъ꽦 鍮꾩젙??(??{Real}紐?/ 遊?{Bot}紐? ???쇰컲 泥댁씤 ?대갚",
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
            "TEST_TWO_PLAYER_MATCH 泥댁씤 媛뺤젣: {Chain}",
            string.Join(" -> ", players.Select((p, i) => $"{p.PlayerId}({jobs[i]})")) + $" -> {players[0].PlayerId}");

        return chain;
    }

    private async Task<List<PlayerInfo>> BuildPlayerRosterAsync(List<ManittoChainLink> chain)
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
                _logger.LogWarning("留ㅼ묶 roster ?앹꽦 ?ㅽ뙣: PlayerInfo 濡쒕뱶 ?ㅽ뙣 ({PlayerId})", data.PlayerId);
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

    private async Task ProcessMatchedEntry(byte[] entry, long matchingId,
        long targetPlayerId, JobTitle targetJobTitle, JobTitle myJobTitle, List<PlayerInfo> playerRoster,
        Cell spawnCell)
    {
        var data = MessagePackSerializer.Deserialize<MatchingQueueData>(entry);
        _logger.LogInformation("?뚮젅?댁뼱 {DataPlayerId} 泥섎━ 以?.. (?寃? {TargetPlayerId})", data.PlayerId, targetPlayerId);

        var session = _getSession(data.PlayerId);
        if (session?.PlayerInfo == null)
        {
            _logger.LogWarning("?뚮젅?댁뼱 {DataPlayerId} ?몄뀡 ?먮뒗 PlayerInfo媛 null", data.PlayerId);
            return;
        }

        const MapId mapId = MapId.School;
        var spawnPosition = Cell.Clone(spawnCell);
        if (spawnPosition.X == 0 && spawnPosition.Y == 0)
        {
            throw new InvalidOperationException($"Missing Survivor Royale spawn assignment for player {data.PlayerId}.");
        }
        // RedLock?쇰줈 PlayerInfo ?섏젙 蹂댄샇
        await using var playerLock = await PlayerInfo.Lock(_redLock, data.PlayerId);
        var playerInfo = await PlayerInfo.Load(_cacheHelper, data.PlayerId);
        if (playerInfo == null)
        {
            _logger.LogError("?뚮젅?댁뼱 {DataPlayerId} PlayerInfo 濡쒕뱶 ?ㅽ뙣!", data.PlayerId);
            return;
        }

        playerInfo.LastMapId = mapId;
        playerInfo.LastMapSubId = matchingId;
        playerInfo.LastCell = spawnPosition;
        playerInfo.ObjectInfo.MapId = mapId;
        playerInfo.ObjectInfo.MapSubId = matchingId;
        playerInfo.ObjectInfo.Cell = Cell.Clone(spawnPosition);
        playerInfo.ObjectInfo.Position = CellToWorldPosition(mapId, spawnPosition);
        playerInfo.ObjectInfo.Velocity = new Vector3f(0f, 0f, 0f);
        playerInfo.ObjectInfo.MoveTimestamp = DateTime.UtcNow;
        string handoffKey = MatchingHandoffRedisKeys.Key(matchingId);
        await _cacheHelper.HashSetAsync(handoffKey, MatchingHandoffRedisKeys.SpawnField(data.PlayerId),
            MessagePackSerializer.Serialize(spawnPosition));
        await _cacheHelper.KeyExpireAsync(handoffKey, MatchingHandoffRedisKeys.Lifetime);

        // 寃뚯엫?쒕쾭 ?뺣낫
        string gameServerIp = Environment.GetEnvironmentVariable("GAME_SERVER_IP") ?? "127.0.0.1";
        int gameServerPort =
            int.TryParse(Environment.GetEnvironmentVariable("GAME_SERVER_PORT"), out int port) ? port : 9001;

        long gameEndTimestamp = DateTimeOffset.UtcNow.AddMinutes(Config.GAME_DURATION_MINUTES)
            .ToUnixTimeMilliseconds();

        using var packet = PacketMaker.U_TO_C_MATCHING_SUCCESS(
            matchingId, mapId, matchingId, spawnPosition,
            gameServerIp, gameServerPort, gameEndTimestamp,
            targetPlayerId, targetJobTitle, myJobTitle, playerRoster, new List<int>()
        );

        session.Send(packet);
        _logger.LogInformation("?뚮젅?댁뼱 {DataPlayerId} 留ㅼ묶 ?깃났 ?⑦궥 ?꾩넚 (?寃? {TargetPlayerId}, ??吏곸콉: {MyJob}, ?寃?吏곸콉: {TargetJob})",
            data.PlayerId, targetPlayerId, myJobTitle, targetJobTitle);
    }

    private static Vector3f CellToWorldPosition(MapId mapId, Cell cell) =>
        MapCoordinateConverter.CellToWorld(mapId, cell);

    /// <summary>
    ///     ?댄깉 ?섎꼸???湲??쒓컙 議고쉶: ?댄깉 ?잛닔 횞 30珥?(理쒕? 300珥?.
    ///     24?쒓컙 寃쎄낵 ???댄깉 ?잛닔 1 媛먯냼 (?쒓컙 寃쎄낵 媛먯뇿).
    /// </summary>
    private async Task<long> GetLeavePenaltyDelayAsync(long playerId)
    {
        try
        {
            var value = await _cacheHelper.HashGetAsync(LeavePenaltyKey, playerId);
            if (value.IsNullOrEmpty) return 0;

            long leaveCount = BitConverter.ToInt64((byte[])value!);
            if (leaveCount <= 0) return 0;

            // 24?쒓컙 寃쎄낵 媛먯뇿: decayAt ?댄썑 24h媛 吏?ъ쑝硫?leaveCount 1 媛먯냼
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
    ///     24?쒓컙 寃쎄낵留덈떎 ?댄깉 ?잛닔 1 媛먯냼 (諛섎났 ?곸슜).
    ///     decayAt 湲곕줉???놁쑝硫?泥?議고쉶 ?쒖젏?쇰줈 珥덇린??
    /// </summary>
    private async Task<long> ApplyTimeDecayAsync(long playerId, long leaveCount)
    {
        try
        {
            var decayAtValue = await _cacheHelper.HashGetAsync(LeavePenaltyDecayAtKey, playerId);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (decayAtValue.IsNullOrEmpty)
            {
                // 理쒖큹 議고쉶 ??湲곗? ?쒓컖 ?ㅼ젙 (?꾩옱 ?쒓컖)
                await _cacheHelper.HashSetAsync(LeavePenaltyDecayAtKey, playerId, BitConverter.GetBytes(now));
                return leaveCount;
            }

            long decayAt = BitConverter.ToInt64((byte[])decayAtValue!);
            long elapsedSeconds = now - decayAt;
            long decayIntervalSeconds = PenaltyDecayIntervalHours * 3600L;

            if (elapsedSeconds < decayIntervalSeconds) return leaveCount;

            // 寃쎄낵??24h ?⑥쐞 ?잛닔留뚰겮 媛먯냼
            long decayCount = elapsedSeconds / decayIntervalSeconds;
            leaveCount = Math.Max(0, leaveCount - decayCount);

            // ?ㅼ쓬 decayAt 媛깆떊 (寃쎄낵遺??쒖쇅)
            long newDecayAt = decayAt + decayCount * decayIntervalSeconds;

            if (leaveCount <= 0)
            {
                // ?섎꼸???꾩쟾 ?뚮㈇ ??????紐⑤몢 ??젣
                await _cacheHelper.HashDeleteAsync(LeavePenaltyKey, playerId);
                await _cacheHelper.HashDeleteAsync(LeavePenaltyDecayAtKey, playerId);
                _logger.LogInformation("?댄깉 ?섎꼸??媛먯뇿 ?뚮㈇: PlayerId={PlayerId}", playerId);
            }
            else
            {
                await _cacheHelper.HashSetAsync(LeavePenaltyKey, playerId, BitConverter.GetBytes(leaveCount));
                await _cacheHelper.HashSetAsync(LeavePenaltyDecayAtKey, playerId, BitConverter.GetBytes(newDecayAt));
                _logger.LogInformation("?댄깉 ?섎꼸??媛먯뇿: PlayerId={PlayerId}, ?⑥??잛닔={Count}", playerId, leaveCount);
            }

            return leaveCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "?댄깉 ?섎꼸??媛먯뇿 泥섎━ ?ㅽ뙣: PlayerId={PlayerId}", playerId);
            return leaveCount;
        }
    }

    /// <summary>
    ///     ?뺤긽 寃뚯엫 ?꾨즺 ???댄깉 ?잛닔 1 媛먯냼. game_server?먯꽌 NATS濡??몄텧.
    /// </summary>
    public async Task RecordLeaveAsync(long playerId)
    {
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

            _logger.LogInformation("?뺤긽 ?꾨즺 ?섎꼸??媛먯냼: PlayerId={PlayerId}, ?⑥??잛닔={Count}", playerId, leaveCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "?뺤긽 ?꾨즺 ?섎꼸??媛먯냼 ?ㅽ뙣: PlayerId={PlayerId}", playerId);
        }
    }

    public void Dispose()
    {
        _matchingTimer.Dispose();
        _logger.LogInformation("MatchingManager 醫낅즺");
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
///     ?먰삎 泥댁씤????留곹겕: ?뚮젅?댁뼱 ???寃?愿怨?+ 吏곸콉
/// </summary>
public class ManittoChainLink
{
    public byte[] Entry { get; set; } = Array.Empty<byte>();
    public long TargetPlayerId { get; set; }
    public JobTitle MyJobTitle { get; set; }
    public JobTitle TargetJobTitle { get; set; }
    public PersonaType Persona { get; set; } = PersonaType.None;
    public AreaType StartArea { get; set; } = AreaType.None;
    public Cell SpawnCell { get; set; } = new(0, 0);

}
