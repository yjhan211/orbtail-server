using System.Collections.Generic;
using System.Linq;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;
using network.packets;

namespace game_server.network;

public partial class GameClientSession
{

    private async Task HandleConnect(C_TO_G_CONNECT msg)
    {
        try
        {
            Logger.LogInformation("Client connection request: PlayerId={MsgPlayerId}, MatchingId={MsgMatchingId}",
                msg.PlayerId, msg.MatchingId);

            // TODO: MatchingId 寃利?(Redis?먯꽌 留ㅼ묶 ?뺣낫 ?뺤씤)
            // 吏湲덉? 媛꾨떒?섍쾶 PlayerId留??ㅼ젙

            PlayerId = msg.PlayerId;
            CurrentMapId = MapId.School; // TODO: 留ㅼ묶 ?뺣낫?먯꽌 媛?몄삤湲?
            CurrentMapSubId = msg.MatchingId;

            // 留덈땲??泥댁씤 ?뺣낫 ???
            TargetPlayerId = msg.TargetPlayerId;
            MyJobTitle = msg.MyJobTitle;
            TargetJobTitle = msg.TargetJobTitle;
            SetActiveBuffIds([]);
            Logger.LogInformation("留덈땲??泥댁씤: PlayerId={PlayerId}, ?寃?{Target}, ??吏곸콉={MyJob}, ?寃?吏곸콉={TargetJob}",
                PlayerId, TargetPlayerId, MyJobTitle, TargetJobTitle);

            // 泥댁씤 留ㅻ땲???留곹겕 ?깅줉 (媛??뚮젅?댁뼱媛 ?묒냽???뚮쭏???꾩쟻)
            _manittoChainManager.RegisterLink(msg.MatchingId, new ChainLink
            {
                PlayerId = msg.PlayerId,
                TargetPlayerId = msg.TargetPlayerId,
                MyJobTitle = msg.MyJobTitle,
                TargetJobTitle = msg.TargetJobTitle
            });

            // 誘몄뀡 珥덇린??
            SetActiveBuffIds([]); // 방 사건 특성 버프 퇴역 (#238) — 접속 시 버프 없음

            // 遊?濡쒕뱶 (留ㅼ묶??理쒖큹 1?? ???먯뇙 珥덇린???꾩뿉 濡쒕뱶??吏곸콉 ????뺤젙 (#87)
            await LoadBotsIfNeeded(msg.MatchingId, CurrentMapId);
            if (TargetPlayerId == 0)
            {
                long recoveredTargetPlayerId = ResolveHumanTargetFromLoadedBotChain(msg.MatchingId, msg.PlayerId);
                if (recoveredTargetPlayerId != 0)
                {
                    TargetPlayerId = recoveredTargetPlayerId;
                    var targetBot = _botPlayerManager.GetBot(msg.MatchingId, recoveredTargetPlayerId);
                    if (targetBot != null)
                        TargetJobTitle = targetBot.MyJobTitle;

                    Logger.LogWarning(
                        "Recovered missing human target from bot chain: MatchingId={MatchingId}, PlayerId={PlayerId}, Target={TargetPlayerId}, TargetJob={TargetJobTitle}",
                        msg.MatchingId, msg.PlayerId, TargetPlayerId, TargetJobTitle);

                    _manittoChainManager.RegisterLink(msg.MatchingId, new ChainLink
                    {
                        PlayerId = msg.PlayerId,
                        TargetPlayerId = TargetPlayerId,
                        MyJobTitle = MyJobTitle,
                        TargetJobTitle = TargetJobTitle
                    });
                }
                else
                {
                    Logger.LogWarning(
                        "C_TO_G_CONNECT missing target and recovery failed: MatchingId={MatchingId}, PlayerId={PlayerId}",
                        msg.MatchingId, msg.PlayerId);
                }
            }

            foreach (var bot in _botPlayerManager.GetBots(msg.MatchingId))
                if (!bot.IsEliminated)
                {
                    _presenceTracker?.SetPlayerArea(msg.MatchingId, bot.PlayerId, bot.CurrentArea,
                        countAsEntry: false);
                    _gameEventLogManager.SetPlayerArea(msg.MatchingId, bot.PlayerId, bot.CurrentArea.ToString());
                }

            // 援ъ뿭 ?먯뇙 珥덇린??(留ㅼ묶??理쒖큹 1??
            // #87: 留ㅼ묶??吏곸콉 ????뷀뵆 ?곗꽑?쒖쐞??諛섏쁺 (5遺?1?④퀎 蹂댁옣 + 吏곸콉蹂??꾩닚??
            var jobPool = _manittoChainManager.GetMatchingJobs(msg.MatchingId);
            if (!Config.SPOT_ARENA_P0_ENABLED)
            {
                _areaClosureManager.InitializeMatching(
                    msg.MatchingId,
                    jobPool,
                    SurvivorRoyaleSpawnData.GetPhaseRoomCandidates());
            }
            _areaItemStockManager.InitializeMatching(msg.MatchingId);
            _groundItemManager.InitializeMatching(msg.MatchingId);
            int matchSeed = SurvivorRoyaleSpawnData.GetDeterministicSeed(msg.MatchingId);
            _gameEventLogManager.BeginMatch(msg.MatchingId, matchSeed);
            if (!Config.SPOT_ARENA_P0_ENABLED)
            {
                _emotionAfterimageMonsterManager.InitializeMatching(msg.MatchingId);
                _gameEventLogManager.LogRewardAreaSnapshot(
                    msg.MatchingId,
                    _emotionAfterimageMonsterManager.GetRewardAreaSnapshot(msg.MatchingId),
                    "initial");
            }
            foreach (var bot in _botPlayerManager.GetBots(msg.MatchingId))
            {
                _gameEventLogManager.LogSpawnAssignment(
                    msg.MatchingId,
                    bot.PlayerId,
                    matchSeed,
                    SurvivorRoyaleSpawnData.GetAnchorIndex(bot.Cell),
                    bot.Cell.X,
                    bot.Cell.Y,
                    bot.CurrentArea.ToString(),
                    isBot: true);
            }

            // ?멸쾶???ㅽ꺈 珥덇린??            ResetInGameStats();

            // ?몄뀡 ?깅줉 ??寃뚯엫 ??대㉧ ?쒖옉 (?대떦 留ㅼ묶?????理쒖큹 1?뚮쭔)
            _registerSessionCallback(PlayerId.Value, this);
            int connectedBotCount = _botPlayerManager.GetBots(msg.MatchingId).Count;
            MatchStartGate.RegisterHumanPlayer(msg.MatchingId, PlayerId.Value, connectedBotCount);

            StartGameTimerIfNeeded(msg.MatchingId);

            // 珥덇린 ?꾩튂 濡쒕뱶
            {
                await using var playerLock = await PlayerInfo.Lock(RedLock, PlayerId.Value);
                var playerInfo = await PlayerInfo.Load(CacheHelper, PlayerId.Value);

                if (playerInfo != null)
                {
                    var matchingSpawnCell = await LoadMatchingSpawnCell(msg.MatchingId, msg.PlayerId);
                    if (matchingSpawnCell != null)
                    {
                        playerInfo.ObjectInfo.Cell = matchingSpawnCell;
                        playerInfo.ObjectInfo.Position = CellToWorldPosition(matchingSpawnCell);
                    }

                    _lastValidatedPosition = playerInfo.ObjectInfo.Position;
                    _lastValidCell = playerInfo.ObjectInfo.Cell;
                    _lastValidatedRotation = playerInfo.ObjectInfo.Rotation;
                    // 珥덇린 Area ?ㅼ젙
                    CurrentArea = GameMapData.GetCurrentArea(CurrentMapId, playerInfo.ObjectInfo.Cell);
                    Logger.LogInformation(
                        "Player {PlayerId} initial Area: {Area}, Position: ({PosX:F2},{PosY:F2}), Cell: ({CellX},{CellY})",
                        PlayerId, CurrentArea, _lastValidatedPosition?.X, _lastValidatedPosition?.Y, _lastValidCell?.X,
                        _lastValidCell?.Y);
                    _presenceTracker?.SetPlayerArea(CurrentMapSubId, PlayerId.Value, CurrentArea,
                        countAsEntry: false);
                    _gameEventLogManager.LogSpawnAssignment(
                        CurrentMapSubId,
                        PlayerId.Value,
                        SurvivorRoyaleSpawnData.GetDeterministicSeed(CurrentMapSubId),
                        SurvivorRoyaleSpawnData.GetAnchorIndex(_lastValidCell),
                        _lastValidCell.X,
                        _lastValidCell.Y,
                        CurrentArea.ToString(),
                        isBot: false);
                    _gameEventLogManager.SetPlayerArea(CurrentMapSubId, PlayerId.Value, CurrentArea.ToString());

                    // 珥덇린 Area??Interactable 紐⑸줉 ?꾩넚
                    if (CurrentArea != AreaType.None)
                    {
                        SendInteractableList(CurrentArea);
                        SendInteractCooldownSnapshot();
                        SendGroundItemSnapshot(CurrentArea);

                        // 珥덇린 Area?먯꽌???щ낫?二??대깽???몃━嫄?                    _sabotageManager.OnPlayerEnterArea(CurrentMapSubId, CurrentArea);
                    }
                }

                // ?곌껐 ?깃났 ?묐떟
            }

            using var connectResultPacket = Packet.Create((int)Protocol.G_TO_C_CONNECT_RESULT, PlayerId.Value);
            var response = new G_TO_C_CONNECT_RESULT
            {
                Success = true,
                ErrorCode = ErrorCode.SUCCESS,
                Message = "Connected to GameServer"
            };
            connectResultPacket.SetBody(MessagePackSerializer.Serialize(response));
            Send(connectResultPacket);
            SendMatchStartCountdown(msg.MatchingId);

            Logger.LogInformation("Client connected successfully: PlayerId={L}", PlayerId);


            if (!Config.SPOT_ARENA_P0_ENABLED)
                _summonStoneManager.EnsureStartingStones(CurrentMapSubId, PlayerId.Value);

            SendInGameInventoryList();
            SendSummonStoneState();
            var connectionBoard = _inGameInventoryManager.GetPlayerInventory(CurrentMapSubId, PlayerId.Value);
            _gameEventLogManager.LogSurvivorOrbBoardTransition(
                CurrentMapSubId, PlayerId.Value, connectionBoard.GetAllItems(),
                connectionBoard.GetEquippedBattleItem()?.ItemId ?? 0, CurrentArea.ToString(), "connection_sync", isBot: false);

            // 臾?珥덇린 ?곹깭 ?ㅼ젙 諛??대┛ 臾?紐⑸줉 ?꾩넚
            _doorStateManager.InitializeMatching(
                CurrentMapSubId,
                Config.SPOT_ARENA_P0_ENABLED
                    ? Array.Empty<AreaType>()
                    : SurvivorRoyaleSpawnData.GetPhaseRoomCandidates());
            SendDoorStateList();

            // 誘몄뀡 ?뺣낫 ?꾩넚
            // 스웜 모드(M4)는 시간 웨이브 폐쇄를 쓰므로 폐쇄 스냅샷을 복원해야 한다.
            if (!Config.SPOT_ARENA_P0_ENABLED || Config.SWARM_P0_ENABLED)
                SendAreaClosureStateSnapshot();
            SendSurvivorAreaStockStateSnapshot();
            if (!Config.SPOT_ARENA_P0_ENABLED)
                SendMonsterSnapshot();
            SendChecklistInfo();

            // ?ㅻⅨ ?뚮젅?댁뼱???뺣낫 ?꾩넚 & ???뺣낫 釉뚮줈?쒖틦?ㅽ듃
            await BroadcastPlayerJoin();

            // The standalone submission client is only ready after the full initial snapshot
            // has been sent. Starting the countdown earlier lets bots consume finite room stock
            // while the human client is still loading the match.
            int expectedBotCount = Config.SWARM_P0_ENABLED
                ? Config.SWARM_PLAYERS_PER_MATCH - 1
                : Config.SPOT_ARENA_P0_ENABLED
                    ? 3
                    : 7;
            if (connectedBotCount == expectedBotCount || MatchStartGate.IsSoloMapValidationEnabled)
            {
                MatchStartGate.MarkHumanReady(msg.MatchingId, PlayerId.Value);
                SendMatchStartCountdown(msg.MatchingId);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to handle connect");

            // ?곌껐 ?ㅽ뙣 ?묐떟
            using var packet = Packet.Create((int)Protocol.G_TO_C_CONNECT_RESULT);
            var response = new G_TO_C_CONNECT_RESULT
            {
                Success = false,
                ErrorCode = ErrorCode.FATAL,
                Message = "?곌껐 泥섎━ 以??ㅻ쪟媛 諛쒖깮?덉뒿?덈떎"
            };
            packet.SetBody(MessagePackSerializer.Serialize(response));
            Send(packet);
        }
    }

    private async Task<Cell?> LoadMatchingSpawnCell(long matchingId, long playerId)
    {
        try
        {
            string handoffKey = MatchingHandoffRedisKeys.Key(matchingId);
            var raw = await CacheHelper.HashGetAsync(handoffKey, MatchingHandoffRedisKeys.SpawnField(playerId));
            return raw.IsNullOrEmpty
                ? null
                : MessagePackSerializer.Deserialize<Cell>((byte[])raw!);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "Matching spawn handoff load failed: MatchingId={MatchingId}, PlayerId={PlayerId}",
                matchingId, playerId);
            return null;
        }
    }
    private async Task BroadcastPlayerJoin()
    {
        if (!PlayerId.HasValue || IsEliminated) return;

        try
        {
            var allSessions = _getSessionsByInstance(CurrentMapId, CurrentMapSubId);
            // Same-area players only
            var sameAreaSessions = allSessions
                .Where(s => !s.IsEliminated &&
                            s.PlayerId != PlayerId &&
                            s.CurrentArea == CurrentArea &&
                            s.PlayerId.HasValue)
                .ToList();

            // 1. ?섏뿉寃?媛숈? Area???ㅻⅨ ?뚮젅?댁뼱???뺣낫 ?꾩넚
            if (sameAreaSessions.Count > 0)
            {
                var playerInfoList = new List<PlayerInfo>();
                foreach (var session in sameAreaSessions)
                {
                    await using var playerLock = await PlayerInfo.Lock(RedLock, session.PlayerId!.Value);
                    var playerInfo = await PlayerInfo.Load(CacheHelper, session.PlayerId!.Value);
                    if (playerInfo == null) continue;

                    ApplyLivePlayerInfoSnapshot(session, playerInfo);
                    playerInfoList.Add(playerInfo);
                }

                if (playerInfoList.Count > 0)
                {
                    using var packet = PacketMaker.G_TO_C_PLAYER_INFO(playerInfoList);
                    Send(packet);
                    Logger.LogInformation("Sent {Count} PlayerInfo in Area {Area} to PlayerId={L}",
                        playerInfoList.Count, CurrentArea, PlayerId);
                }
            }

            // 2. ???뺣낫 濡쒕뱶
            await using var myPlayerLock = await PlayerInfo.Lock(RedLock, PlayerId.Value);
            var myPlayerInfo = await PlayerInfo.Load(CacheHelper, PlayerId.Value);

            if (myPlayerInfo != null)
            {
                ApplyLivePlayerInfoSnapshot(this, myPlayerInfo);

                // 3. 媛숈? Area???ㅻⅨ ?뚮젅?댁뼱?ㅼ뿉寃????뺣낫 釉뚮줈?쒖틦?ㅽ듃
                using var myPacket = PacketMaker.G_TO_C_PLAYER_INFO([myPlayerInfo]);
                foreach (var session in sameAreaSessions) session.Send(myPacket);
                Logger.LogInformation("Broadcasted my PlayerInfo (PlayerId={L}) to {Count} players in Area {Area}",
                    PlayerId, sameAreaSessions.Count, CurrentArea);
            }

            // 4. #125: 媛숈? Area??遊뉖뱾 ?뺣낫瑜??섏뿉寃??꾩넚 (?ㅼ젣 ?뚮젅?댁뼱 ?숇벑 ?쒓컖??
            var sameAreaBots = _botPlayerManager.GetBots(CurrentMapSubId)
                .Where(b => !b.IsEliminated && b.CurrentArea == CurrentArea)
                .ToList();
            if (sameAreaBots.Count > 0)
            {
                var botInfoList = new List<PlayerInfo>();
                foreach (var bot in sameAreaBots)
                {
                    var botInfo = _botPlayerManager.SynthesizePlayerInfo(CurrentMapSubId, bot.PlayerId);
                    if (botInfo != null) botInfoList.Add(botInfo);
                }
                if (botInfoList.Count > 0)
                {
                    using var botPacket = PacketMaker.G_TO_C_PLAYER_INFO(botInfoList);
                    Send(botPacket);
                    Logger.LogInformation("Sent {Count} bot PlayerInfo in Area {Area} to PlayerId={L}",
                        botInfoList.Count, CurrentArea, PlayerId);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to broadcast player join for PlayerId={L}", PlayerId);
        }
    }

    private static void ApplyLivePlayerInfoSnapshot(GameClientSession session, PlayerInfo playerInfo)
    {
        var cell = session._lastValidCell ?? playerInfo.LastCell ?? playerInfo.ObjectInfo?.Cell;
        var position = session._lastValidatedPosition;

        playerInfo.State = session.CurrentState == PlayerState.Exploring
            ? global::network.common.PlayerState.EXPLORE_1
            : global::network.common.PlayerState.IDLE;
        playerInfo.LastMapId = session.CurrentMapId;
        playerInfo.LastMapSubId = session.CurrentMapSubId;
        playerInfo.ObjectInfo ??= new GameObjectInfo(playerInfo.PlayerId);
        playerInfo.ObjectInfo.MapId = session.CurrentMapId;
        playerInfo.ObjectInfo.MapSubId = session.CurrentMapSubId;
        playerInfo.ObjectInfo.Rotation = session._lastValidatedRotation;

        if (cell != null)
        {
            playerInfo.LastCell = Cell.Clone(cell);
            playerInfo.ObjectInfo.Cell = Cell.Clone(cell);
            position ??= session.CellToWorldPosition(cell);
        }

        if (position != null)
            playerInfo.ObjectInfo.Position = position;
    }

    private void SendMatchStartCountdown(long matchingId)
    {
        var snapshot = MatchStartGate.GetSnapshot(matchingId);
        if (!snapshot.IsKnown)
            return;

        using var packet = Packet.Create((int)Protocol.G_TO_C_MATCH_START_COUNTDOWN, PlayerId ?? 0);
        packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_ROUND_STATE
        {
            MatchingId = matchingId,
            RoundNumber = 0,
            TotalRounds = 0,
            Phase = RoundPhase.Action,
            RemainingSeconds = snapshot.RemainingSeconds,
            PhaseDurationSeconds = 5,
            ServerUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            IsSessionEnded = false
        }));
        Send(packet);
    }

