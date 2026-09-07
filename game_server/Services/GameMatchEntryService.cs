using game_server.network;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.gameentry;
using network.helpers;
using network.infrastructure.redis;

namespace game_server.services;

/// <summary>
///     현재 노드에 발급된 입장 티켓을 소비하고 manifest·예약·준비 마커를 확인해 사람·봇·스폰 구성을 매치당 한 번 확정한다.
///     Redis 조회는 매치별 비동기 초기화 잠금으로 직렬화하고, 메모리 상태 반영은 매치 잠금 안에서 처리한다.
///     연결 인증과 초기 패킷 전송은 세션이 맡으며, 이 서비스는 특정 연결을 보관하지 않는다.
/// </summary>
internal sealed class GameMatchEntryService(
    IRedisOperations RedisOperations,
    MatchRuntimeStore _matchRuntimes,
    GameServerDevOptions _devOptions,
    ILogger Logger,
    GameEntryTicketService ticketService,
    GameServerNodeOptions nodeOptions)
{
    private readonly GameEntryStateCommitter _entryStateCommitter = new(RedisOperations, Logger);

    /// <summary>티켓을 한 번 소비하고 현재 GameServer 노드에 배정된 입장인지 확인한다.</summary>
    public Task<GameEntryContext?> ConsumeTicketAsync(string? ticket) =>
        ticketService.ConsumeAsync(ticket, nodeOptions.NodeId);

    public Task CommitAsync(long matchingId, long playerId, IReadOnlyCollection<long> humanPlayerIds) =>
        _entryStateCommitter.CommitAsync(matchingId, playerId, humanPlayerIds);

    private void RunUnderLiveMatch(MatchRuntime runtime, Action initialize)
    {
        using MatchScope scope = _matchRuntimes.Enter(runtime);
        if (runtime.IsTerminal)
            throw new OperationCanceledException("Match became terminal during game entry.");
        initialize();
    }
    /// <summary>
    ///     매치당 한 번 manifest(사람 ID·봇 수)를 읽고 봇 ID·스폰·최종 명단을 확정한다.
    ///     이후 세션은 런타임에 세워진 구성을 그대로 쓴다. 입장 마커·사람 reservation 확인은 세션마다 다시 한다.
    /// </summary>
    public async Task<MatchComposition> LoadCompositionAsync(long matchingId, MapId mapId, MatchRuntime runtime)
    {
        var initializationLock = runtime.EntryInitializationLock;
        await initializationLock.WaitAsync();
        try
        {
            MatchManifest manifest = await ReadMatchManifestAsync(matchingId);
            await WaitForMatchingEntryReadyAsync(matchingId, manifest.HumanPlayerIds);

            using (_matchRuntimes.Enter(runtime))
            {
                if (runtime.IsTerminal)
                    throw new OperationCanceledException("Match became terminal during game entry.");
                if (runtime.Composition is { } existing)
                    return existing;
            }
            List<long> humanPlayerIds = manifest.HumanPlayerIds.Distinct().ToList();
            List<long> botPlayerIds = MatchRosterBuilder.CreateBotIds(manifest);
            MatchMode mode = manifest.Mode;

            IReadOnlyDictionary<long, Cell> spawnCells =
                MatchSpawnPlanner.Plan(
                    matchingId, mapId, humanPlayerIds.Concat(botPlayerIds), _devOptions.CrossfireSandbox);
            if (botPlayerIds.Count > 0)
                RunUnderLiveMatch(runtime, () => runtime.Bots.RegisterBots(matchingId, mapId, botPlayerIds, spawnCells));

            var roster = await new MatchRosterBuilder(RedisOperations, Logger).BuildAsync(humanPlayerIds,
                botPlayerIds.Select(id => _matchRuntimes.GetRequired(matchingId).Bots.SynthesizePlayerInfo(matchingId, id)
                    ?? throw new InvalidOperationException($"Bot {id} was not initialized.")));

            var composition = new MatchComposition(humanPlayerIds, botPlayerIds, mode, spawnCells, roster);
            RunUnderLiveMatch(runtime, () =>
            {
                foreach (var participant in roster)
                {
                    runtime.Roster.RegisterEntry(new RosterEntry { PlayerId = participant.PlayerId });
                    runtime.Roster.UpdatePlayerProfile(participant.PlayerId, participant.Name, participant.WearItemIdList);
                }
                runtime.Composition = composition;
            });
            return composition;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load match composition: MatchingId={MatchingId}", matchingId);
            throw;
        }
        finally
        {
            initializationLock.Release();
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
