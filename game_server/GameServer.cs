using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using game_server.admin.dto;
using game_server.controllers;
using game_server.network;
using game_server.services;
using MessagePack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;
using network.core;
using network.helpers;
using network.infrastructure;
using network.interfaces;
using network.packets;

namespace game_server;

public partial class GameServer(
    IConfiguration configuration,
    ILogger<GameServer> logger,
    INatsClientFactory natsClientFactory,
    ICacheHelper cacheHelper,
    INetworkService networkService,
    ServerConfig serverConfig)
    : IHostedService
{
    // 하트비트 체크 간격 (10초마다 체크)
    private const int HeartbeatCheckIntervalSeconds = 10;
    private const float RoomExploreSpotOccupancyDistance = 2.75f;

    private readonly AreaRuleManager _areaRuleManager = new();
    private readonly ConcurrentDictionary<long, GameClientSession> _clientSessions = new();
    private readonly DoorStateManager _doorStateManager = new();
    private readonly InGameInventoryManager _inGameInventoryManager = new();

    private readonly List<InstanceMapManager> _instanceControllerList = [];
    private readonly InteractableStateManager _interactableStateManager = new();
    private readonly ItemPoolManager _itemPoolManager = new();
    private readonly AreaItemStockManager _areaItemStockManager =
        new(naturalExploreLootEnabled: !Config.MONSTER_SUMMON_ECONOMY_ENABLED);
    private readonly GroundItemManager _groundItemManager = new();
    private readonly EmotionAfterimageMonsterManager _emotionAfterimageMonsterManager =
        new(ambientCorridorEnabled: false);
    private readonly SurvivorPhaseManager _survivorPhaseManager = new();
    private readonly SpotArenaManager _spotArenaManager = new();
    private readonly SwarmArenaManager _swarmArenaManager = new();
    private readonly SummonStoneManager _summonStoneManager = new();
    private readonly InteractionLogManager _interactionLogManager = new();
    private readonly ManittoChainManager _manittoChainManager = new(logger);
    private readonly ChecklistManager _checklistManager = new(logger);
    private readonly MatchingConfigService _matchingConfigService = new(cacheHelper, logger);
    // _areaClosureManager은 InitializeServices()에서 _matchingConfigService 생성 후 초기화
    private AreaClosureManager _areaClosureManager = null!;
    private readonly BotPlayerManager _botPlayerManager = new(logger);
    private readonly GameEventLogManager _gameEventLogManager = new();
    private readonly MatchSummaryFileStore _matchSummaryFileStore = new(
        configuration["MATCH_SUMMARY_DIRECTORY"],
        configuration.GetValue<int>("MATCH_SUMMARY_MAX_FILES", MatchSummaryFileStore.DefaultMaxSummaries));
    private readonly EncounterRevealManager _encounterRevealManager = new();
    private readonly Proto0PresenceTracker _presenceTracker = new();
    private readonly ConcurrentDictionary<long, int> _lastMatchStartCountdownBroadcast = new();
    private readonly ConcurrentDictionary<long, object> _survivorSettlementLocks = new();
    // 재시작해도 되감기지 않도록 기동 시각을 섞는다. 고정 시드로 시작하면 서버를 다시
    // 올릴 때마다 같은 matchingId가 나오고, 매치 요약 파일이 같은 이름을 만나
    // 저장이 통째로 건너뛰어진다(기존 파일 우선 규칙).
    private long _adminBotOnlyMatchingIdSeed =
        9_000_000 + DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond % 900_000;
    private long _adminBotOnlyPlayerIdSeed = -900_000_000;
    private INatsClient? _matchingLifecycleNatsClient;

    private CancellationTokenSource _cts = new();
    private Timer? _heartbeatCheckTimer;
    private Timer? _resourceTickTimer;        // 폐쇄 구역 등 주기성 자원 변화
    private Timer? _areaClosureTickTimer;     // 구역 폐쇄 체크
    private Timer? _targetLocationTimer;      // 타겟 위치 전송
    private Timer? _botMovementTimer;         // #127 봇 walking step (250ms)
    private Timer? _checklistProgressTickTimer;

    // 자원 틱 설정 (GDD v0.0.5 확정 수치)
    private int _botMovementProcessing;

    // 봇 이동 틱 계측 (#207) — 틱이 밀려 스킵되면 봇 위치 브로드캐스트 간격이 벌어진다.
    private int _botMovementTickSkips;
    private int _botMovementTickCount;
    private double _botMovementTickTotalMs;
    private readonly List<double> _botMovementSnapshotSamples = new(200);
    private readonly List<double> _botMovementPlanningSamples = new(200);
    private readonly List<double> _botMovementWalkingSamples = new(200);
    private readonly List<double> _botMovementBroadcastSamples = new(200);
    private double _botMovementTickMaxMs;
    private readonly List<double> _botMovementTickSamples = new(200);
    private int _botMovementConsecutiveSkips;
    private int _botMovementMaxConsecutiveSkips;

    internal const int ResourceTickIntervalSeconds = 5;
    private const int ChecklistProgressTickIntervalSeconds = 1;
    private const int ClosedAreaStatusEffectId = 1003;  // status_effect_info: 폐쇄 구역
    private const int TerminalDecayAmount = 5;          // 시한부 추가 감소량 (5초당 오염도 +5)
    // ClosedAreaStaminaPenaltyPerTick 제거 — v0.1.9 #66: 폐쇄 구역 패널티 → 오염도로 변경

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("Game server starting...");
            GameClientSession.SetPresenceTracker(_presenceTracker);
            GameClientSession.SetSurvivorPhaseManager(_survivorPhaseManager);

            InitializeServices();
            InitializeControllers();

            // Redis에서 폐쇄 config 복원 (재시작/핫리로드 후에도 어드민 설정 유지)
            await _matchingConfigService.LoadClosureConfigFromRedisAsync();

            StartTcpServer();
            StartHeartbeatChecker();
            StartResourceTickTimer();
            StartAreaClosureTickTimer();
            StartTargetLocationTimer();
            StartBotMovementTimer();
            StartChecklistProgressTickTimer();
            if (Config.PROXIMITY_AUTO_COMBAT_P0_ENABLED)
                StartProximityAutoCombatTimer();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            logger.LogInformation("Game server started successfully.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Game server starting failed.");
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Game server stopping...");

        // 서버 셧다운 시 모든 세션을 서버 주도 종료로 마킹 → 페널티 면제
        foreach (var session in _clientSessions.Values)
            session.MarkServerInitiatedDisconnect();

        await _cts.CancelAsync();

        // 타이머 정리
        if (_heartbeatCheckTimer != null)
        {
            await _heartbeatCheckTimer.DisposeAsync();
            _heartbeatCheckTimer = null;
        }

        if (_resourceTickTimer != null) { await _resourceTickTimer.DisposeAsync(); _resourceTickTimer = null; }
        if (_areaClosureTickTimer != null) { await _areaClosureTickTimer.DisposeAsync(); _areaClosureTickTimer = null; }
        if (_targetLocationTimer != null) { await _targetLocationTimer.DisposeAsync(); _targetLocationTimer = null; }
        if (_botMovementTimer != null) { await _botMovementTimer.DisposeAsync(); _botMovementTimer = null; }
        if (_checklistProgressTickTimer != null) { await _checklistProgressTickTimer.DisposeAsync(); _checklistProgressTickTimer = null; }
        if (_proximityAutoCombatTimer != null) { await _proximityAutoCombatTimer.DisposeAsync(); _proximityAutoCombatTimer = null; }

        await Task.WhenAll(_instanceControllerList.Select(c => c.ShutdownAsync()));

        _cts.Dispose();

        _matchingLifecycleNatsClient?.Close();
        logger.LogInformation("Game server stopped.");
    }

    private void InitializeServices()
    {
        string natsEndpoint = configuration.GetRequiredString("natsEndPoint");

        // MatchingConfigService 의존 — _matchingConfigService 필드 초기화 후 생성
        _areaClosureManager = new AreaClosureManager(logger, _matchingConfigService);
        // M4: 폐쇄 구역은 스웜 신규 스폰을 멈춘다 (잔존 몹은 ReclaimStrandedMonsters가 걷어냄)
        _swarmArenaManager.IsAreaClosedResolver =
            (matchingId, area) => _areaClosureManager.IsAreaClosed(matchingId, area);
        // 인트로 산개 (2026-08-16): 카운트다운 동안에는 전 방을 공급 대상으로 열어
        // 운동장에서 열 방향으로 실제 몹이 뻗어 나가게 한다.
        _swarmArenaManager.IsGameplayActiveResolver = MatchStartGate.IsGameplayActive;
        // 무오브 우선 표적 (2026-08-16 유저 명세): 잔상 주인 배정·재배정이 이걸 본다.
        // 무오브는 자동 공격도 절단도 못 하므로, 잔상까지 남을 쫓으면 재건하는 동안
        // 아무 압력도 안 받아 무오브가 안전지대가 된다.
        _swarmArenaManager.IsPlayerOrblessResolver =
            (matchingId, playerId) => !HasAnySquadOrb(matchingId, playerId);

        try
        {
            natsClientFactory.Initialize(natsEndpoint);
            _matchingLifecycleNatsClient = natsClientFactory.Create();
            // 서버 환경에서 CSV 파일 경로 설정
            // Dev: 소스 디렉토리에서 직접 읽기 (Docker 볼륨 마운트 대응)
            string networkSourcePath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..",
                "..", "..", "network"));
            GameDataHelper.SetBasePath(Directory.Exists(Path.Combine(networkSourcePath, "Common", "csv"))
                ? networkSourcePath
                : AppDomain.CurrentDomain.BaseDirectory);
            GameDataHelper.Initialize();
            MapHelper.Initialize(serverConfig.GameServerNum);
            Action<string> log = msg => logger.LogInformation(msg);
            _emotionAfterimageMonsterManager.SetMatchingStateRemovedCallback(
                CleanupEmotionAfterimageMonsterRuntime);
            // 방 전투 페이즈 동안 봇의 방 진입을 통제한다. 클리어 전 방은 문이 잠겨 있고,
            // 클리어된 방은 페이즈 규칙이 봇을 복도로 내보내는 공간이다 — 무한 리필로 잔상이
            // 남아 있어 사냥 AI가 되들어가면 페이즈 이동과 0.7초 왕복 루프가 생긴다
            // (2026-08-04 match-2157: 봇당 문턱 왕복 최대 40회). 그래서 현재 스테이지 방
            // 전체를 봇 경로에서 차단한다. 자기 방(현재 위치)은 게이트가 항상 예외로 둔다.
            // 폐쇄 예고부터는 대피를 위해 전 문이 열리므로 ROOM_COMBAT에서만 적용한다.
            _botPlayerManager.SetLockedRoomAreasProvider(matchingId =>
            {
                var snapshot = _survivorPhaseManager.GetSnapshot(matchingId);
                return snapshot.Phase == SurvivorMatchPhase.ROOM_COMBAT
                    ? snapshot.CurrentRooms
                    : Array.Empty<AreaType>();
            });
            // 문 상태는 페이즈와 별개다 (2026-08-16). 위 제공자는 ROOM_COMBAT에서만 채워져
            // 군집 모드에서는 항상 비었고, 그래서 봇이 잠긴 문을 그냥 통과했다.
            _botPlayerManager.SetDoorOpenResolver(_doorStateManager.IsDoorOpen);
            // 투사체 회피 반사 (#232 §9): 봇 걸음마다 교차사격 스냅샷을 물어 비켜설 방향을 받는다.
            _botPlayerManager.SetSwarmDodgeResolver(ResolveSwarmBotDodgeDirection);
            _interactableStateManager.Initialize(log);
            _inGameInventoryManager.Initialize(log);
            _areaRuleManager.Initialize(log);
            _itemPoolManager.Initialize(log);
            _checklistManager.Initialize(log);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to initialize services.", ex);
        }
    }

    private void InitializeControllers()
    {
        var instanceController = new InstanceMapManager(logger, natsClientFactory.Create(), cacheHelper,
            serverConfig, _clientSessions, _interactableStateManager, _inGameInventoryManager, _areaRuleManager);
        instanceController.Initialize();
        _instanceControllerList.Add(instanceController);
    }

    private void StartTcpServer()
    {
        short port = configuration.GetValue<short>("clientPort", 9001);
        networkService.SessionCreatedCallback += OnClientSessionCreated;
        networkService.Listen(IPAddress.Any, port);
        logger.LogInformation($"TCP server listening on port {port}");
    }

    private void StartHeartbeatChecker()
    {
        _heartbeatCheckTimer = new Timer(
            CheckHeartbeatTimeouts,
            null,
            TimeSpan.FromSeconds(HeartbeatCheckIntervalSeconds),
            TimeSpan.FromSeconds(HeartbeatCheckIntervalSeconds));
        logger.LogInformation("Heartbeat checker started (interval: {Interval}s)", HeartbeatCheckIntervalSeconds);
    }

    // ===== 자원 틱 (폐쇄 구역 등 주기성 자원 변화) =====

    private void StartResourceTickTimer()
    {
        _resourceTickTimer = new Timer(ProcessResourceTick, null,
            TimeSpan.FromSeconds(ResourceTickIntervalSeconds),
            TimeSpan.FromSeconds(ResourceTickIntervalSeconds));
        logger.LogInformation("자원 틱 타이머 시작 ({Interval}초)", ResourceTickIntervalSeconds);
    }

    private void StartChecklistProgressTickTimer()
    {
        if (!Config.CHECKLIST_SYSTEM_ENABLED)
        {
            logger.LogInformation("Checklist progress timer disabled by configuration");
            return;
        }

        _checklistProgressTickTimer = new Timer(ProcessChecklistProgressTick, null,
            TimeSpan.FromSeconds(ChecklistProgressTickIntervalSeconds),
            TimeSpan.FromSeconds(ChecklistProgressTickIntervalSeconds));
        logger.LogInformation("체크리스트 진행 틱 타이머 시작 ({Interval}초)", ChecklistProgressTickIntervalSeconds);
    }

    private void ProcessChecklistProgressTick(object? state)
    {
        try
        {
            var activeSessions = _clientSessions.Values
                .Where(s => s.PlayerId.HasValue && !s.IsEliminated)
                .ToList();

            foreach (var session in activeSessions)
            {
                if (!GameClientSession.IsRoundActionPhase(session.CurrentMapSubId))
                    continue;
                if (session.CurrentArea == AreaType.None || session.TargetPlayerId == 0)
                    continue;

                GameClientSession? targetSession = activeSessions.FirstOrDefault(s =>
                    s.PlayerId == session.TargetPlayerId &&
                    s.CurrentMapSubId == session.CurrentMapSubId &&
                    !s.IsEliminated);
                BotPlayerState? targetBot = targetSession == null
                    ? _botPlayerManager.GetBot(session.CurrentMapSubId, session.TargetPlayerId)
                    : null;
                if (targetSession == null && targetBot is not { IsEliminated: false })
                    continue;

                if (IsTargetWithinProximity(session, targetSession, targetBot))
                    session.AdvanceTargetProximityChecklistProgress(ChecklistProgressTickIntervalSeconds);
            }

            var matchingIds = GetActiveMatchingIds();
            foreach (long matchingId in matchingIds)
            {
                if (Config.SPOT_ARENA_P0_ENABLED) continue;
                if (!GameClientSession.IsRoundActionPhase(matchingId)) continue;
                if (!_botPlayerManager.HasBots(matchingId)) continue;

                ProcessBotTargetProximityChecklistProgress(
                    matchingId,
                    activeSessions,
                    ChecklistProgressTickIntervalSeconds);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "체크리스트 진행 틱 처리 중 오류");
        }
    }

    private void ProcessResourceTick(object? state)
    {
        try
        {
            var activeSessions = _clientSessions.Values
                .Where(s => s.PlayerId.HasValue && !s.IsEliminated)
                .ToList();

            // Environmental damage and eliminations are settled per matching below.
            var matchingIds = GetActiveMatchingIds();

            foreach (long matchingId in matchingIds)
            {
                // #222 M3-2: 스웜에서도 폐쇄·오버타임 오염은 이 정산 틱이 적용한다.
                // SPOT_ARENA_P0_ENABLED 게이트가 스웜까지 막아 폐쇄 구역이 무해했던 버그 수정.
                // 레거시 부수 시스템(패시브 소환석 수입·봇 사보타주)은 스웜에서 계속 차단.
                if (Config.SWARM_P0_ENABLED)
                {
                    if (GameClientSession.IsRoundActionPhase(matchingId))
                        ProcessSurvivorResourceTickForMatching(matchingId, activeSessions);
                    continue;
                }

                if (Config.SPOT_ARENA_P0_ENABLED) continue;
                if (!GameClientSession.IsRoundActionPhase(matchingId)) continue;
                ProcessSurvivorResourceTickForMatching(matchingId, activeSessions);
                ProcessPassiveSummonStoneIncomeForMatching(matchingId, activeSessions);

                // 봇 미션 처리(부품 회수/결합/RNG 채집)는 별도 1초 타이머(ProcessBotMission)에서 수행.

            }

            // 7. 프로토 0 기척 틱 (#159) — 5초 조우 강도 계산 후 인간 세션에 전송
            foreach (long matchingId in matchingIds)
            {
                if (Config.SPOT_ARENA_P0_ENABLED) continue;
                if (!GameClientSession.IsRoundActionPhase(matchingId)) continue;
                var playerAreas = BuildPlayerAreas(matchingId, activeSessions);
                _presenceTracker.Tick(matchingId, playerAreas);
                int roundNumber = 0 /* 라운드 시스템 퇴역(#246) */;

                foreach (var session in activeSessions)
                {
                    if (session.CurrentMapSubId != matchingId || !session.PlayerId.HasValue) continue;

                    // roster = 현재 살아있는 전체 플레이어 → 타겟 제외 후보 전원(presence 0 포함)
                    var scored = _presenceTracker.GetCandidates(
                        matchingId, session.PlayerId.Value, session.TargetPlayerId, playerAreas.Keys);

                    var candidates = new List<(long playerId, float presence, string name, List<int> wear)>();
                    foreach (var (candidateId, presence) in scored)
                    {
                        string name = "";
                        List<int> wear = null;
                        // 봇은 서버 메모리에 정체성 보유 → 후보가 멀리 있어도 카드 채움. 인간은 빈값(클라가 폴백).
                        if (BotPlayerManager.IsBotPlayerId(candidateId))
                        {
                            var info = _botPlayerManager.SynthesizePlayerInfo(matchingId, candidateId);
                            if (info != null) { name = info.Name; wear = info.WearItemIdList; }
                        }

                        candidates.Add((candidateId, presence, name, wear));
                    }

                    session.SendPresenceUpdate(candidates);

                    var notebookRecords = _presenceTracker.GetNotebookRecords(
                        matchingId,
                        session.PlayerId.Value,
                        playerAreas.Keys,
                        includeEmpty: true);
                    session.SendPresenceNotebookUpdate(matchingId, roundNumber, notebookRecords);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "자원 틱 처리 중 오류");
        }
    }

    /// <summary>프로토 0 기척: 인스턴스의 모든 플레이어(인간 + 생존 봇) 영역 맵.</summary>
    private void ProcessPassiveSummonStoneIncomeForMatching(
        long matchingId,
        List<GameClientSession> activeSessions)
    {
        int awardedPlayerCount = 0;
        foreach (var session in activeSessions)
        {
            if (session.CurrentMapSubId != matchingId || !session.PlayerId.HasValue)
                continue;

            var state = _summonStoneManager.AdvancePassiveIncome(
                matchingId,
                session.PlayerId.Value,
                ResourceTickIntervalSeconds,
                out int awardedStones);
            if (awardedStones <= 0)
                continue;

            var source = session.LastValidatedPosition;
            session.SendSummonStoneState(
                awardedStones,
                source?.X ?? 0f,
                source?.Y ?? 0f);
            awardedPlayerCount++;
        }

        int awardedBotCount = 0;
        foreach (var bot in _botPlayerManager.GetBots(matchingId).Where(candidate => !candidate.IsEliminated))
        {
            _summonStoneManager.AdvancePassiveIncome(
                matchingId,
                bot.PlayerId,
                ResourceTickIntervalSeconds,
                out int awardedStones);
            if (awardedStones > 0)
                awardedBotCount++;
        }

        if (awardedPlayerCount > 0 || awardedBotCount > 0)
            logger.LogInformation(
                "Passive summon stone income granted: MatchingId={MatchingId}, Players={PlayerCount}, Bots={BotCount}, Amount={Amount}",
                matchingId,
                awardedPlayerCount,
                awardedBotCount,
                SummonStoneManager.PassiveIncomeAmount);
    }
    private Dictionary<long, AreaType> BuildPlayerAreas(long matchingId, List<GameClientSession> activeSessions)
    {
        var areas = new Dictionary<long, AreaType>();
        foreach (var session in activeSessions)
            if (session.CurrentMapSubId == matchingId && session.PlayerId.HasValue)
                areas[session.PlayerId.Value] = session.CurrentArea;

        if (_botPlayerManager.HasBots(matchingId))
            foreach (var bot in _botPlayerManager.GetBots(matchingId))
                if (!bot.IsEliminated)
                    areas[bot.PlayerId] = bot.CurrentArea;

        return areas;
    }

    private void ProcessBotTargetProximityChecklistProgress(
        long matchingId,
        List<GameClientSession> activeSessions,
        float deltaSeconds)
    {
        foreach (var bot in _botPlayerManager.GetBots(matchingId))
        {
            if (bot.IsEliminated || bot.TargetPlayerId == 0 || bot.CurrentArea == AreaType.None)
                continue;

            var targetSession = activeSessions.FirstOrDefault(s =>
                s.PlayerId == bot.TargetPlayerId &&
                s.CurrentMapSubId == matchingId &&
                !s.IsEliminated);
            var targetBot = targetSession == null
                ? _botPlayerManager.GetBot(matchingId, bot.TargetPlayerId)
                : null;
            if (targetSession == null && targetBot is not { IsEliminated: false })
                continue;

            bool sameArea = targetSession?.CurrentArea == bot.CurrentArea || targetBot?.CurrentArea == bot.CurrentArea;
            if (!sameArea || !IsBotTargetWithinProximity(bot, targetSession, targetBot))
                continue;

            var progress = _checklistManager.AdvanceActiveTaskProgress(
                matchingId,
                bot.PlayerId,
                "MANITTO_STAY_NEAR_TARGET_20",
                deltaSeconds);
            if (progress.Completion?.CompletedTask != null)
            {
                logger.LogInformation(
                    "Bot proximity checklist completed: MatchingId={MatchingId}, BotId={BotId}, TaskId={TaskId}",
                    matchingId, bot.PlayerId, progress.Completion.CompletedTask.TaskId);
            }

        }
    }

    /// <summary>타겟 근접 체크리스트 판정용 평면 거리 확인.</summary>
    private static bool IsTargetWithinProximity(
        GameClientSession session, GameClientSession? targetSession, BotPlayerState? targetBot)
    {
        var myPosition = session.LastValidatedPosition;
        if (myPosition == null) return false;

        var targetPosition = targetSession?.LastValidatedPosition ?? targetBot?.Position;
        if (targetPosition == null) return false;

        float dx = myPosition.X - targetPosition.X;
        float dy = myPosition.Y - targetPosition.Y;
        return dx * dx + dy * dy <=
               Config.TARGET_PROXIMITY_DISTANCE * Config.TARGET_PROXIMITY_DISTANCE;
    }

    private static bool IsBotTargetWithinProximity(
        BotPlayerState bot, GameClientSession? targetSession, BotPlayerState? targetBot)
    {
        var targetPosition = targetSession?.LastValidatedPosition ?? targetBot?.Position;
        if (targetPosition == null) return false;

        float dx = bot.Position.X - targetPosition.X;
        float dy = bot.Position.Y - targetPosition.Y;
        return dx * dx + dy * dy <=
               Config.TARGET_PROXIMITY_DISTANCE * Config.TARGET_PROXIMITY_DISTANCE;
    }

    internal static int ResolveStatusEffectCorruptionDelta(int statusEffectId, int value)
    {
        if (value == 0) return 0;
        if (!GameStatusEffectData.TryGet(statusEffectId, out var statusEffect) || statusEffect.BuffId <= 0)
            return 0;

        var buff = GameBuffData.Get(statusEffect.BuffId);
        int magnitude = Math.Abs(value);

        return buff.SubType switch
        {
            BuffSubType.CORRUPTION_ADD => magnitude,
            BuffSubType.CORRUPTION_DOWN => -magnitude,
            _ => 0
        };
    }

    /// <summary>
    ///     #26: 봇 자원 고갈 탈락 시 체인 단절 처리 + 게임 종료 판정.
    ///     ManittoChainManager.EliminatePlayer로 체인 단절 (마니또 시한부 / 타겟 해방 등) 일괄 적용.
    /// </summary>
    private void ProcessBotElimination(long matchingId, long botId, EliminationReason reason,
        List<GameClientSession> activeSessions, long attackerPlayerId = 0, bool isAreaClosureElimination = false,
        bool isOvertimeElimination = false, bool deferGameOver = false, int forcedRank = 0)
    {
        try
        {
            var eliminatedBot = _botPlayerManager.GetBot(matchingId, botId);
            AreaType eliminatedArea = eliminatedBot?.CurrentArea ?? AreaType.None;
            int finalOrbTier = ResolveFinalOrbTier(matchingId, botId);
            var transition = _manittoChainManager.TryEliminatePlayer(matchingId, botId, reason,
                attackerPlayerId, eliminatedArea, isAreaClosureElimination, isOvertimeElimination, forcedRank,
                finalOrbTier);
            if (!transition.Applied)
            {
                logger.LogDebug(
                    "Duplicate bot elimination ignored: MatchingId={MatchingId}, BotId={BotId}, Reason={Reason}",
                    matchingId, botId, reason);
                return;
            }

            var affected = transition.AffectedPlayers;
            _groundItemManager.ReleaseClaimReservationsForPlayer(matchingId, botId);
            _gameEventLogManager.LogElimination(
                matchingId,
                botId,
                reason.ToString(),
                isBot: true,
                attackerPlayerId: attackerPlayerId,
                isAreaClosureElimination: isAreaClosureElimination,
                isOvertimeElimination: isOvertimeElimination);
            var matchingSessions = _clientSessions.Values
                .Where(s => s.PlayerId.HasValue && s.CurrentMapSubId == matchingId)
                .ToList();
            DropBotInventoryAtCurrentPosition(matchingId, botId, matchingSessions);

            // 1) 전체에게 봇 탈락 알림 (G_TO_C_PLAYER_ELIMINATED)
            using (var eliminatedPacket = Packet.Create((int)Protocol.G_TO_C_PLAYER_ELIMINATED))
            {
                var eliminatedMsg = new G_TO_C_PLAYER_ELIMINATED
                {
                    PlayerId = botId,
                    AttackerPlayerId = attackerPlayerId,
                    Reason = reason
                };
                eliminatedPacket.SetBody(MessagePackSerializer.Serialize(eliminatedMsg));
                foreach (var s in matchingSessions) s.Send(eliminatedPacket);
            }

            // 2) 영향받는 봇/세션 상태 동기화 + 체인 단절 알림
            foreach (var (affectedId, newStatus) in affected)
            {
                var session = matchingSessions.FirstOrDefault(s => s.PlayerId == affectedId);
                if (session != null)
                {
                    session.ApplyChainBreakStatus(newStatus, botId);
                    continue;
                }

                // 봇 영향
                var bot = _botPlayerManager.GetBot(matchingId, affectedId);
                if (bot == null) continue;
                if (newStatus == ManittoStatus.ELIMINATED)
                {
                    bot.IsEliminated = true;
                    bot.ManittoStatus = ManittoStatus.SPECTATING;
                }
                else
                {
                    bot.ManittoStatus = newStatus;
                }
            }


            // 3) 게임 종료 판정 — 봇 탈락으로 최후 1인 결정 가능
            var (isGameOver, winnerId) = _manittoChainManager.CheckGameOver(matchingId);
            if (!deferGameOver && isGameOver && matchingSessions.Count > 0)
            {
                logger.LogInformation("게임 종료(봇 탈락 후): MatchingId={MatchingId}, Winner={WinnerId}",
                    matchingId, winnerId);
                matchingSessions[0].TryEndSurvivorMatch(winnerId ?? 0, "last_survivor_after_combat");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "봇 탈락 처리 중 오류: BotId={BotId}", botId);
        }
    }

    private void DropBotInventoryAtCurrentPosition(
        long matchingId,
        long botPlayerId,
        IReadOnlyCollection<GameClientSession> matchingSessions)
    {
        var bot = _botPlayerManager.GetBot(matchingId, botPlayerId);
        if (bot == null || bot.CurrentArea == AreaType.None)
            return;

        var drop = EliminationInventoryDropper.DropAll(
            _inGameInventoryManager,
            _groundItemManager,
            matchingId,
            botPlayerId,
            bot.CurrentArea,
            bot.Position.X,
            bot.Position.Y,
            _botPlayerManager.GetMatchingMapId(matchingId));
        if (drop.RemovedItems.Count == 0)
            return;

        var emptyBoard = _inGameInventoryManager.GetPlayerInventory(matchingId, botPlayerId);
        _gameEventLogManager.LogSurvivorOrbBoardTransition(
            matchingId, botPlayerId, emptyBoard.GetAllItems(), 0, bot.CurrentArea.ToString(), "elimination_drop",
            isBot: true);

        if (drop.DroppedItemIds.Count == 0)
            return;

        _gameEventLogManager.LogEliminationDrop(
            matchingId,
            botPlayerId,
            bot.CurrentArea.ToString(),
            drop.DroppedItemIds,
            drop.SpawnedItems,
            GameEventLogManager.CalculateDropRecoveryTotal(drop.DroppedItemIds),
            isBot: true);

        BroadcastGroundItemSpawnChunked(
            matchingId, bot.CurrentArea, drop.SpawnedItems,
            matchingSessions.Where(session => session.CurrentArea == bot.CurrentArea));

        logger.LogInformation(
            "Bot elimination inventory scattered: MatchingId={MatchingId}, BotId={BotId}, Area={Area}, ItemCount={ItemCount}",
            matchingId, botPlayerId, bot.CurrentArea, drop.DroppedItemIds.Count);
    }
    // 바닥 아이템 스폰 브로드캐스트는 반드시 청크로 나눈다 (#229).
    // 단일 패킷은 버퍼 2048에 묶여 있는데 탈락 드롭은 개수가 열려 있다 — 오브 상한이 99로
    // 오르고 소환석 예산이 커지면서 실제로 넘겼다(실측 2091, 봇 탈락 처리 전체가 예외로 죽어
    // 드롭이 통째로 사라졌다). SpotArena가 #222에서 같은 이유로 8개씩 나눈 전례를 따른다.
    private const int GroundItemSpawnBroadcastChunkSize = 8;

    private void BroadcastGroundItemSpawnChunked(
        long matchingId, AreaType area, IReadOnlyList<GroundItemInfo> spawned,
        IEnumerable<GameClientSession> targets)
    {
        if (spawned == null || spawned.Count == 0 || area == AreaType.None)
            return;

        var receivers = targets.ToList();
        if (receivers.Count == 0)
            return;

        int remaining = _areaItemStockManager.GetRemainingCount(matchingId, (int)area);
        for (int offset = 0; offset < spawned.Count; offset += GroundItemSpawnBroadcastChunkSize)
        {
            var chunk = spawned.Skip(offset).Take(GroundItemSpawnBroadcastChunkSize).ToList();
            using var packet = PacketMaker.G_TO_C_GROUND_ITEM_SPAWN((int)area, remaining, chunk);
            foreach (var session in receivers)
                session.Send(packet);
        }
    }

    private void BroadcastSurvivorAreaStockState(long matchingId, List<GameClientSession> sessions)
    {
        var message = new G_TO_C_SURVIVOR_AREA_STOCK_STATE
        {
            Areas = _areaItemStockManager.GetPublicDepletionSnapshot(matchingId)
                .Select(state => new SurvivorAreaNaturalStockState
                {
                    AreaType = state.AreaType,
                    IsDepleted = state.IsDepleted,
                    AvailableOrbColors = state.AvailableOrbColors
                })
                .ToList()
        };
        using var packet = Packet.Create((int)Protocol.G_TO_C_SURVIVOR_AREA_STOCK_STATE);
        packet.SetBody(MessagePackSerializer.Serialize(message));
        foreach (var session in sessions.Where(session => session.CurrentMapSubId == matchingId))
            session.Send(packet);
    }

    private void BroadcastNaturalStockRefresh(long matchingId, List<GameClientSession> sessions,
        IEnumerable<AreaType> areas)
    {
        foreach (var area in areas.Distinct())
        {
            var items = _groundItemManager.GetSnapshot(matchingId, area);
            int remaining = _areaItemStockManager.GetRemainingCount(matchingId, (int)area);
            using var packet = PacketMaker.G_TO_C_GROUND_ITEM_SNAPSHOT((int)area, remaining, items);
            foreach (var session in sessions) session.Send(packet);
        }
    }
    /// <summary>
    ///     #134 — 봇 RNG progress 시작을 같은 영역 인간 세션에 G_TO_C_EXPLORE_START broadcast.
    ///     클라가 봇 캐릭터를 EXPLORE_1 상태로 설정 → 탐색 애니메이션 + 사운드 자동 재생.
    /// </summary>
    private void BroadcastBotExploreStarts(long matchingId,
        List<(long botId, int interactId, AreaType area)> starts,
        List<GameClientSession> activeSessions)
    {
        foreach (var (botId, interactId, area) in starts)
        {
            var sameAreaSessions = activeSessions
                .Where(s => s.PlayerId.HasValue && s.CurrentMapSubId == matchingId && s.CurrentArea == area)
                .ToList();
            if (sameAreaSessions.Count == 0) continue;

            var msg = new G_TO_C_EXPLORE_START { PlayerId = botId, InteractId = interactId };
            var body = MessagePackSerializer.Serialize(msg);
            foreach (var session in sameAreaSessions)
            {
                using var packet = Packet.Create((int)Protocol.G_TO_C_EXPLORE_START, session.PlayerId!.Value);
                packet.SetBody(body);
                session.Send(packet);
            }
        }
    }

    /// <summary>
    ///     #134 — 봇 RNG progress 종료 broadcast. 클라가 봇 캐릭터 EXPLORE_1 → IDLE 복귀.
    /// </summary>
    private void BroadcastBotExploreEnds(long matchingId,
        List<(long botId, AreaType area)> ends, List<GameClientSession> activeSessions)
    {
        foreach (var (botId, area) in ends)
        {
            var sameAreaSessions = activeSessions
                .Where(s => s.PlayerId.HasValue && s.CurrentMapSubId == matchingId && s.CurrentArea == area)
                .ToList();
            if (sameAreaSessions.Count == 0) continue;

            var msg = new G_TO_C_EXPLORE_END { PlayerId = botId };
            var body = MessagePackSerializer.Serialize(msg);
            foreach (var session in sameAreaSessions)
            {
                using var packet = Packet.Create((int)Protocol.G_TO_C_EXPLORE_END, session.PlayerId!.Value);
                packet.SetBody(body);
                session.Send(packet);
            }
        }
    }

    private void BroadcastBotBattleItemEquips(long matchingId,
IReadOnlyCollection<(long botPlayerId, int itemId)> equips,
IReadOnlyCollection<GameClientSession> activeSessions)
    {
        foreach (var (botPlayerId, _) in equips)
        {
            var bot = _botPlayerManager.GetBot(matchingId, botPlayerId);
            var botInfo = _botPlayerManager.SynthesizePlayerInfo(matchingId, botPlayerId);
            if (bot == null || botInfo == null) continue;

            var sameAreaSessions = activeSessions
                .Where(session => session.PlayerId.HasValue &&
                                  session.CurrentMapSubId == matchingId &&
                                  session.CurrentArea == bot.CurrentArea)
                .ToList();
            if (sameAreaSessions.Count == 0) continue;

            using var packet = PacketMaker.G_TO_C_PLAYER_INFO([botInfo]);
            foreach (var session in sameAreaSessions)
                session.Send(packet);
        }
    }
    /// <summary>
    ///     #125: 봇 이동 이벤트를 같은 매칭의 영향권 인간 세션에 패킷 브로드캐스트.
    ///     - 영역 전환: G_TO_C_AREA_PLAYER_LEAVE(이전 영역) + G_TO_C_AREA_PLAYER_ENTER(새 영역) + G_TO_C_MOVE(텔레포트)
    ///     - 영역 내 wander: G_TO_C_MOVE(같은 영역)
    /// </summary>
    private void BroadcastBotGroundItemPickups(
        long matchingId,
        IReadOnlyCollection<BotGroundItemPickup> pickups,
        IReadOnlyCollection<GameClientSession> activeSessions)
    {
        foreach (var pickup in pickups)
        {
            if (pickup.CorruptionRecovery > 0)
                _gameEventLogManager.RecordSurvivorRecovery(matchingId, pickup.BotPlayerId, pickup.CorruptionRecovery);
            if (pickup.AutoUsed)
            {
                _gameEventLogManager.LogRecoveryUse(
                    matchingId, pickup.BotPlayerId, pickup.Item.ItemId, pickup.EffectiveRecovery,
                    source: "ground_auto_use", isBot: true);
                _gameEventLogManager.LogPelletPickupOutcome(
                    matchingId, pickup.BotPlayerId, pickup.Item.ItemId, pickup.RequestedRecovery, pickup.EffectiveRecovery,
                    pickup.EffectiveRecovery == 0 ? "wasted" :
                    pickup.EffectiveRecovery == pickup.RequestedRecovery ? "effective" : "partial_waste",
                    isBot: true);
            }

            var area = (AreaType)pickup.Item.AreaType;
            _gameEventLogManager.LogGroundItemPickup(
                matchingId,
                pickup.BotPlayerId,
                pickup.DiscovererPlayerId,
                pickup.Item.GroundItemUid,
                pickup.Item.ItemId,
                area.ToString(),
                pickup.AutoUsed,
                isBot: true);
            if (pickup.SummonStoneAmount > 0)
            {
                _gameEventLogManager.LogSummonStoneAward(
                    matchingId,
                    pickup.BotPlayerId,
                    monsterId: 0,
                    pickup.SummonStoneAmount,
                    pickup.SummonStoneBalance,
                    area.ToString(),
                    isCore: false,
                    isBot: true);
            }
            else
            {
                var boardAfterPickup = _inGameInventoryManager.GetPlayerInventory(matchingId, pickup.BotPlayerId);
                _gameEventLogManager.LogSurvivorOrbBoardTransition(
                    matchingId, pickup.BotPlayerId, boardAfterPickup.GetAllItems(),
                    boardAfterPickup.GetEquippedBattleItem()?.ItemId ?? 0, area.ToString(), "pickup", isBot: true);
            }
            using var packet = PacketMaker.G_TO_C_GROUND_ITEM_REMOVED(
                pickup.Item.GroundItemUid,
                pickup.BotPlayerId,
                pickup.AutoUsed);
            foreach (var session in activeSessions.Where(session =>
                         session.CurrentMapSubId == matchingId && session.CurrentArea == area))
            {
                session.Send(packet);
            }
        }

        var autoEquips = pickups
            .Where(pickup => pickup.AutoEquippedItemId > 0)
            .Select(pickup => (pickup.BotPlayerId, pickup.AutoEquippedItemId))
            .ToList();
        if (autoEquips.Count > 0)
            BroadcastBotBattleItemEquips(matchingId, autoEquips, activeSessions);
    }
    private void BroadcastBotMovement(long matchingId, BotMovementEvent ev,
        List<GameClientSession> activeSessions)
    {
        var matchingSessions = activeSessions
            .Where(s => s.PlayerId.HasValue && s.CurrentMapSubId == matchingId)
            .ToList();

        long serverTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (ev.IsAreaTransition)
        {
            _presenceTracker.SetPlayerArea(matchingId, ev.BotPlayerId, ev.ToArea, countAsEntry: true);

            _gameEventLogManager.LogMove(matchingId, ev.BotPlayerId,
                ev.FromArea.ToString(), ev.ToArea.ToString(), isBot: true);
            var core = _emotionAfterimageMonsterManager.GetSnapshot(matchingId, ev.ToArea)
                .FirstOrDefault(monster => monster.IsAlive && monster.IsCore);
            _gameEventLogManager.LogCoreContestedEntry(
                matchingId,
                ev.BotPlayerId,
                ev.ToArea.ToString(),
                core,
                isBot: true);

            if (matchingSessions.Count == 0) return;

            // 1) 이전 영역의 인간들에게 LEAVE
            using var leavePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(ev.BotPlayerId);
            foreach (var session in matchingSessions)
            {
                if (session.CurrentArea != ev.FromArea) continue;
                session.Send(leavePacket);
            }

            // 2) 새 영역의 인간들에게 ENTER (합성 PlayerInfo)
            var botInfo = _botPlayerManager.SynthesizePlayerInfo(matchingId, ev.BotPlayerId);
            if (botInfo != null)
            {
                using var enterPacket = PacketMaker.G_TO_C_AREA_PLAYER_ENTER(botInfo, ev.ToCell);
                foreach (var session in matchingSessions)
                {
                    if (session.CurrentArea != ev.ToArea) continue;
                    session.Send(enterPacket);
                }
            }
        }
        else if (matchingSessions.Count == 0)
        {
            return;
        }

        // 3) 새 영역의 인간들에게 MOVE (텔레포트 또는 wander)
        float orbOrbitPhase = _botPlayerManager.GetBot(matchingId, ev.BotPlayerId)?.OrbOrbitPhaseDegrees
                              ?? SwarmOrbOrbit.InitialPhaseDegrees(ev.BotPlayerId);
        using var movePacket = PacketMaker.G_TO_C_MOVE(
            ev.BotPlayerId,
            ev.Position,
            ev.Velocity,
            ev.Rotation,
            ev.ToCell,
            lastProcessedInput: 0u,
            serverTimestamp,
            orbOrbitPhase);
        foreach (var session in matchingSessions)
        {
            if (session.CurrentArea != ev.ToArea) continue;
            session.Send(movePacket);
        }

        TrySendBotCorridorEncounterEvent(matchingId, ev, matchingSessions);
        TrySendBotRoomEncounterEvent(matchingId, ev, matchingSessions);
    }

    private void TrySendBotCorridorEncounterEvent(
        long matchingId,
        BotMovementEvent ev,
        List<GameClientSession> matchingSessions)
    {
        if (!ev.ToArea.IsCorridor())
            return;

        var candidates = matchingSessions
            .Where(session =>
                session.PlayerId.HasValue &&
                !session.IsEliminated &&
                session.CurrentMapSubId == matchingId &&
                session.CurrentArea == ev.ToArea &&
                session.LastValidatedPosition != null)
            .Select(session => (session.PlayerId!.Value, session.LastValidatedPosition!))
            .ToList();
        if (candidates.Count == 0)
            return;

        var decision = _encounterRevealManager.ResolveCorridorEncounter(
            matchingId,
            ev.BotPlayerId,
            ev.Position,
            candidates.Select(entry => (entry.Item1, entry.Item2!)),
            PassiveBuffUtility.GetValuePercent(
                _botPlayerManager.GetBot(matchingId, ev.BotPlayerId)?.ActiveBuffIds ?? [],
                BuffSubType.RISK_EVENT_CHANCE_DOWN),
            PassiveBuffUtility.GetValuePercent(
                _botPlayerManager.GetBot(matchingId, ev.BotPlayerId)?.ActiveBuffIds ?? [],
                BuffSubType.ENCOUNTER_ESCAPE_CHANCE_ADD));
        if (!decision.HasEvent)
            return;

        var targetSession = matchingSessions.FirstOrDefault(session => session.PlayerId == decision.TargetPlayerId);
        if (targetSession == null)
            return;

        using var packet = PacketMaker.G_TO_C_ENCOUNTER_REVEAL(
            ev.BotPlayerId,
            ev.ToArea,
            decision.EventType,
            decision.CooldownSeconds,
            decision.RevealDelayMs);
        targetSession.Send(packet);

        logger.LogInformation(
            "Bot corridor encounter event: Matching={MatchingId}, Bot={Bot}, Target={Target}, Area={Area}, EventType={EventType}",
            matchingId,
            ev.BotPlayerId,
            decision.TargetPlayerId,
            ev.ToArea,
            decision.EventType);
    }

    private void TrySendBotRoomEncounterEvent(
        long matchingId,
        BotMovementEvent ev,
        List<GameClientSession> matchingSessions)
    {
        if (ev.ToArea == AreaType.None || ev.ToArea.IsCorridor())
            return;

        foreach (var session in matchingSessions)
        {
            if (!session.PlayerId.HasValue ||
                session.IsEliminated ||
                session.CurrentMapSubId != matchingId ||
                session.CurrentArea != ev.ToArea ||
                session.PlayerId.Value == ev.BotPlayerId)
            {
                continue;
            }

        }
    }

    // ===== 구역 폐쇄 틱 =====

    private void StartAreaClosureTickTimer()
    {
        // 1초 간격 — 클라 카운트다운 종료 시점과 실제 폐쇄 트리거 사이 지연을 최소화
        _areaClosureTickTimer = new Timer(ProcessAreaClosureTick, null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        logger.LogInformation("구역 폐쇄 타이머 시작 (1초 간격)");
    }

    private void BroadcastDoorStateChanges(
        IReadOnlyCollection<GameClientSession> sessions,
        IEnumerable<int> doorIds,
        bool isOpen,
        long openerPlayerId)
    {
        foreach (int doorId in doorIds.Distinct())
        {
            using var packet = PacketMaker.G_TO_C_DOOR_STATE_UPDATE(
                doorId,
                isOpen,
                ErrorCode.SUCCESS,
                openerPlayerId);
            foreach (var session in sessions)
                session.Send(packet);
        }
    }

    private void ProcessAreaClosureTick(object? state)
    {
        try
        {
            // 매칭별로 폐쇄 스케줄 체크
            var matchingIds = GetActiveMatchingIds();

            foreach (long matchingId in matchingIds)
            {
                if (Config.SWARM_P0_ENABLED)
                {
                    // #219 폐쇄 부활: 페이즈 머신 없이 시간 웨이브 스케줄로만 폐쇄한다.
                    // (자기장 틱은 SwarmFieldEnabled 게이트에 그대로 보관)
                    if (GameClientSession.IsRoundActionPhase(matchingId))
                    {
                        ProcessSwarmScheduledClosureTick(matchingId);
                        ProcessSwarmClosureTick(matchingId);
                    }
                    continue;
                }

                if (Config.SPOT_ARENA_P0_ENABLED) continue;
                if (!GameClientSession.IsRoundActionPhase(matchingId)) continue;
                var sessions = _clientSessions.Values
                    .Where(s => s.PlayerId.HasValue && s.CurrentMapSubId == matchingId)
                    .ToList();
                var bots = _botPlayerManager.GetBots(matchingId)
                    .Where(bot => !bot.IsEliminated)
                    .ToList();
                var alivePlayerIds = sessions
                    .Where(session => !session.IsEliminated)
                    .Select(session => session.PlayerId!.Value)
                    .Concat(bots.Select(bot => bot.PlayerId))
                    .ToArray();
                bool phaseWasUninitialized = !_survivorPhaseManager.HasMatching(matchingId);
                DateTime phaseStartUtc = MatchStartGate.GetGameplayStartedAtUtc(matchingId) ?? DateTime.UtcNow;
                var occupiedStartingRooms = sessions
                    .Where(session => !session.IsEliminated)
                    .Select(session => session.CurrentArea)
                    .Concat(bots.Select(bot => bot.CurrentArea))
                    .Where(area => area != AreaType.None)
                    .ToArray();
                _survivorPhaseManager.InitializeMatching(
                    matchingId,
                    phaseStartUtc,
                    occupiedStartingRooms);
                var phaseTick = _survivorPhaseManager.Tick(matchingId, alivePlayerIds);

                if (phaseWasUninitialized && phaseTick.Snapshot.Phase == SurvivorMatchPhase.ROOM_COMBAT)
                {
                    var closedDoorIds = _doorStateManager.CloseDoorsForAreas(
                        matchingId,
                        phaseTick.Snapshot.CurrentRooms);
                    BroadcastDoorStateChanges(sessions, closedDoorIds, false, 0);
                }

                foreach (var transition in phaseTick.Transitions)
                {
                    if (transition.Before.Phase == SurvivorMatchPhase.ROOM_COMBAT &&
                        transition.After.Phase == SurvivorMatchPhase.ROOM_CLOSURE_WARNING)
                    {
                        var openedDoorIds = _doorStateManager.OpenDoorsForAreas(
                            matchingId,
                            transition.Before.CurrentRooms);
                        BroadcastDoorStateChanges(sessions, openedDoorIds, true, -1);
                    }

                    if (transition.After.Phase == SurvivorMatchPhase.ROOM_COMBAT)
                    {
                        var closedDoorIds = _doorStateManager.CloseDoorsForAreas(
                            matchingId,
                            transition.After.CurrentRooms);
                        BroadcastDoorStateChanges(sessions, closedDoorIds, false, 0);
                    }
                }

                var phaseAreaDelta = _areaClosureManager.ApplyPhaseSnapshot(matchingId, phaseTick.Snapshot);
                var closureTick = new ClosureScheduleTick(
                    phaseAreaDelta.WarningAreas,
                    phaseAreaDelta.WarningSeconds,
                    phaseAreaDelta.TransitionAtUnixMs,
                    phaseAreaDelta.ClosedAreas);
                var globalClosureTick = GlobalClosureTick.Empty;

                var visibleNextRooms = phaseTick.Snapshot.Phase is
                    SurvivorMatchPhase.CORRIDOR_ENTRY or
                    SurvivorMatchPhase.CORRIDOR_COMBAT or
                    SurvivorMatchPhase.ROOM_SELECTION or
                    SurvivorMatchPhase.CORRIDOR_CLOSURE_WARNING
                        ? phaseTick.Snapshot.NextRooms
                        : [];
                int[] visibleNextRoomAreaTypes = visibleNextRooms
                    .Select(area => (int)area)
                    .ToArray();
                int[] visibleNextRoomOccupancies = visibleNextRooms
                    .Select(area => Math.Min(2,
                        sessions.Count(session => !session.IsEliminated && session.CurrentArea == area) +
                        bots.Count(bot => !bot.IsEliminated && bot.CurrentArea == area)))
                    .ToArray();
                using (var phasePacket = PacketMaker.G_TO_C_ROUND_STATE(
                           matchingId,
                           phaseTick.Snapshot.StageIndex + 1,
                           5,
                           SurvivorPhaseManager.ToRoundPhase(phaseTick.Snapshot.Phase),
                           phaseTick.Snapshot.RemainingSeconds,
                           SurvivorPhaseManager.GetPhaseDurationSeconds(phaseTick.Snapshot),
                           phaseTick.Snapshot.Phase == SurvivorMatchPhase.FINISHED,
                           visibleNextRoomAreaTypes,
                           visibleNextRoomOccupancies))
                {
                    foreach (var session in sessions) session.Send(phasePacket);
                }

                var monsterPhaseStates = new Dictionary<int, MonsterRuntimeInfo>();
                if (phaseWasUninitialized || phaseTick.Transitions.Count > 0)
                {
                    bool beginRoomWave = phaseTick.Snapshot.Phase is
                        SurvivorMatchPhase.ROOM_COMBAT or SurvivorMatchPhase.FINAL;
                    foreach (var monster in _emotionAfterimageMonsterManager.ApplyPhaseSnapshot(
                                 matchingId,
                                 phaseTick.Snapshot.OpenAreas,
                                 beginRoomWave,
                                 phaseTick.Snapshot.StageIndex,
                                 DateTime.UtcNow))
                        monsterPhaseStates[monster.MonsterId] = monster;

                    if (phaseWasUninitialized)
                        _gameEventLogManager.LogSurvivorPhaseTransition(
                            matchingId, SurvivorPhaseSnapshot.Empty, phaseTick.Snapshot);

                    foreach (var transition in phaseTick.Transitions)
                    {
                        _gameEventLogManager.LogSurvivorPhaseTransition(
                            matchingId, transition.Before, transition.After);
                        logger.LogInformation(
                            "Survivor phase transition: MatchingId={MatchingId}, Stage={Stage}, From={From}, To={To}, OpenAreas={OpenAreas}",
                            matchingId,
                            transition.After.StageIndex,
                            transition.Before.Phase,
                            transition.After.Phase,
                            string.Join(',', transition.After.OpenAreas));
                    }
                }

                // 수명이 다한 낙수를 걷어낸다 (#229 절단 낙수). 같은 구역 인원에게만 알린다.
                foreach (var expiredItem in _groundItemManager.ExpireGroundItems(matchingId))
                {
                    using var removedPacket = PacketMaker.G_TO_C_GROUND_ITEM_REMOVED(
                        expiredItem.GroundItemUid, 0, false);
                    foreach (var session in GetSessionsByInstance(MapId.School, matchingId))
                    {
                        if (session.PlayerId.HasValue &&
                            session.CurrentArea == (AreaType)expiredItem.AreaType)
                            session.Send(removedPacket);
                    }
                }

                foreach (var expired in _groundItemManager.ExpireClaimReservations(matchingId))
                    _gameEventLogManager.LogGroundItemPriorityExpired(
                        matchingId,
                        expired.DiscovererPlayerId,
                        expired.GroundItemUid,
                        expired.ItemId,
                        expired.ExpiresAtUnixMs);

                _gameEventLogManager.FlushElapsedEliminationDrops(
                    matchingId,
                    groundItemUid => _groundItemManager.Exists(matchingId, groundItemUid));

                var (overtimeStage, overtimeRate) = _areaClosureManager.GetOvertimeStatus(matchingId);
                _gameEventLogManager.LogOvertimeStageChanged(matchingId, overtimeStage, overtimeRate);

                if (closureTick.WarningAreas.Count > 0)
                {
                    // #214 room phases use monsters and summon stones as their economy. The old
                    // exploration-stock refresh remains available only to legacy closure matches.
                    if (!_survivorPhaseManager.HasMatching(matchingId))
                    {
                        var closureState = _areaClosureManager.GetClientStateSnapshot(matchingId);
                        var replenished = _areaItemStockManager.ReplenishForClosureWarning(
                            matchingId,
                            closureTick.ClosureAtUnixMs,
                            closureTick.WarningAreas,
                            closureState.ClosedAreas);
                        if (replenished.Count > 0)
                        {
                            BroadcastSurvivorAreaStockState(matchingId, sessions);
                            BroadcastNaturalStockRefresh(matchingId, sessions, replenished.Select(entry => entry.AreaType));
                            logger.LogInformation(
                                "Survivor Royale closure supply added: MatchingId={MatchingId}, Supply={Supply}",
                                matchingId,
                                string.Join(',', replenished.Select(entry => $"{entry.AreaType}:{entry.ItemId}")));
                        }
                    }

                    var warningAreas = closureTick.WarningAreas.Select(area => area.ToString()).ToList();
                    foreach (var session in sessions.Where(session => !session.IsEliminated))
                    {
                        long playerId = session.PlayerId!.Value;
                        int slots = _inGameInventoryManager.GetPlayerInventory(matchingId, playerId)
                            .GetAllItems().Count;
                        _gameEventLogManager.LogClosureWarningSnapshot(
                            matchingId,
                            playerId,
                            warningAreas,
                            session.CurrentArea.ToString(),
                            session.CurrentCorruption,
                            slots,
                            Config.SURVIVOR_INVENTORY_SLOT_COUNT,
                            _areaItemStockManager.GetRemainingCount(matchingId, (int)session.CurrentArea),
                            closureTick.ClosureAtUnixMs,
                            isBot: false);
                    }

                    foreach (var bot in _botPlayerManager.GetBots(matchingId).Where(bot => !bot.IsEliminated))
                    {
                        int slots = _inGameInventoryManager.GetPlayerInventory(matchingId, bot.PlayerId)
                            .GetAllItems().Count;
                        _gameEventLogManager.LogClosureWarningSnapshot(
                            matchingId,
                            bot.PlayerId,
                            warningAreas,
                            bot.CurrentArea.ToString(),
                            bot.Corruption,
                            slots,
                            Config.SURVIVOR_INVENTORY_SLOT_COUNT,
                            _areaItemStockManager.GetRemainingCount(matchingId, (int)bot.CurrentArea),
                            closureTick.ClosureAtUnixMs,
                            isBot: true);
                    }
                }

                foreach (var reopenedArea in phaseAreaDelta.ReopenedAreas)
                {
                    using var packet = Packet.Create((int)Protocol.G_TO_C_AREA_CLOSED);
                    packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_AREA_CLOSED
                    {
                        AreaType = reopenedArea,
                        IsClosed = false
                    }));
                    foreach (var session in sessions) session.Send(packet);
                }
                foreach (var warningArea in closureTick.WarningAreas)
                {
                    using var packet = Packet.Create((int)Protocol.G_TO_C_AREA_CLOSURE_WARNING);
                    var msg = new G_TO_C_AREA_CLOSURE_WARNING
                    {
                        AreaType = warningArea,
                        SecondsRemaining = closureTick.WarningSeconds,
                        ClosureAtUnixMs = closureTick.ClosureAtUnixMs
                    };
                    packet.SetBody(MessagePackSerializer.Serialize(msg));
                    foreach (var session in sessions) session.Send(packet);
                }

                if (monsterPhaseStates.Count > 0)
                {
                    BroadcastMonsterSnapshot(matchingId, sessions, monsterPhaseStates.Values);
                    BroadcastMonsterMinimapSnapshot(matchingId, sessions, monsterPhaseStates.Values);
                }
                if (closureTick.ClosedAreas.Count > 0)
                {
                    _gameEventLogManager.LogRewardAreaSnapshot(
                        matchingId,
                        _emotionAfterimageMonsterManager.GetRewardAreaSnapshot(matchingId),
                        "closure");
                }

                foreach (var closedArea in closureTick.ClosedAreas)
                {
                    _gameEventLogManager.LogClosure(matchingId, closedArea.ToString());

                    using var packet = Packet.Create((int)Protocol.G_TO_C_AREA_CLOSED);
                    var msg = new G_TO_C_AREA_CLOSED
                    {
                        AreaType = closedArea,
                        SuppressAlert = phaseWasUninitialized
                    };
                    packet.SetBody(MessagePackSerializer.Serialize(msg));
                    foreach (var session in sessions) session.Send(packet);
                }
                // 폐쇄 = 문 잠금 + 틱 오염 (2026-08-18 유저 결정, #227 즉사 퇴역): 닫히면 문이 잠기고
                // 안에 남은 사람은 정산 틱 오염을 받으며 자기 구역 문을 게이지로 따고 나갈 수 있다.
                if (closureTick.ClosedAreas.Count > 0)
                {
                    var lockedDoorIds = _doorStateManager.CloseDoorsForAreas(
                        matchingId, closureTick.ClosedAreas);
                    BroadcastDoorStateChanges(sessions, lockedDoorIds, false, 0);
                }

                if (globalClosureTick.HasTransition)
                {
                    using var packet = Packet.Create((int)Protocol.G_TO_C_AREA_CLOSURE_WARNING);
                    packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_AREA_CLOSURE_WARNING
                    {
                        AreaType = AreaType.None,
                        SecondsRemaining = globalClosureTick.SecondsRemaining,
                        ClosureAtUnixMs = globalClosureTick.ClosureAtUnixMs,
                        IsGlobalClosure = true,
                        IsGlobalClosureActive = globalClosureTick.IsActive
                    }));
                    foreach (var session in sessions) session.Send(packet);
                }

                if (closureTick.WarningAreas.Count > 0 || closureTick.ClosedAreas.Count > 0)
                {
                    var snapshot = _areaClosureManager.GetClientStateSnapshot(matchingId);
                    using var packet = Packet.Create((int)Protocol.G_TO_C_AREA_CLOSURE_WARNING);
                    packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_AREA_CLOSURE_WARNING
                    {
                        AreaType = AreaType.None,
                        SecondsRemaining = snapshot.NextWarningSeconds,
                        ClosureAtUnixMs = snapshot.NextWarningAtUnixMs
                    }));
                    foreach (var session in sessions) session.Send(packet);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "구역 폐쇄 틱 처리 중 오류");
        }
    }

    // ===== 타겟 위치 전송 =====

    private void StartTargetLocationTimer()
    {
        _targetLocationTimer = new Timer(ProcessTargetLocation, null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        logger.LogInformation("타겟 위치 전송 타이머 시작 (1초 간격)");
    }

    private void ProcessTargetLocation(object? state)
    {
        try
        {
            var activeSessions = _clientSessions.Values
                .Where(s => s.PlayerId.HasValue && s.TargetPlayerId != 0)
                .ToList();

            foreach (var session in activeSessions)
            {
                session.SendTargetLocation();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "타겟 위치 전송 처리 중 오류");
        }
    }

    // ===== #127 봇 walking 타이머 =====

    private const int BotMovementTickIntervalMs = 50; // 봇 walking step 주기 (실제 플레이어 sendInterval=50ms와 동등 — 클라 보간 일치)

    private void StartBotMovementTimer()
    {
        _botMovementTimer = new Timer(ProcessBotMovement, null,
            TimeSpan.FromMilliseconds(BotMovementTickIntervalMs),
            TimeSpan.FromMilliseconds(BotMovementTickIntervalMs));
        logger.LogInformation("봇 walking 타이머 시작 ({Ms}ms 간격)", BotMovementTickIntervalMs);
    }

    private void ProcessBotMovement(object? state)
    {
        if (System.Threading.Interlocked.Exchange(ref _botMovementProcessing, 1) == 1)
        {
            // 틱이 50ms를 넘기면 다음 틱이 통째로 스킵되어 봇 위치 브로드캐스트 간격이
            // 50ms와 100ms를 오간다. 클라 보간이 그대로 튀므로 빈도를 계측한다.
            System.Threading.Interlocked.Increment(ref _botMovementTickSkips);
            int consecutiveSkips = System.Threading.Interlocked.Increment(ref _botMovementConsecutiveSkips);
            UpdateMaximum(ref _botMovementMaxConsecutiveSkips, consecutiveSkips);
            return;
        }

        var botMovementTickStartedAt = DateTime.UtcNow;
        double snapshotElapsedMilliseconds = 0d;
        double planningElapsedMilliseconds = 0d;
        double walkingElapsedMilliseconds = 0d;
        double broadcastElapsedMilliseconds = 0d;
        try
        {
            var activeSessions = _clientSessions.Values
                .Where(s => s.PlayerId.HasValue)
                .ToList();
            var matchingIds = GetActiveMatchingIds();
            BroadcastMatchStartCountdowns(matchingIds, activeSessions);

            foreach (long matchingId in matchingIds)
            {
                if (!MatchStartGate.IsGameplayActive(matchingId)) continue;
                if (!GameClientSession.IsRoundActionPhase(matchingId)) continue;
                if (!_botPlayerManager.HasBots(matchingId)) continue;
                // 프로토 0: 봇 타겟 추적/떠보기를 위해 같은 매칭 인간 플레이어의 현재 영역을 넘긴다.
                long snapshotStartedAt = Stopwatch.GetTimestamp();
                var humanAreas = activeSessions
                    .Where(s => s.CurrentMapSubId == matchingId && s.PlayerId.HasValue)
                    .ToDictionary(s => s.PlayerId!.Value, s => s.CurrentArea);
                var combatTargets = activeSessions
                    .Where(s => s.CurrentMapSubId == matchingId && s.PlayerId.HasValue && !s.IsEliminated &&
                                s.LastValidatedPosition != null)
                    .Select(s => new BotCombatTargetSnapshot(
                        s.PlayerId!.Value,
                        s.CurrentArea,
                        s.LastValidatedPosition!,
                        _inGameInventoryManager.GetEquippedBattleItem(matchingId, s.PlayerId.Value)?.ItemId ?? 0,
                        s.CurrentCorruption))
                    .Concat(_botPlayerManager.GetBots(matchingId)
                        .Where(bot => !bot.IsEliminated)
                        .Select(bot => new BotCombatTargetSnapshot(
                            bot.PlayerId,
                            bot.CurrentArea,
                            bot.Position,
                            _inGameInventoryManager.GetEquippedBattleItem(matchingId, bot.PlayerId)?.ItemId ?? 0,
                            bot.Corruption)))
                    .ToList();
                var pveTargets = _emotionAfterimageMonsterManager.GetAliveTargets(matchingId);
                snapshotElapsedMilliseconds += Stopwatch.GetElapsedTime(snapshotStartedAt).TotalMilliseconds;

                var movementResult = _botPlayerManager.ProcessBotMovementTick(
                    matchingId,
                    _areaClosureManager,
                    _areaItemStockManager,
                    humanAreas,
                    _checklistManager,
                    _inGameInventoryManager,
                    _groundItemManager,
                    combatTargets,
                    pveTargets,
                    _survivorPhaseManager,
                    Config.SWARM_P0_ENABLED
                        ? ResolveSwarmBotDirective
                        : _spotArenaManager.GetBotDirective,
                    _summonStoneManager);
                planningElapsedMilliseconds += movementResult.PlanningElapsedMilliseconds;
                walkingElapsedMilliseconds += movementResult.WalkingElapsedMilliseconds;

                long broadcastStartedAt = Stopwatch.GetTimestamp();
                foreach (var ev in movementResult.Movements)
                {
                    // 오브 궤도 (#232): 봇도 이동한 거리만큼 돈다 — 사람 세션의 검증 이동 적산과 같은 규칙.
                    _botPlayerManager.GetBot(matchingId, ev.BotPlayerId)?.AdvanceOrbOrbit(ev.Position);
                    BroadcastBotMovement(matchingId, ev, activeSessions);
                }
                if (movementResult.ExploreEnds.Count > 0)
                    BroadcastBotExploreEnds(matchingId, movementResult.ExploreEnds, activeSessions);
                if (movementResult.GroundItemPickups.Count > 0)
                    BroadcastBotGroundItemPickups(matchingId, movementResult.GroundItemPickups, activeSessions);
                StartTargetBotInterrogations(matchingId, activeSessions);
                broadcastElapsedMilliseconds += Stopwatch.GetElapsedTime(broadcastStartedAt).TotalMilliseconds;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "봇 walking 틱 처리 중 오류");
        }
        finally
        {
            try
            {
                double botTickElapsedMs = (DateTime.UtcNow - botMovementTickStartedAt).TotalMilliseconds;
                System.Threading.Interlocked.Exchange(ref _botMovementConsecutiveSkips, 0);
                _botMovementTickSamples.Add(botTickElapsedMs);
                _botMovementSnapshotSamples.Add(snapshotElapsedMilliseconds);
                _botMovementPlanningSamples.Add(planningElapsedMilliseconds);
                _botMovementWalkingSamples.Add(walkingElapsedMilliseconds);
                _botMovementBroadcastSamples.Add(broadcastElapsedMilliseconds);
                _botMovementTickCount++;
                _botMovementTickTotalMs += botTickElapsedMs;
                if (botTickElapsedMs > _botMovementTickMaxMs) _botMovementTickMaxMs = botTickElapsedMs;
                if (_botMovementTickCount >= 200)
                {
                    var sortedSamples = _botMovementTickSamples.OrderBy(value => value).ToArray();
                    double p50Milliseconds = CalculatePercentile(sortedSamples, 0.50);
                    double p95Milliseconds = CalculatePercentile(sortedSamples, 0.95);
                    double p99Milliseconds = CalculatePercentile(sortedSamples, 0.99);
                    double snapshotP95Milliseconds = CalculatePercentile(
                        _botMovementSnapshotSamples.OrderBy(value => value).ToArray(), 0.95);
                    double planningP95Milliseconds = CalculatePercentile(
                        _botMovementPlanningSamples.OrderBy(value => value).ToArray(), 0.95);
                    double walkingP95Milliseconds = CalculatePercentile(
                        _botMovementWalkingSamples.OrderBy(value => value).ToArray(), 0.95);
                    double broadcastP95Milliseconds = CalculatePercentile(
                        _botMovementBroadcastSamples.OrderBy(value => value).ToArray(), 0.95);
                    int skippedTicks = System.Threading.Interlocked.Exchange(ref _botMovementTickSkips, 0);
                    int maxConsecutiveSkippedTicks =
                        System.Threading.Interlocked.Exchange(ref _botMovementMaxConsecutiveSkips, 0);
                    logger.LogInformation(
                        "Bot movement tick: avg={Avg:F1}ms max={Max:F1}ms skips={Skips} over {Count} ticks; " +
                        "p95 snapshot={SnapshotP95:F1}ms planning={PlanningP95:F1}ms walking={WalkingP95:F1}ms " +
                        "broadcast={BroadcastP95:F1}ms",
                        _botMovementTickTotalMs / _botMovementTickCount,
                        _botMovementTickMaxMs,
                        skippedTicks,
                        _botMovementTickCount,
                        snapshotP95Milliseconds,
                        planningP95Milliseconds,
                        walkingP95Milliseconds,
                        broadcastP95Milliseconds);
                    foreach (long matchingId in GetActiveMatchingIds()
                                 .Where(_botPlayerManager.HasBots))
                    {
                        _gameEventLogManager.LogBotMovementTickPerformance(
                            matchingId,
                            p50Milliseconds,
                            p95Milliseconds,
                            p99Milliseconds,
                            snapshotP95Milliseconds,
                            planningP95Milliseconds,
                            walkingP95Milliseconds,
                            broadcastP95Milliseconds,
                            _botMovementTickCount,
                            skippedTicks,
                            maxConsecutiveSkippedTicks);
                    }
                    _botMovementTickCount = 0;
                    _botMovementTickTotalMs = 0;
                    _botMovementTickMaxMs = 0;
                    _botMovementTickSamples.Clear();
                    _botMovementSnapshotSamples.Clear();
                    _botMovementPlanningSamples.Clear();
                    _botMovementWalkingSamples.Clear();
                    _botMovementBroadcastSamples.Clear();
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to record bot movement tick metrics");
            }
            finally
            {
                System.Threading.Volatile.Write(ref _botMovementProcessing, 0);
            }
        }
    }

    private static double CalculatePercentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
            return 0d;

        double position = (sortedValues.Count - 1) * Math.Clamp(percentile, 0d, 1d);
        int lowerIndex = (int)Math.Floor(position);
        int upperIndex = (int)Math.Ceiling(position);
        if (lowerIndex == upperIndex)
            return sortedValues[lowerIndex];
        double fraction = position - lowerIndex;
        return sortedValues[lowerIndex] + (sortedValues[upperIndex] - sortedValues[lowerIndex]) * fraction;
    }

    private static void UpdateMaximum(ref int location, int candidate)
    {
        int current = System.Threading.Volatile.Read(ref location);
        while (candidate > current)
        {
            int observed = System.Threading.Interlocked.CompareExchange(ref location, candidate, current);
            if (observed == current)
                return;
            current = observed;
        }
    }

    private void BroadcastMatchStartCountdowns(
        IEnumerable<long> matchingIds,
        IReadOnlyCollection<GameClientSession> activeSessions)
    {
        foreach (long matchingId in matchingIds)
        {
            var snapshot = MatchStartGate.GetSnapshot(matchingId);
            if (!snapshot.IsKnown)
                continue;

            if (_lastMatchStartCountdownBroadcast.TryGetValue(matchingId, out int previous) &&
                previous == snapshot.RemainingSeconds)
            {
                continue;
            }

            _lastMatchStartCountdownBroadcast[matchingId] = snapshot.RemainingSeconds;
            var matchingSessions = activeSessions
                .Where(session => session.CurrentMapSubId == matchingId)
                .ToList();
            if (matchingSessions.Count == 0)
                continue;

            using var packet = Packet.Create((int)Protocol.G_TO_C_MATCH_START_COUNTDOWN);
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

            foreach (var session in matchingSessions)
                session.Send(packet);
        }
    }

    private static readonly TimeSpan TargetBotInterrogationDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan BotToBotStatementHold = TimeSpan.FromSeconds(2);

    private void StartTargetBotInterrogations(long matchingId, List<GameClientSession> activeSessions)
    {
        var now = DateTime.UtcNow;
        var matchingSessions = activeSessions
            .Where(s => s.CurrentMapSubId == matchingId)
            .ToList();

        var bots = _botPlayerManager.GetBots(matchingId)
            .Where(b => !b.IsEliminated)
            .ToList();

        foreach (var bot in bots)
        {
            long guardedPlayerId = bot.PresenceBookmarkPlayerId;
            if (guardedPlayerId == 0 || guardedPlayerId == bot.PlayerId)
            {
                PruneGuardedBotEncounter(bot, guardedPlayerId, false);
                continue;
            }

            if (bot.IsInInteraction || bot.CurrentArea == AreaType.None)
                continue;

            var targetSession = matchingSessions.FirstOrDefault(s => s.PlayerId == guardedPlayerId);
            var targetBot = targetSession == null
                ? bots.FirstOrDefault(b => b.PlayerId == guardedPlayerId)
                : null;

            bool targetSameArea =
                targetSession != null &&
                !targetSession.IsEliminated &&
                targetSession.CurrentArea == bot.CurrentArea;
            if (!targetSameArea)
                targetSameArea =
                    targetBot is { IsEliminated: false } &&
                    !targetBot.IsInInteraction &&
                    targetBot.CurrentArea == bot.CurrentArea;

            PruneGuardedBotEncounter(bot, guardedPlayerId, targetSameArea);
            if (!targetSameArea) continue;
            if (bot.TargetInterrogationRequestedInEncounterPlayerIds.Contains(guardedPlayerId)) continue;

            if (!bot.TargetEncounterStartedAtByPlayerId.TryGetValue(guardedPlayerId, out var encounterStartedAt))
            {
                bot.TargetEncounterStartedAtByPlayerId[guardedPlayerId] = now;
                continue;
            }

            if (now - encounterStartedAt < TargetBotInterrogationDelay) continue;

            bool started = targetSession != null
                ? targetSession.TryStartTargetBotInterrogation(bot)
                : targetBot != null && TryCreateBotToBotStatement(matchingId, bot, targetBot);
            if (!started) continue;

            bot.TargetEncounterStartedAtByPlayerId[guardedPlayerId] = now;
            bot.TargetInterrogationRequestedInEncounterPlayerIds.Add(guardedPlayerId);
        }
    }

    private static void PruneGuardedBotEncounter(BotPlayerState bot, long guardedPlayerId, bool targetSameArea)
    {
        foreach (long playerId in bot.TargetEncounterStartedAtByPlayerId.Keys.ToList())
            if (playerId != guardedPlayerId || !targetSameArea)
                bot.TargetEncounterStartedAtByPlayerId.Remove(playerId);

        foreach (long playerId in bot.TargetInterrogationRequestedInEncounterPlayerIds.ToList())
            if (playerId != guardedPlayerId || !targetSameArea)
                bot.TargetInterrogationRequestedInEncounterPlayerIds.Remove(playerId);
    }

    private bool TryCreateBotToBotStatement(long matchingId, BotPlayerState askerBot, BotPlayerState answererBot)
    {
        if (askerBot.PlayerId == answererBot.PlayerId) return false;
        if (askerBot.CurrentArea == AreaType.None || askerBot.CurrentArea != answererBot.CurrentArea) return false;

        var interactionChoiceService = new InteractionChoiceService(
            _interactionLogManager,
            _manittoChainManager,
            _gameEventLogManager);

        var questionSet = interactionChoiceService.GenerateQuestionSet(
            matchingId,
            askerBot.PlayerId,
            answererBot.PlayerId,
            askerBot.CurrentArea,
            null);
        var question = questionSet.Questions.FirstOrDefault(q => q.QuestionType == InteractionQuestionType.ASK_NEARBY_REASON)
                       ?? questionSet.Questions.FirstOrDefault();
        if (question == null) return false;

        var answerTask = ResolveBotAnswerTask(matchingId, answererBot);
        var answerSet = interactionChoiceService.GenerateAnswerSet(
            matchingId,
            answererBot.PlayerId,
            askerBot.PlayerId,
            question.QuestionType,
            answererBot.CurrentArea,
            questionSet.Contexts.FirstOrDefault(c => c.QuestionType == question.QuestionType),
            ResolveTaskArea(answerTask),
            answerTask?.TaskId ?? 0);
        if (answerSet.Answers.Count == 0) return false;

        int answerIndex = _botPlayerManager.PickAnswerIndex(answerSet.Answers.Count);
        answerIndex = Math.Clamp(answerIndex, 0, answerSet.Answers.Count - 1);
        var answerContext = answerSet.Contexts.ElementAtOrDefault(answerIndex);
        if (answerContext == null) return false;

        int roundId = 0 /* 라운드 시스템 퇴역(#246) */;
        var statement = _gameEventLogManager.LogStatement(
            matchingId,
            roundId,
            answererBot.PlayerId,
            askerBot.PlayerId,
            answerContext.Area.ToString(),
            answerContext.QuestionId,
            answerContext.QuestionText,
            answerContext.AnswerType,
            answerContext.AnswerText,
            answerContext.LinkedLogIds,
            isBot: true);

        askerBot.HoldForInteraction(BotToBotStatementHold);
        answererBot.HoldForInteraction(BotToBotStatementHold);

        logger.LogInformation(
            "[BOT_STATEMENT] MatchingId={MatchingId}, Asker={Asker}, Answerer={Answerer}, AnswerType={AnswerType}, Description={Description}",
            matchingId, askerBot.PlayerId, answererBot.PlayerId, answerContext.AnswerType, statement.Description);
        return true;
    }

    private ChecklistTaskData? ResolveBotAnswerTask(long matchingId, BotPlayerState bot)
    {
        if (bot.PendingChecklistTaskId > 0)
        {
            var pendingTask = GameChecklistData.GetTask(bot.PendingChecklistTaskId);
            if (pendingTask != null)
                return pendingTask;
        }

        return _checklistManager.GetNextActiveGeneralInteractTask(matchingId, bot.PlayerId);
    }

    private static AreaType? ResolveTaskArea(ChecklistTaskData? task)
    {
        if (task?.AreaType > 0 && Enum.IsDefined(typeof(AreaType), task.AreaType))
            return (AreaType)task.AreaType;

        return null;
    }

    private void CheckHeartbeatTimeouts(object? state)
    {
        try
        {
            var timedOutSessions = _clientSessions.Values
                .Where(s => s.PlayerId.HasValue && s.IsHeartbeatTimedOut())
                .ToList();

            foreach (var session in timedOutSessions)
            {
                logger.LogWarning("Heartbeat timeout for PlayerId={PlayerId}, forcing disconnect", session.PlayerId);
                session.ForceDisconnect();
            }

            if (timedOutSessions.Count > 0)
                logger.LogInformation("Disconnected {Count} sessions due to heartbeat timeout",
                    timedOutSessions.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error checking heartbeat timeouts");
        }
    }


    private void PublishMatchingLifecycle(string subject, long playerId)
    {
        try
        {
            _matchingLifecycleNatsClient?.Publish(subject, BitConverter.GetBytes(playerId));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Matching lifecycle publish failed: Subject={Subject}, PlayerId={PlayerId}",
                subject, playerId);
        }
    }

    private void OnClientSessionCreated(UserToken token)
    {
        try
        {
            var redLockFactory = cacheHelper.GetRedLockFactory();
            _ = new GameClientSession(
                token,
                redLockFactory,
                logger,
                cacheHelper,
                OnClientSessionLeave,
                RegisterClientSession,
                GetSessionsByInstance,
                _interactableStateManager,
                _inGameInventoryManager,
                _areaRuleManager,
                _itemPoolManager,
                _areaItemStockManager,
                _groundItemManager,
                _emotionAfterimageMonsterManager,
                _summonStoneManager,
                _doorStateManager,
                _manittoChainManager,
                _checklistManager,
                _areaClosureManager,
                new InteractionChoiceService(_interactionLogManager, _manittoChainManager, _gameEventLogManager),
                _botPlayerManager,
                _gameEventLogManager,
                _matchSummaryFileStore,
                _encounterRevealManager,
                playerId => PublishMatchingLifecycle(MatchingLifecycleSubjects.PlayerLeft, playerId),
                playerId => PublishMatchingLifecycle(MatchingLifecycleSubjects.PlayerCompleted, playerId));

            logger.LogInformation("Game client session created");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create game client session");
        }
    }

    private void OnClientSessionLeave(GameClientSession session)
    {
        if (session.PlayerId.HasValue)
        {
            _clientSessions.TryRemove(session.PlayerId.Value, out _);
            logger.LogInformation("Game client session removed: PlayerId={SessionPlayerId}", session.PlayerId.Value);

            if (session.CurrentMapSubId > 0 && session.CurrentArea != AreaType.None)
            {
                using var leavePacket = PacketMaker.G_TO_C_AREA_PLAYER_LEAVE(session.PlayerId.Value);
                var sameAreaSessions = _clientSessions.Values
                    .Where(other =>
                        !ReferenceEquals(other, session) &&
                        other.PlayerId.HasValue &&
                        other.CurrentMapId == session.CurrentMapId &&
                        other.CurrentMapSubId == session.CurrentMapSubId &&
                        other.CurrentArea == session.CurrentArea)
                    .ToList();
                foreach (var other in sameAreaSessions) other.Send(leavePacket);

                logger.LogInformation(
                    "Broadcasted disconnected player leave: PlayerId={PlayerId}, MatchingId={MatchingId}, Area={Area}, Receivers={ReceiverCount}",
                    session.PlayerId.Value,
                    session.CurrentMapSubId,
                    session.CurrentArea,
                    sameAreaSessions.Count);
            }

            // 인스턴스 컨트롤러에 연결 해제 알림 (모든 유저 연결 해제 시 게임 종료 처리)
            if (session.CurrentMapSubId > 0)
                foreach (var controller in _instanceControllerList)
                    controller.OnPlayerDisconnected(session.CurrentMapId, session.CurrentMapSubId,
                        session.PlayerId.Value);

            if (session.CurrentMapSubId > 0)
                CleanupMatchingIfNoHumanSessionsRemain(session.CurrentMapSubId);
        }
    }

    /// <summary>
    ///     사람 세션 없이 진행되는 매치(관리자 봇 전용 인스턴스)를 정산한다.
    ///     승리 판정이 사람 세션에 의존해 최후 1인이 남아도 끝나지 않고, 오염도가 한계에
    ///     닿은 봇이 계속 살아 있는 상태로 매치가 무한히 이어지던 것을 막는다.
    /// </summary>
    public void EndBotOnlyMatchIfSettled(long matchingId, long winnerPlayerId)
    {
        if (_clientSessions.Values.Any(session =>
                session.PlayerId.HasValue && session.CurrentMapSubId == matchingId))
            return;

        CleanupMatchingIfNoHumanSessionsRemain(matchingId, "last_survivor_bot_only", winnerPlayerId);
    }

    private void CleanupMatchingIfNoHumanSessionsRemain(long matchingId) =>
        CleanupMatchingIfNoHumanSessionsRemain(matchingId, "last_human_left", 0);

    private void CleanupMatchingIfNoHumanSessionsRemain(long matchingId, string endReason, long winnerPlayerId)
    {
        if (_clientSessions.Values.Any(other =>
                other.PlayerId.HasValue && other.CurrentMapSubId == matchingId))
            return;

        if (_gameEventLogManager.TryBeginFinalization(matchingId))
        {
            DateTime endedAtUtc = DateTime.UtcNow;
            DateTime startedAtUtc = _areaClosureManager.GetMatchingState(matchingId)?.GameStartTime ?? endedAtUtc;
            var finalPlayerStats = _manittoChainManager.BuildGameResult(matchingId)
                .Select(row =>
                {
                    var stats = _gameEventLogManager.GetSurvivorResultStats(matchingId, row.playerId);
                    DateTime survivalEndUtc = row.eliminatedAt ?? endedAtUtc;
                    return new SurvivorFinalPlayerStats(
                        row.playerId,
                        row.eliminationRank,
                        Math.Max(0, (int)Math.Floor((survivalEndUtc - startedAtUtc).TotalSeconds)),
                        stats.KillCount + stats.MonsterKillCount,
                        stats.TotalDamageDealt + stats.MonsterDamageDealt,
                        stats.TotalRecovery,
                        // 승점 (#229): 사람이 나간 매치도 오브 수를 남긴다 — 봇 매치가 유일한
                        // 자동 검증 창구라 여기서 빠지면 결과 집계를 로그로 확인할 수 없다.
                        GetSwarmOrbScore(matchingId, row.playerId).OrbCount);
                })
                .ToList();
            _gameEventLogManager.LogMatchAbandoned(matchingId, endReason, finalPlayerStats);
            PersistMatchSummary(matchingId, endReason, winnerPlayerId);
        }

        GameClientSession.CleanupAbandonedMatchingRuntime(matchingId);
        _areaClosureManager.CleanupMatching(matchingId);
        _survivorPhaseManager.CleanupMatching(matchingId);
        _botPlayerManager.CleanupMatching(matchingId);
        _presenceTracker.Remove(matchingId);
        _checklistManager.RemoveMatchingState(matchingId);
        _areaItemStockManager.RemoveMatchingState(matchingId);
        _groundItemManager.RemoveMatchingState(matchingId);
        _emotionAfterimageMonsterManager.RemoveMatchingState(matchingId);
        _summonStoneManager.RemoveMatchingState(matchingId);
        _inGameInventoryManager.RemoveMatchingState(matchingId);
        _interactableStateManager.RemoveMatchingState(matchingId);
        _areaRuleManager.RemoveMatchingState(matchingId);
        _itemPoolManager.RemoveMatchingState(matchingId);
        _doorStateManager.ClearMatching(matchingId);
        _manittoChainManager.CleanupMatching(matchingId);
        _interactionLogManager.CleanupMatching(matchingId);
        _gameEventLogManager.Clear(matchingId);
        _encounterRevealManager.CleanupMatching(matchingId);
        _spotArenaManager.RemoveMatching(matchingId);
        _proximityAutoCombatResolver.RemoveMatching(matchingId);
        CleanupSurvivorSettlementState(matchingId);
        _ = CleanupAbandonedMatchingRedisAsync(matchingId);

        logger.LogInformation(
            "Removed matching without human sessions: MatchingId={MatchingId}, EndReason={EndReason}",
            matchingId, endReason);
    }

    private void PersistMatchSummary(long matchingId, string endReason, long winnerId)
    {
        try
        {
            var events = _gameEventLogManager.GetForPersistence(matchingId);
            var summary = _matchSummaryFileStore.Save(matchingId, endReason, winnerId, events);
            logger.LogInformation(
                "Match summary persisted: MatchingId={MatchingId}, EndReason={EndReason}, Events={EventCount}, Directory={Directory}",
                matchingId, summary.EndReason, summary.RawEventCount, _matchSummaryFileStore.DirectoryPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist match summary: MatchingId={MatchingId}", matchingId);
        }
    }

    private async Task CleanupAbandonedMatchingRedisAsync(long matchingId)
    {
        try
        {
            await cacheHelper.KeyDeleteAsync(MatchingHandoffRedisKeys.Key(matchingId));
            await cacheHelper.HashDeleteAsync("matching_bots", matchingId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to remove abandoned matching from Redis: MatchingId={MatchingId}",
                matchingId);
        }
    }

    private void RegisterClientSession(long playerId, GameClientSession session)
    {
        _clientSessions.TryAdd(playerId, session);
        logger.LogInformation("Game client session registered: PlayerId={PlayerId}", playerId);
    }

    private List<GameClientSession> GetSessionsByInstance(MapId mapId, long mapSubId)
    {
        return _clientSessions.Values
            .Where(s => s.CurrentMapId == mapId && s.CurrentMapSubId == mapSubId)
            .ToList();
    }

    // MMO 로그아웃 프로토콜 제거됨 - 세션 기반 게임에서는 불필요
    // private void SubscribeToLogoutEvents() { ... }
    // private async Task ProcessMessage(byte[] message) { ... }
    // private async Task HandleLogout(long playerId, byte[] body) { ... }

    // ===== 운영 어드민 API =====

    /// <summary>
    ///     글로벌 매칭 config 서비스 (AdminEndpoints에서 직접 접근)
    /// </summary>
    public MatchingConfigService MatchingConfigService => _matchingConfigService;

    /// <summary>
    ///     운영툴 진행 로그 매니저 (AdminEndpoints에서 events 조회용)
    /// </summary>
    public GameEventLogManager GameEventLogManager => _gameEventLogManager;
    public MatchSummaryFileStore MatchSummaryFileStore => _matchSummaryFileStore;

    /// <summary>
    ///     활성 인스턴스 ID 목록 반환 (MatchingId 기준 dedup)
    /// </summary>
    public IReadOnlyList<long> GetActiveInstanceIds()
    {
        return GetActiveMatchingIds();
    }

    private List<long> GetActiveMatchingIds()
    {
        var ids = _clientSessions.Values
            .Where(s => s.PlayerId.HasValue && s.CurrentMapSubId > 0)
            .Select(s => s.CurrentMapSubId)
            .ToHashSet();

        foreach (long matchingId in _botPlayerManager.GetActiveMatchingIds())
        {
            if (matchingId > 0)
                ids.Add(matchingId);
        }

        return ids.OrderBy(id => id).ToList();
    }

    public InstanceSnapshot? CreateBotOnlyInstance(int botCount = 10)
    {
        // 상한 = 매치 정원 (#223 10인 전환) — 스폰 포드 수와 일치.
        botCount = Math.Clamp(botCount, 2, Config.SWARM_PLAYERS_PER_MATCH);
        long matchingId = System.Threading.Interlocked.Increment(ref _adminBotOnlyMatchingIdSeed);
        var jobs = BuildBotOnlyJobPool(botCount);
        var playerIds = Enumerable.Range(0, botCount)
            .Select(_ => System.Threading.Interlocked.Decrement(ref _adminBotOnlyPlayerIdSeed))
            .ToList();

        var botInfoList = new List<BotMatchingInfo>();
        for (int i = 0; i < botCount; i++)
        {
            int targetIndex = (i + 1) % botCount;
            botInfoList.Add(new BotMatchingInfo
            {
                PlayerId = playerIds[i],
                TargetPlayerId = playerIds[targetIndex],
                MyJobTitle = jobs[i],
                TargetJobTitle = jobs[targetIndex]
            });
        }

        var spawnAssignments = SurvivorRoyaleSpawnData.CreatePhaseRoomAssignments(matchingId, playerIds);
        foreach (var botInfo in botInfoList)
            botInfo.SpawnCell = Cell.Clone(spawnAssignments[botInfo.PlayerId]);

        _botPlayerManager.RegisterBots(matchingId, MapId.School, botInfoList);
        int matchSeed = SurvivorRoyaleSpawnData.GetDeterministicSeed(matchingId);
        _gameEventLogManager.BeginMatch(matchingId, matchSeed);
        foreach (var bot in _botPlayerManager.GetBots(matchingId))
        {
            _gameEventLogManager.LogSpawnAssignment(
                matchingId,
                bot.PlayerId,
                matchSeed,
                SurvivorRoyaleSpawnData.GetAnchorIndex(bot.Cell),
                bot.Cell.X,
                bot.Cell.Y,
                bot.CurrentArea.ToString(),
                isBot: true);
        }


        foreach (var bot in botInfoList)
        {
            _manittoChainManager.RegisterLink(matchingId, new ChainLink
            {
                PlayerId = bot.PlayerId,
                TargetPlayerId = bot.TargetPlayerId,
                MyJobTitle = bot.MyJobTitle,
                TargetJobTitle = bot.TargetJobTitle
            });


            if (!Config.SPOT_ARENA_P0_ENABLED)
                _summonStoneManager.EnsureStartingStones(matchingId, bot.PlayerId);
        }

        if (!Config.SPOT_ARENA_P0_ENABLED)
        {
            _areaClosureManager.InitializeMatching(
                matchingId,
                jobs,
                SurvivorRoyaleSpawnData.GetPhaseRoomCandidates());
        }
        _areaItemStockManager.InitializeMatching(matchingId);
        _groundItemManager.InitializeMatching(matchingId);
        if (!Config.SPOT_ARENA_P0_ENABLED)
        {
            _emotionAfterimageMonsterManager.InitializeMatching(matchingId);
            _gameEventLogManager.LogRewardAreaSnapshot(
                matchingId,
                _emotionAfterimageMonsterManager.GetRewardAreaSnapshot(matchingId),
                "initial");
        }
        _doorStateManager.InitializeMatching(
            matchingId,
            Config.SPOT_ARENA_P0_ENABLED
                ? Array.Empty<AreaType>()
                : SurvivorRoyaleSpawnData.GetPhaseRoomCandidates());
        _checklistManager.StartRound(matchingId, 1, playerIds,
            playerId => ResolveBotOnlyChecklistChainContext(matchingId, playerId));

        _gameEventLogManager.LogSystem(matchingId,
            $"Bot-only instance created: botCount={botCount}, ids=[{string.Join(",", playerIds)}]");
        logger.LogInformation(
            "Bot-only instance created: MatchingId={MatchingId}, BotCount={BotCount}, Ids=[{Ids}]",
            matchingId, botCount, string.Join(",", playerIds));

        return GetInstanceSnapshot(matchingId);
    }

    private static List<JobTitle> BuildBotOnlyJobPool(int botCount)
    {
        var jobs = new List<JobTitle>
        {
            JobTitle.BROADCAST_MEMBER,
            JobTitle.DISCIPLINE_MEMBER,
            JobTitle.LIBRARY_COMMITTEE,
            JobTitle.SPORTS_CAPTAIN,
            JobTitle.SCIENCE_MEMBER,
            JobTitle.CLEANING_MEMBER,
            JobTitle.STUDENT_PRESIDENT,
            JobTitle.HEALTH_MEMBER
        };

        // 정원(10)이 잡 풀(8)보다 클 수 있다 (#223 10인 전환) — 순환 배정.
        return Enumerable.Range(0, botCount).Select(index => jobs[index % jobs.Count]).ToList();
    }

    private ChecklistChainContext ResolveBotOnlyChecklistChainContext(long matchingId, long playerId)
    {
        var myLink = _manittoChainManager.GetLink(matchingId, playerId);
        bool targetAlive = myLink != null && IsBotOnlyChainPlayerActive(matchingId, myLink.TargetPlayerId);
        var manittoLink = _manittoChainManager.FindManittoOf(matchingId, playerId);
        bool manittoAlive = manittoLink != null
                            && manittoLink.Status != ManittoStatus.ELIMINATED
                            && manittoLink.Status != ManittoStatus.SPECTATING;
        return new ChecklistChainContext(targetAlive, manittoAlive);
    }

    private bool IsBotOnlyChainPlayerActive(long matchingId, long playerId)
    {
        var link = _manittoChainManager.GetLink(matchingId, playerId);
        return link != null
               && link.Status != ManittoStatus.ELIMINATED
               && link.Status != ManittoStatus.SPECTATING;
    }

    /// <summary>
    ///     인스턴스 요약 (목록 뷰용)
    /// </summary>
    public InstanceSummary? GetInstanceSummary(long matchingId)
    {
        var sessions = _clientSessions.Values
            .Where(s => s.PlayerId.HasValue && s.CurrentMapSubId == matchingId)
            .ToList();
        var bots = _botPlayerManager.GetBots(matchingId);

        if (sessions.Count == 0 && bots.Count == 0) return null;

        var closureState = _areaClosureManager.GetMatchingState(matchingId);
        double elapsed = closureState != null
            ? (DateTime.UtcNow - closureState.GameStartTime).TotalSeconds
            : 0;

        var closedAreas = closureState?.ClosedAreas
            .Select(a => a.ToString())
            .ToList() ?? [];

        int aliveCount = sessions.Count(s => !s.IsEliminated) + bots.Count(b => !b.IsEliminated);
        string mapId = sessions.FirstOrDefault()?.CurrentMapId.ToString()
                       ?? _botPlayerManager.GetMatchingMapId(matchingId).ToString();

        return new InstanceSummary
        {
            MatchingId = matchingId,
            MapId = mapId,
            PlayerCount = sessions.Count + bots.Count,
            AliveCount = aliveCount,
            ElapsedSeconds = Math.Round(elapsed, 1),
            RoundNumber = 0, // 라운드 시스템 퇴역(#246)
            TotalRounds = 0,
            RoundPhase = "",
            RoundRemainingSeconds = 0,
            RoundPhaseDurationSeconds = 0,
            RoundSessionEnded = false,
            ClosedAreas = closedAreas
        };
    }

    /// <summary>
    ///     인스턴스 풀 스냅샷 (폐쇄 스케줄 포함)
    /// </summary>
    public InstanceSnapshot? GetFullInstanceSnapshot(long matchingId)
    {
        var base_ = GetInstanceSnapshot(matchingId);
        if (base_ == null) return null;

        // 폐쇄 스케줄 조립 (startDelaySec, intervalSec 포함)
        var (sequence, closedIds, nextArea, nextAtUnix, secondsLeft, warningActive, startDelaySec, intervalSec) =
            _areaClosureManager.GetClosureSnapshot(matchingId);

        // 시퀀스 한글명 목록
        var areaNames = sequence.Select(a => GameAreaNameData.Get((AreaType)a)).ToList();

        base_.Closure = new ClosureSnapshot
        {
            ClosureSequence = sequence,
            AreaNames = areaNames,
            ClosedAreaIds = closedIds,
            NextClosureAreaType = nextArea,
            NextClosureAtUnix = nextAtUnix,
            NextClosureSecondsLeft = secondsLeft,
            WarningActive = warningActive,
            StartDelaySec = startDelaySec,
            IntervalSec = intervalSec
        };

        return base_;
    }

    /// <summary>
    ///     인스턴스 상세 스냅샷 (플레이어별 코어 상태 포함)
    /// </summary>
    public InstanceSnapshot? GetInstanceSnapshot(long matchingId)
    {
        var sessions = _clientSessions.Values
            .Where(s => s.PlayerId.HasValue && s.CurrentMapSubId == matchingId)
            .ToList();

        var bots = _botPlayerManager.GetBots(matchingId);

        if (sessions.Count == 0 && bots.Count == 0) return null;

        var closureState = _areaClosureManager.GetMatchingState(matchingId);
        double elapsed = closureState != null
            ? (DateTime.UtcNow - closureState.GameStartTime).TotalSeconds
            : 0;

        var closedAreas = closureState?.ClosedAreas
            .Select(a => a.ToString())
            .ToList() ?? [];

        // 모든 PlayerId(인간+봇)에 대한 ChainLink 조회 → ManittoOfMe 역방향 매핑
        var allPlayerIds = sessions.Select(s => s.PlayerId!.Value).Concat(bots.Select(b => b.PlayerId)).ToList();
        var allLinks = allPlayerIds
            .Select(id => _manittoChainManager.GetLink(matchingId, id))
            .Where(l => l != null)
            .ToList();
        long? FindManittoOf(long playerId) =>
            allLinks.FirstOrDefault(l => l!.TargetPlayerId == playerId)?.PlayerId;

        var playerSnapshots = new List<PlayerSnapshot>();

        // 1) 인간 플레이어
        foreach (var s in sessions)
        {
            var chainLink = _manittoChainManager.GetLink(matchingId, s.PlayerId!.Value);
            playerSnapshots.Add(new PlayerSnapshot
            {
                PlayerId = s.PlayerId!.Value,
                Area = s.CurrentArea.ToString(),
                Stamina = s.AdminStamina,
                Corruption = s.AdminCorruption,
                ManittoStatus = s.ManittoStatus.ToString(),
                TargetPlayerId = s.TargetPlayerId,
                IsBot = s.IsBot,
                IsEliminated = s.IsEliminated,
                ManittoOfMe = FindManittoOf(s.PlayerId!.Value),
                ChainStatus = chainLink?.Status.ToString() ?? ""
            });
        }

        // 2) 봇 — TCP 세션이 없으므로 BotPlayerManager._botStates에서 조회
        foreach (var bot in bots)
        {
            var chainLink = _manittoChainManager.GetLink(matchingId, bot.PlayerId);
            playerSnapshots.Add(new PlayerSnapshot
            {
                PlayerId = bot.PlayerId,
                Area = bot.CurrentArea.ToString(),
                Stamina = bot.Stamina,
                Corruption = bot.Corruption,
                ManittoStatus = bot.ManittoStatus.ToString(),
                TargetPlayerId = bot.TargetPlayerId,
                IsBot = true,
                IsEliminated = bot.IsEliminated,
                ManittoOfMe = FindManittoOf(bot.PlayerId),
                ChainStatus = chainLink?.Status.ToString() ?? ""
            });
        }

        int aliveCount = sessions.Count(s => !s.IsEliminated) + bots.Count(b => !b.IsEliminated);
        string mapId = sessions.FirstOrDefault()?.CurrentMapId.ToString()
                       ?? _botPlayerManager.GetMatchingMapId(matchingId).ToString();

        return new InstanceSnapshot
        {
            MatchingId = matchingId,
            MapId = mapId,
            PlayerCount = sessions.Count + bots.Count,
            AliveCount = aliveCount,
            ElapsedSeconds = Math.Round(elapsed, 1),
            RoundNumber = 0, // 라운드 시스템 퇴역(#246)
            TotalRounds = 0,
            RoundPhase = "",
            RoundRemainingSeconds = 0,
            RoundPhaseDurationSeconds = 0,
            RoundSessionEnded = false,
            ClosedAreas = closedAreas,
            Players = playerSnapshots
        };
    }
}