    private Task HandleMatchStartReady()
    {
        if (PlayerId.HasValue && CurrentMapSubId > 0)
        {
            MatchStartGate.MarkHumanReady(CurrentMapSubId, PlayerId.Value);
            SendMatchStartCountdown(CurrentMapSubId);
        }

        return Task.CompletedTask;
    }

    private Task HandleHeartbeat()
    {
        _lastHeartbeatTime = DateTime.UtcNow;

        // ?섑듃鍮꾪듃 ?묐떟 ?꾩넚
        using var packet = PacketMaker.G_TO_C_HEART_BEAT(DateTime.UtcNow);
        Send(packet);

        return Task.CompletedTask;
    }

    /// <summary>
    ///     ?섑듃鍮꾪듃 ??꾩븘??泥댄겕. ??꾩븘?껊릺硫?true 諛섑솚
    /// </summary>
    public bool IsHeartbeatTimedOut()
    {
        double elapsed = (DateTime.UtcNow - _lastHeartbeatTime).TotalSeconds;
        return elapsed > HeartbeatTimeoutSeconds;
    }

    /// <summary>
    ///     媛뺤젣 ?곌껐 ?댁젣
    /// </summary>
    public void ForceDisconnect()
    {
        Logger.LogWarning("Force disconnecting PlayerId={PlayerId} due to heartbeat timeout", PlayerId);
        Token.Disconnect();
    }

