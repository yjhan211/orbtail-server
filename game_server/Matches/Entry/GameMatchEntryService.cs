using game_server.logging;
using game_server.matches.results;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.gameentry;
using network.infrastructure.redis;

namespace game_server.matches.entry;

/// <summary>
///     Game Server 입장에 필요한 티켓 확인과 매치 초기화, 입장 완료 기록을 담당한다.
///     Redis에서 매치 구성과 입장 준비 여부, 참가자의 매칭 예약을 확인한다.
///     매치당 한 번 사람 프로필을 조회하고 봇을 생성해 참가자 명단과 스폰 위치를 확정한다.
/// </summary>
internal sealed class GameMatchEntryService(
    IRedisOperations redisOperations,
    MatchRuntimeStore matchRuntimes,
    ILogger logger,
    GameEntryTicketService ticketService,
    GameServerNodeOptions nodeOptions,
    GameEventLogManager eventLogs)
{
    private static long _botIdCounter;

    private readonly GameEntryStateCommitter _entryStateCommitter = new(redisOperations, logger);
    public MatchRuntime GetOrCreateMatch(long matchingId) => matchRuntimes.GetOrCreate(matchingId);
    public Task<GameEntryContext?> ConsumeTicketAsync(string? ticket) => ticketService.ConsumeAsync(ticket, nodeOptions.NodeId);
    public Task CommitEntryAsync(long matchingId, long playerId, IReadOnlyCollection<long> humanPlayerIds) => _entryStateCommitter.CommitAsync(matchingId, playerId, humanPlayerIds);

    public async Task PrepareMatchAsync(long matchingId, MatchRuntime runtime)
    {
        var initializationLock = runtime.EntryInitializationLock;
        await initializationLock.WaitAsync();
        try
        {
            string matchingKey = MatchingRedisKeys.Key(matchingId);
            var serialized = await redisOperations.HashGetAsync(matchingKey, MatchingRedisKeys.ManifestField);
            if (serialized.IsNullOrEmpty)
            {
                throw new InvalidOperationException($"Missing match manifest for match {matchingId}.");
            }

            var manifest = MessagePackSerializer.Deserialize<MatchManifest>((byte[])serialized!);
            if (manifest == null)
            {
                throw new InvalidOperationException($"Match manifest is empty for match {matchingId}.");
            }

            int humanCount = manifest.HumanPlayerIds.Count;
            if (humanCount == 0 || manifest.BotCount < 0 || manifest.BotCount > Config.SWARM_PLAYERS_PER_MATCH - humanCount)
            {
                throw new InvalidOperationException("Invalid match participant count.");
            }

            var ready = await redisOperations.HashGetAsync(matchingKey, MatchingRedisKeys.EntryReadyField);
            if (ready.IsNullOrEmpty)
            {
                string entryStateKey = MatchingRedisKeys.EntryStateKey(matchingId);
                var entryState = await redisOperations.StringGetAsync(entryStateKey);
                if (string.Equals(entryState.ToString(), MatchingRedisKeys.EntryCanceledState, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Matching entry was canceled before the handoff became ready for match {matchingId}.");
                }
                throw new InvalidOperationException($"Matching handoff was not committed for match {matchingId}.");
            }

            byte[] value = (byte[])ready!;
            if (value is not [MatchingRedisKeys.EntryReadyValue])
            {
                throw new InvalidOperationException($"Invalid entry marker for match {matchingId}.");
            }

            string expectedReservation = matchingId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (long humanPlayerId in manifest.HumanPlayerIds)
            {
                string reservationKey = MatchingRedisKeys.ReservationKey(humanPlayerId);
                var reservation = await redisOperations.StringGetAsync(reservationKey);
                if (reservation.IsNullOrEmpty || !string.Equals(reservation.ToString(), expectedReservation, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Matching reservation is not active for player {humanPlayerId} in match {matchingId}.");
                }
            }

            using (runtime.Enter())
            {
                if (runtime.IsEnded)
                {
                    throw new OperationCanceledException("Match became terminal during game entry.");
                }

                if (runtime.IsSetupComplete)
                {
                    return;
                }
            }

            var mode = manifest.Mode;
            var humanPlayerIds = manifest.HumanPlayerIds.ToList();
            var botPlayerIds = Enumerable.Range(0, manifest.BotCount).Select(_ => Interlocked.Decrement(ref _botIdCounter)).ToList();
            List<long> participantIds = [..humanPlayerIds, ..botPlayerIds];
            var spawnCells = MatchSpawnPlanner.Plan(matchingId, participantIds);
            var roster = new List<PlayerInfo>();

            foreach (long playerId in humanPlayerIds)
            {
                var info = await PlayerInfo.Load(redisOperations, playerId);
                if (info == null)
                {
                    logger.LogWarning("Match entry rejected: PlayerInfo missing ({PlayerId})", playerId);
                    throw new InvalidOperationException($"PlayerInfo not found for match participant {playerId}.");
                }
                roster.Add(new PlayerInfo
                {
                    PlayerId = playerId,
                    Name = info.Name,
                    WearItemIdList = info.WearItemIdList.ToList()
                });
            }

            using (runtime.Enter())
            {
                if (runtime.IsEnded)
                {
                    throw new OperationCanceledException("Match became terminal during game entry.");
                }
                if (botPlayerIds.Count > 0)
                {
                    runtime.Bots.RegisterBots(matchingId, Config.SWARM_MATCH_MAP, botPlayerIds, spawnCells);
                }
                foreach (long botPlayerId in botPlayerIds)
                {
                    var botProfile = runtime.Bots.CreatePlayerInfo(matchingId, botPlayerId);
                    if (botProfile == null)
                    {
                        throw new InvalidOperationException($"Bot {botPlayerId} was not initialized.");
                    }
                    roster.Add(botProfile);
                }
                foreach (var participant in roster)
                {
                    runtime.Roster.RegisterEntry(new RosterEntry { PlayerId = participant.PlayerId });
                    runtime.Roster.UpdatePlayerProfile(participant.PlayerId, participant.Name, participant.WearItemIdList);
                }
                runtime.Doors.Initialize();
                runtime.InitializeMatch(mode, spawnCells, roster);

                int matchSeed = MatchSpawnData.GetDeterministicSeed(matchingId);
                eventLogs.BeginMatch(matchingId, matchSeed);
                foreach (long playerId in participantIds)
                {
                    var spawnCell = spawnCells[playerId];
                    var area = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, spawnCell);
                    eventLogs.SetPlayerArea(matchingId, playerId, area.ToString());
                    eventLogs.LogSpawnAssignment(matchingId, playerId, matchSeed, MatchSpawnData.GetAnchorIndex(spawnCell), spawnCell.X, spawnCell.Y, area.ToString(), isBot: playerId < 0);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load match setup: MatchingId={MatchingId}", matchingId);
            throw;
        }
        finally
        {
            initializationLock.Release();
        }
    }
}
