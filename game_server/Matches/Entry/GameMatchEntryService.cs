using game_server;
using game_server.matches.logging;
using game_server.matches.results;
using game_server.network;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.gameentry;
using network.helpers;
using network.infrastructure.redis;

namespace game_server.matches.entry;

/// <summary>
///     현재 노드에 발급된 입장 티켓을 소비하고 manifest·예약·준비 마커를 확인해 사람·봇·스폰 구성을 매치당 한 번 확정한다.
///     Redis 조회는 매치별 비동기 초기화 잠금으로 직렬화하고, 메모리 상태 반영은 매치 잠금 안에서 처리한다.
    ///     최초 구성 확정 시 문 상태를 초기화하고 매치 로그와 봇의 초기 구역·스폰을 기록한다.
///     연결 인증과 초기 패킷 전송은 세션이 맡으며, 이 서비스는 특정 연결을 보관하지 않는다.
/// </summary>
internal sealed class GameMatchEntryService(
    IRedisOperations RedisOperations,
    MatchRuntimeStore _matchRuntimes,
    GameServerDevOptions _devOptions,
    ILogger Logger,
    GameEntryTicketService ticketService,
    GameServerNodeOptions nodeOptions,
    GameEventLogManager eventLogs)
{
    private readonly GameEntryStateCommitter _entryStateCommitter = new(RedisOperations, Logger);

    /// <summary>입장할 매치를 찾거나 생성한다. 세션은 반환된 런타임을 입장 후에도 보관한다.</summary>
    public MatchRuntime GetOrCreateMatch(long matchingId) => _matchRuntimes.GetOrCreate(matchingId);

    /// <summary>티켓을 한 번 소비하고 현재 GameServer 노드에 배정된 입장인지 확인한다.</summary>
    public Task<GameEntryContext?> ConsumeTicketAsync(string? ticket) =>
        ticketService.ConsumeAsync(ticket, nodeOptions.NodeId);

    public Task CommitAsync(long matchingId, long playerId, IReadOnlyCollection<long> humanPlayerIds) =>
        _entryStateCommitter.CommitAsync(matchingId, playerId, humanPlayerIds);


    /// <summary>
    ///     매치당 한 번 manifest(사람 ID·봇 수)를 읽고 봇 ID·스폰·최종 명단을 확정한다.
    ///     이후 세션은 런타임에 세워진 구성을 그대로 쓴다. 입장 마커·사람 reservation 확인은 세션마다 다시 한다.
    /// </summary>
    public async Task PrepareMatchAsync(long matchingId, MapId mapId, MatchRuntime runtime)
    {
        var initializationLock = runtime.EntryInitializationLock;
        await initializationLock.WaitAsync();
        try
        {
            MatchManifest manifest = await ReadMatchManifestAsync(matchingId);
            await WaitForMatchingEntryReadyAsync(matchingId, manifest.HumanPlayerIds);

            using (runtime.Enter())
            {
                if (runtime.IsEnded)
                    throw new OperationCanceledException("Match became terminal during game entry.");
                if (runtime.IsSetupComplete)
                    return;
            }
            List<long> humanPlayerIds = manifest.HumanPlayerIds.Distinct().ToList();
            List<long> botPlayerIds = MatchRosterBuilder.CreateBotIds(manifest);
            MatchMode mode = manifest.Mode;

            IReadOnlyDictionary<long, Cell> spawnCells =
                MatchSpawnPlanner.Plan(
                    matchingId, mapId, humanPlayerIds.Concat(botPlayerIds), _devOptions.CrossfireSandbox);
            if (botPlayerIds.Count > 0)
            {
                using (runtime.Enter())
                {
                    if (runtime.IsEnded)
                    {
                        throw new OperationCanceledException("Match became terminal during game entry.");
                    }
                    runtime.Bots.RegisterBots(matchingId, mapId, botPlayerIds, spawnCells);
                }
            }

            var roster = await new MatchRosterBuilder(RedisOperations, Logger).BuildAsync(humanPlayerIds,
                botPlayerIds.Select(id => _matchRuntimes.GetOrThrow(matchingId).Bots.SynthesizePlayerInfo(matchingId, id)
                    ?? throw new InvalidOperationException($"Bot {id} was not initialized.")));

            using (runtime.Enter())
            {
                if (runtime.IsEnded)
                {
                    throw new OperationCanceledException("Match became terminal during game entry.");
                }
                foreach (var participant in roster)
                {
                    runtime.Roster.RegisterEntry(new RosterEntry { PlayerId = participant.PlayerId });
                    runtime.Roster.UpdatePlayerProfile(participant.PlayerId, participant.Name, participant.WearItemIdList);
                }
                runtime.Doors.Initialize();
                InitializeMatchLog(runtime);
                runtime.InitializeMatch(mode, spawnCells, roster);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load match setup: MatchingId={MatchingId}", matchingId);
            throw;
        }
        finally
        {
            initializationLock.Release();
        }
    }

    /// <summary>최초 구성 확정 시 매치 잠금 안에서만 호출한다. 사람별 입장에서는 다시 실행하지 않는다.</summary>
    private void InitializeMatchLog(MatchRuntime runtime)
    {
        long matchingId = runtime.MatchingId;
        int matchSeed = MatchSpawnData.GetDeterministicSeed(matchingId);
        eventLogs.BeginMatch(matchingId, matchSeed);
        foreach (var bot in runtime.Bots.GetBots(matchingId))
        {
            if (!bot.IsEliminated)
                eventLogs.SetPlayerArea(matchingId, bot.PlayerId, bot.CurrentArea.ToString());
            eventLogs.LogSpawnAssignment(
                matchingId, bot.PlayerId, matchSeed,
                MatchSpawnData.GetAnchorIndex(bot.Cell), bot.Cell.X, bot.Cell.Y,
                bot.CurrentArea.ToString(), isBot: true);
        }
    }

    private async Task<MatchManifest> ReadMatchManifestAsync(long matchingId)
    {
        // manifest는 ticket 발급보다 먼저 쓰인다. 없으면 만료됐거나 handoff가 지워진 것이다.
        var serialized = await RedisOperations.HashGetAsync(
            MatchingRedisKeys.Key(matchingId),
            MatchingRedisKeys.ManifestField);
        if (serialized.IsNullOrEmpty)
            throw new InvalidOperationException($"Missing match manifest for match {matchingId}.");

        return MessagePackSerializer.Deserialize<MatchManifest>((byte[])serialized!)
               ?? throw new InvalidOperationException($"Match manifest is empty for match {matchingId}.");
    }

    private async Task WaitForMatchingEntryReadyAsync(
        long matchingId,
        IReadOnlyCollection<long> expectedHumanPlayerIds)
    {
        TimeSpan retryDelay = TimeSpan.FromMilliseconds(50);
        int maxAttempts = Math.Max(
            1,
            (int)Math.Ceiling(MatchingRedisKeys.EntryTimeout.TotalMilliseconds /
                              retryDelay.TotalMilliseconds));
        string handoffKey = MatchingRedisKeys.Key(matchingId);
        string entryStateKey = MatchingRedisKeys.EntryStateKey(matchingId);

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            var ready = await RedisOperations.HashGetAsync(
                handoffKey,
                MatchingRedisKeys.EntryReadyField);
            if (!ready.IsNullOrEmpty)
            {
                byte[] value = (byte[])ready!;
                if (value.Length == 1 && value[0] == MatchingRedisKeys.EntryReadyValue)
                {
                    string expectedReservation = matchingId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    foreach (long humanPlayerId in expectedHumanPlayerIds)
                    {
                        var reservation = await RedisOperations.StringGetAsync(
                            MatchingRedisKeys.ReservationKey(humanPlayerId));
                        if (reservation.IsNullOrEmpty || !string.Equals(reservation.ToString(), expectedReservation,
                                StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(
                                $"Matching reservation is not active for player {humanPlayerId} in match {matchingId}.");
                        }
                    }
                    return;
                }
                throw new InvalidOperationException(
                    $"Invalid entry marker for match {matchingId}.");
            }

            var entryState = await RedisOperations.StringGetAsync(entryStateKey);
            if (!entryState.IsNullOrEmpty &&
                string.Equals(
                    entryState.ToString(),
                    MatchingRedisKeys.EntryCanceledState,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Matching entry was canceled before the handoff became ready for match {matchingId}.");
            }

            if (attempt + 1 < maxAttempts)
                await Task.Delay(retryDelay);
        }

        throw new TimeoutException(
            $"Matching handoff was not committed for match {matchingId}.");
    }

}