    /// <summary>
    ///     Redis?먯꽌 遊??뺣낫 濡쒕뱶 (留ㅼ묶??理쒖큹 1??.
    ///     #125: 遊??꾩튂 珥덇린?붿뿉 MapId媛 ?꾩슂?섎?濡??몄옄濡??꾨떖.
    /// </summary>
    private async Task LoadBotsIfNeeded(long matchingId, MapId mapId)
    {
        try
        {
            if (_botPlayerManager.HasBots(matchingId)) return;

            string handoffKey = MatchingHandoffRedisKeys.Key(matchingId);
            var botData = await CacheHelper.HashGetAsync(handoffKey, MatchingHandoffRedisKeys.BotsField);
            if (botData.IsNullOrEmpty)
                botData = await CacheHelper.HashGetAsync("matching_bots", matchingId);

            if (botData.IsNullOrEmpty) return;

            await CacheHelper.HashDeleteAsync(handoffKey, MatchingHandoffRedisKeys.BotsField);
            await CacheHelper.HashDeleteAsync("matching_bots", matchingId);
            var botInfoList = MessagePackSerializer.Deserialize<List<BotMatchingInfo>>((byte[])botData!);

            _botPlayerManager.RegisterBots(matchingId, mapId, botInfoList);

            // 遊뉖룄 泥댁씤 留ㅻ땲? + 誘몄뀡 留ㅻ땲????깅줉 (#26: 遊?遺???뚯닔/寃고빀 ?쒕???
            foreach (var bot in botInfoList)
            {
                _manittoChainManager.RegisterLink(matchingId, new ChainLink
                {
                    PlayerId = bot.PlayerId,
                    TargetPlayerId = bot.TargetPlayerId,
                    MyJobTitle = bot.MyJobTitle,
                    TargetJobTitle = bot.TargetJobTitle
                });

                // 遊?遺???곹깭 珥덇린?????먭린 吏곸콉 諛쒓껄 ? 湲곗?

                if (!Config.SPOT_ARENA_P0_ENABLED)
                    _summonStoneManager.EnsureStartingStones(matchingId, bot.PlayerId);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "遊??뺣낫 濡쒕뱶 ?ㅽ뙣: MatchingId={MatchingId}", matchingId);
        }
    }

    private long ResolveHumanTargetFromLoadedBotChain(long matchingId, long humanPlayerId)
    {
        var bots = _botPlayerManager.GetBots(matchingId)
            .Where(b => !b.IsEliminated)
            .ToList();
        int expectedBotCount = Config.SPOT_ARENA_P0_ENABLED ? 3 : 4;
        if (bots.Count != expectedBotCount || bots.All(b => b.TargetPlayerId != humanPlayerId)) return 0;

        var botIds = bots.Select(b => b.PlayerId).ToHashSet();
        var targetedBotIds = bots
            .Where(b => b.TargetPlayerId != humanPlayerId && botIds.Contains(b.TargetPlayerId))
            .Select(b => b.TargetPlayerId)
            .ToHashSet();

        var candidates = botIds
            .Where(botId => !targetedBotIds.Contains(botId))
            .ToList();

        if (candidates.Count == 1) return candidates[0];

        Logger.LogWarning(
            "Could not infer human target from bot chain: MatchingId={MatchingId}, PlayerId={PlayerId}, Candidates=[{Candidates}]",
            matchingId, humanPlayerId, string.Join(",", candidates));
        return 0;
    }

    private static bool IsSameBotChain(List<BotPlayerState> existingBots, List<BotMatchingInfo> botInfoList)
    {
        if (existingBots.Count != botInfoList.Count) return false;

        var existingById = existingBots.ToDictionary(b => b.PlayerId);
        foreach (var botInfo in botInfoList)
        {
            if (!existingById.TryGetValue(botInfo.PlayerId, out var existing)) return false;
            if (existing.TargetPlayerId != botInfo.TargetPlayerId) return false;
            if (existing.MyJobTitle != botInfo.MyJobTitle) return false;
            if (existing.TargetJobTitle != botInfo.TargetJobTitle) return false;
        }

        return true;
    }

    /// <summary>
    ///     매치 세션 시작 (매칭당 최초 1회) — 체크리스트 라운드 상태만 초기화한다.
    ///     라운드/정산 시스템은 퇴역(#246); 매치 종료는 SwarmArena 틱(orb_score_timeout)이 담당한다.
    /// </summary>
    private void StartGameTimerIfNeeded(long matchingId)
    {
        lock (_roundSessionStartLock)
        {
            _checklistManager.RemoveMatchingState(matchingId);
            _presenceTracker?.Remove(matchingId);
            StartChecklistRound(matchingId, 1, broadcast: false);
            Logger.LogInformation("Continuous session started: MatchingId={MatchingId}", matchingId);
        }
    }

    private void StartChecklistRound(long matchingId, int roundNumber, bool broadcast = true)
    {
        var playerIds = GetChecklistActivePlayerIds(matchingId);
        if (playerIds.Count == 0)
            return;

        _checklistManager.StartRound(matchingId, roundNumber, playerIds,
            playerId => ResolveChecklistChainContext(matchingId, playerId));
        if (broadcast)
            BroadcastChecklistInfo(matchingId);
    }

    private void BroadcastChecklistInfo(long matchingId)
    {
        foreach (var session in _getSessionsByInstance(CurrentMapId, matchingId))
        {
            if (session.PlayerId.HasValue)
                session.SendChecklistInfo();
        }

        if (CurrentMapSubId == matchingId && PlayerId.HasValue)
            SendChecklistInfo();
    }

    private List<long> GetAliveMatchPlayerIds(long matchingId)
    {
        var result = new List<long>();

        foreach (var session in _getSessionsByInstance(CurrentMapId, matchingId))
        {
            if (!session.PlayerId.HasValue || session.IsEliminated ||
                session.ManittoStatus == ManittoStatus.SPECTATING)
                continue;

            result.Add(session.PlayerId.Value);
        }

        foreach (var bot in _botPlayerManager.GetBots(matchingId))
        {
            if (bot.IsEliminated || bot.ManittoStatus == ManittoStatus.SPECTATING)
                continue;

            result.Add(bot.PlayerId);
        }

        return result
            .Distinct()
            .OrderBy(playerId => playerId)
            .ToList();
    }

    private List<long> GetChecklistActivePlayerIds(long matchingId)
    {
        var playerIds = GetAliveMatchPlayerIds(matchingId);

        if (CurrentMapSubId == matchingId
            && PlayerId.HasValue
            && !IsEliminated
            && ManittoStatus != ManittoStatus.SPECTATING)
        {
            playerIds.Add(PlayerId.Value);
        }

        return playerIds
            .Distinct()
            .OrderBy(id => id)
            .ToList();
    }

    private ChecklistChainContext ResolveChecklistChainContext(long matchingId, long playerId)
    {
        var myLink = _manittoChainManager.GetLink(matchingId, playerId);
        bool targetAlive = myLink != null && IsAliveChainPlayer(matchingId, myLink.TargetPlayerId);
        var manittoLink = _manittoChainManager.FindManittoOf(matchingId, playerId);
        bool manittoAlive = IsAliveChainLink(manittoLink);
        return new ChecklistChainContext(targetAlive, manittoAlive);
    }

    private bool IsAliveChainPlayer(long matchingId, long playerId)
    {
        var link = _manittoChainManager.GetLink(matchingId, playerId);
        return IsAliveChainLink(link);
    }

    private static bool IsAliveChainLink(ChainLink? link)
    {
        return link != null
               && link.Status != ManittoStatus.ELIMINATED
               && link.Status != ManittoStatus.SPECTATING;
    }

    /// <summary>
    /// 새 접속과 재접속 시 현재 방송 대상·남은 시간·누적 폐쇄 지역을 복원한다.
    /// 미래 웨이브는 아직 보내지 않아 다음 방송 전까지 대상이 드러나지 않는다.
    /// </summary>
    private void SendAreaClosureStateSnapshot()
    {
        if (CurrentMapSubId <= 0) return;

        var snapshot = _areaClosureManager.GetClientStateSnapshot(CurrentMapSubId);
        foreach (var closedArea in snapshot.ClosedAreas)
        {
            using var packet = Packet.Create((int)Protocol.G_TO_C_AREA_CLOSED);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_AREA_CLOSED
            {
                AreaType = closedArea,
                SuppressAlert = true
            }));
            Send(packet);
        }

        foreach (var warningArea in snapshot.WarningAreas)
        {
            using var packet = Packet.Create((int)Protocol.G_TO_C_AREA_CLOSURE_WARNING);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_AREA_CLOSURE_WARNING
            {
                AreaType = warningArea,
                SecondsRemaining = snapshot.WarningSeconds,
                ClosureAtUnixMs = snapshot.ClosureAtUnixMs
            }));
            Send(packet);
        }
        var globalClosure = _areaClosureManager.GetGlobalClosureClientState(CurrentMapSubId);
        if (globalClosure.IsKnown)
        {
            using var packet = Packet.Create((int)Protocol.G_TO_C_AREA_CLOSURE_WARNING);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_AREA_CLOSURE_WARNING
            {
                AreaType = AreaType.None,
                SecondsRemaining = globalClosure.SecondsRemaining,
                ClosureAtUnixMs = globalClosure.ClosureAtUnixMs,
                IsGlobalClosure = true,
                IsGlobalClosureActive = globalClosure.IsActive
            }));
            Send(packet);
        }

        // AreaType.None is the generic next-warning clock.
        using var countdownPacket = Packet.Create((int)Protocol.G_TO_C_AREA_CLOSURE_WARNING);
        countdownPacket.SetBody(MessagePackSerializer.Serialize(new G_TO_C_AREA_CLOSURE_WARNING
        {
            AreaType = AreaType.None,
            SecondsRemaining = snapshot.NextWarningSeconds,
            ClosureAtUnixMs = snapshot.NextWarningAtUnixMs
        }));
        Send(countdownPacket);
    }

}
