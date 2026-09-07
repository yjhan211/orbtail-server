using game_server.sessions;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;

namespace game_server.services;

/// <summary>
///     투사체 회피 조언 (#232 §9): 비켜설 월드 방향과, 그 위협의 앞머리가 이 봇을 지나갈 때까지의 시간.
///     봇은 이 시간 동안 회피를 커밋한다 — 띠 밖으로 나가면 서서 기다리고, 원래 경로로 되돌아가지 않는다.
/// </summary>
public readonly record struct SwarmBotDodgeAdvice(float DirectionX, float DirectionY, float HoldSeconds);

/// <summary>매치 하나의 봇과 이동 계획 상태를 관리한다. 호출은 해당 매치 잠금 안에서 실행한다.</summary>
public partial class BotPlayerManager
{
    /// <summary>
    ///     문 개방 여부 조회 (2026-08-16 유저 제보: 봇이 문 열리기 전에 들어온다).
    ///     사람은 GameClientSession.Movement가 문 상태로 막는데 봇만 그냥 지나다녔다.
    ///     봇도 잠긴 문은 3초 채널링(ProcessSwarmBotDoorUnlocks)으로 열 수 있으므로 막아도 갇히지 않는다.
    /// </summary>
    private Func<long, int, bool>? _doorOpenResolver;

    public void SetDoorOpenResolver(Func<long, int, bool> resolver) =>
        _doorOpenResolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    private bool IsDoorOpenForBot(long matchingId, int doorId) =>
        _doorOpenResolver?.Invoke(matchingId, doorId) ?? true;

    /// <summary>
    ///     투사체 회피 반사 (#232 §9): (matchingId, botId, position, area, now) → 지금 비켜설 월드 방향과
    ///     그 위협이 지나갈 때까지의 시간. null이면 위협 없음. 게임서버가 교차사격 모양 스냅샷으로 답한다.
    /// </summary>
    private Func<long, long, Vector3f, AreaType, DateTime, SwarmBotDodgeAdvice?>? _swarmDodgeResolver;

    public void SetSwarmDodgeResolver(Func<long, long, Vector3f, AreaType, DateTime, SwarmBotDodgeAdvice?> resolver) =>
        _swarmDodgeResolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    // 배회 폴백(잔상 사냥 실패 시)에서 최저 인원 방으로 흩어질 확률 — 봇이 한 방에 뭉치지 않게.
    private const double SwarmWanderScatterProbability = 0.3;

    private const double BotInitialDecisionDelayMinSeconds = 0.15;
    private const double BotInitialDecisionDelayMaxSeconds = 1.2;
    private const double BotRoomDwellMinSeconds = 1.25;
    private const double BotRoomDwellMaxSeconds = 2.25;

    private const int BotMissionTickIntervalSeconds = 1;
    private static int InitialHealth => Config.MAX_HEALTH;

    private readonly long _matchingId;
    private List<BotPlayerState> _bots = [];
    private bool _registered;

    // Cell BFS is expensive enough that replanning every bot in one 50 ms tick stalls broadcasts.
    // Rotate one planning slot per matching while every bot keeps walking its existing path.
    private int _movementPlanningCursor;

    private MapId _mapId = Config.SWARM_MATCH_MAP;

    private readonly ILogger _logger;

    private readonly Random _rng = Random.Shared;

    public BotPlayerManager(long matchingId, ILogger logger)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(matchingId);
        _matchingId = matchingId;
        _logger = logger;
    }

    /// <summary>
    ///     매치 구성이 정한 봇 ID와 스폰으로 봇 상태를 만든다. 스폰은 MatchSpawnPlanner가 사람과 함께 배정한 값이다.
    /// </summary>
    public void RegisterBots(long matchingId, MapId mapId, IReadOnlyList<long> botPlayerIds,
        IReadOnlyDictionary<long, Cell> spawnCells)
    {
        if (matchingId != _matchingId)
            throw new InvalidOperationException("Cannot register bots from another match.");
        _mapId = mapId;

        var bots = botPlayerIds.Select(botPlayerId =>
        {
            if (!spawnCells.TryGetValue(botPlayerId, out Cell? assignedSpawn))
                throw new InvalidOperationException($"Bot {botPlayerId} has no spawn assignment in match {matchingId}.");
            var startCell = Cell.Clone(assignedSpawn);
            var startArea = GameMapData.GetCurrentArea(mapId, startCell);
            if (startArea == AreaType.None)
            {
                // 구역 판정 실패 폴백 — 항상 경계 안인 중앙 광장으로 (#310, 구 Corridor 폴백 대체).
                startArea = AreaType.S2Corridor9;
            }

            var startPosition = CellToWorldPosition(mapId, startCell);
            var now = DateTime.UtcNow;

            return new BotPlayerState
            {
                PlayerId = botPlayerId,
                Name = $"Player{Math.Abs(botPlayerId)}",
                CurrentArea = startArea,
                Cell = startCell,
                Position = startPosition,
                Rotation = 0f,
                Health = InitialHealth,
                PlayerMatchStatus = PlayerMatchStatus.ACTIVE,
                GameStartTime = now,
                LoopWaitUntil = now.AddSeconds(RandomRange(
                    BotInitialDecisionDelayMinSeconds,
                    BotInitialDecisionDelayMaxSeconds))
            };
        }).ToList();

        _bots = bots;
        _registered = true;
        _movementPlanningCursor = 0;

        _logger.LogInformation(
            "Bots registered: Count={Count}, MatchingId={MatchingId}, MapId={MapId}, IDs=[{Ids}]",
            bots.Count, matchingId, mapId,
            string.Join(",", bots.Select(b => $"{b.PlayerId}@{b.CurrentArea}")));
    }

    /// <summary>
    /// Rooms where an unarmed survivor can farm without deliberately lingering in a corridor or a large open zone.
    /// Corridors may still be crossed by the pathfinder while travelling between these rooms.
    /// </summary>
    private static bool IsSecludedFarmingArea(MapId mapId, AreaType area)
    {
        if (area == AreaType.None || area.IsCorridor())
            return false;

        // #272 School2: 대형 개방 구역(운동장·테라스·1차 통로·합류 4곳)은 은둔 파밍처가 아니다.
        return area is not (AreaType.S2Ground or AreaType.S2Terrace or AreaType.S2Corridor9
                   or AreaType.S2Library1 or AreaType.S2Library2
                   or AreaType.S2Gym1 or AreaType.S2Gym2) &&
               GameMapData.GetAreas(mapId).Any(region => region.AreaType == area);
    }



    public MapId GetMatchingMapId(long matchingId)
    {
        return matchingId == _matchingId ? _mapId : Config.SWARM_MATCH_MAP;
    }

    private static readonly int[] BotDefaultWearItemIds =
    {
        101000003, // Hair
        102000003, // Face
        104000005, // Top
        105000005, // Bottom
        106000003  // Shoes
    };

    private static readonly int[] BotCustomizationItems =
    {
        103000001,
        103000004,
        103000005,
        103000006
    };

    internal static List<int> BuildBotWearItems(BotPlayerState bot)
    {
        var list = new List<int>(BotDefaultWearItemIds);
        int idx = (int)(Math.Abs(bot.PlayerId) % BotCustomizationItems.Length);
        list.Add(BotCustomizationItems[idx]);
        if (bot.EquippedBattleItemId > 0)
            list.Add(bot.EquippedBattleItemId);
        return list;
    }

    public PlayerInfo? SynthesizePlayerInfo(long matchingId, long botPlayerId)
    {
        var bot = GetBot(matchingId, botPlayerId);
        if (bot == null) return null;

        var mapId = GetMatchingMapId(matchingId);
        var state = bot.RestUntil != DateTime.MinValue && DateTime.UtcNow < bot.RestUntil
            ? PlayerState.SLEEP
            : bot.RngCollectProgressStartTime != DateTime.MinValue ||
              bot.SwarmExploreStartedAtUtc != DateTime.MinValue
                ? PlayerState.EXPLORE_1
                : PlayerState.IDLE;
        var info = new PlayerInfo
        {
            PlayerId = bot.PlayerId,
            Name = bot.Name,
            State = state,
            Hp = 5000,
            WearItemIdList = BuildBotWearItems(bot)
        };
        return info;
    }

    public GameObjectInfo? SynthesizeGameObjectInfo(long matchingId, long botPlayerId)
    {
        var bot = GetBot(matchingId, botPlayerId);
        if (bot == null) return null;
        var state = bot.RestUntil != DateTime.MinValue && DateTime.UtcNow < bot.RestUntil
            ? PlayerState.SLEEP
            : bot.RngCollectProgressStartTime != DateTime.MinValue || bot.SwarmExploreStartedAtUtc != DateTime.MinValue
                ? PlayerState.EXPLORE_1 : PlayerState.IDLE;
        return new GameObjectInfo(ObjectType.PLAYER, bot.PlayerId, GetMatchingMapId(matchingId), matchingId, bot.Cell)
        {
            Position = new Vector3f(bot.Position.X, bot.Position.Y, bot.Position.Z),
            Rotation = bot.Rotation,
            State = state
        };
    }

    internal static Vector3f CellToWorldPosition(MapId mapId, Cell cell) =>
        MapCoordinateConverter.CellToWorld(mapId, cell);

    public List<BotPlayerState> GetBots(long matchingId)
    {
        return matchingId == _matchingId ? _bots : [];
    }

    public BotPlayerState? GetBot(long matchingId, long playerId)
    {
        return GetBots(matchingId).FirstOrDefault(b => b.PlayerId == playerId);
    }

    public bool HasBots(long matchingId) => matchingId == _matchingId && _registered;

    internal void Release()
    {
        _bots.Clear();
        _registered = false;
        _movementPlanningCursor = 0;
    }

    public static bool IsBotPlayerId(long playerId) => playerId < 0;

}

public class BotPlayerState
{
    public long PlayerId { get; set; }

    /// <summary>절단 실험 더미 (#226): AI 정지·불사·오브 자동 리필 — 어드민이 지정한다.</summary>
    public bool IsSwarmCutDummy { get; set; }

    public AreaType CurrentArea { get; set; }
    public int Health { get; set; } = Config.MAX_HEALTH;
    public long LastProximityAttackerPlayerId { get; set; }
    public bool IsEliminated { get; set; }
    public PlayerMatchStatus PlayerMatchStatus { get; set; } = PlayerMatchStatus.ACTIVE;
    public List<int> ActiveBuffIds { get; set; } = new();

    public string Name { get; set; } = "";

    public Cell Cell { get; set; } = new(0, 0);

    public Vector3f Position { get; set; } = new(0f, 0f, 0f);

    public float Rotation { get; set; }

    // 오브 궤도 위상 (#232): 사람 세션과 같은 규칙 — 이동한 거리만큼 돈다. null = 아직 시드 전.
    private float? _orbOrbitPhaseDegrees;
    private Vector3f? _orbOrbitLastPosition;

    /// <summary>오브 궤도 위상 — 서버 전투의 오브별 자리 근거이자 G_TO_C_MOVE 보정값.</summary>
    public float OrbOrbitPhaseDegrees =>
        _orbOrbitPhaseDegrees ?? SwarmOrbOrbit.InitialPhaseDegrees(PlayerId);

    /// <summary>이동 이벤트마다 호출 — 직전 이벤트 위치에서 이번 위치까지 거리만큼 돈다(텔레포트급은 무시).</summary>
    public void AdvanceOrbOrbit(Vector3f newPosition)
    {
        if (_orbOrbitLastPosition != null)
        {
            float dx = newPosition.X - _orbOrbitLastPosition.X;
            float dy = newPosition.Y - _orbOrbitLastPosition.Y;
            _orbOrbitPhaseDegrees = SwarmOrbOrbit.AdvancePhase(
                OrbOrbitPhaseDegrees, MathF.Sqrt(dx * dx + dy * dy));
        }

        _orbOrbitLastPosition = new Vector3f(newPosition.X, newPosition.Y, newPosition.Z);
    }


    // === #127 walking pathfinding ===
    public List<BotPathfinder.Step> Path { get; set; } = new();

    public int PathIndex { get; set; }

    public Vector3f WalkVelocity { get; set; } = new(0f, 0f, 0f);

    public DateTime LastWalkStepTime { get; set; } = DateTime.UtcNow;

    public DateTime LoopWaitUntil { get; set; } = DateTime.MinValue;


    /// <summary>
    ///     현재 지역에 들어온 시각. 방 사냥이 진전 없이 길어졌는지 판정하는 기준이다.
    ///     정상적인 팩 정리는 20초 안에 끝나므로, 이 시각이 오래되면 그 방을 목적지 후보에서 뺀다.
    /// </summary>

    /// <summary>잠긴 문 차단 로그의 중복 억제 — 같은 방에 연속으로 막히면 한 번만 남긴다.</summary>
    public AreaType LastLockedDoorBlockArea { get; set; } = AreaType.None;

    /// <summary>
    ///     정체가 감지되어 현재 방을 떠나야 한다는 요청. 이동 루프 상단에서 세우고
    ///     잔상 사냥 계획이 소비한다. 목적지 커밋이 사냥 계획을 가로막기 때문에 두 단계로 나눈다.
    /// </summary>

    public bool IsChannelHeld { get; set; }

    public DateTime ChannelHoldUntil { get; set; } = DateTime.MinValue;

    public AreaType PendingForcedInteractArea { get; set; } = AreaType.None;

    public int PendingForcedInteractId { get; set; }




    public DateTime RestUntil { get; set; } = DateTime.MinValue;


    public DateTime GameStartTime { get; set; } = DateTime.UtcNow;



    // 카이팅 접선 방향 (2026-08-18 유저 지시 "제자리 좌우 와리가리 금지"): 초마다 좌우를 바꾸던 것을
    // 봇마다 한쪽으로 고정한다 — 그쪽이 막혔을 때만 뒤집는다. 0이면 미정(봇 id 홀짝으로 정한다).

    // 투사체 회피 커밋 (2026-08-18): 한 번 비켜서기 시작한 방향과 유지 시각. 유지 중에는 띠 밖에 나가도
    // 원래 경로로 되돌아가지 않고 제자리에 선다 — 띠 가장자리에서 들락거리는 떨림을 없앤다.
    public float SwarmDodgeDirectionX { get; set; }
    public float SwarmDodgeDirectionY { get; set; }
    public DateTime SwarmDodgeHoldUntilUtc { get; set; } = DateTime.MinValue;

    /// <summary>Safe room retained while the bot is travelling out of a warned area.</summary>
    public AreaType EvacuationDestination { get; set; } = AreaType.None;

    /// <summary>Room goal retained while the bot is travelling for loot, an interaction, or a target.</summary>
    public AreaType MovementDestination { get; set; } = AreaType.None;

    /// <summary>Safe room selected during #214 corridor selection.</summary>

    public SwarmBotMode SwarmMode { get; set; } = SwarmBotMode.None;
    public DateTime SwarmModeUntilUtc { get; set; } = DateTime.MinValue;











    public void HoldForChannel(TimeSpan fallbackDuration)
    {
        IsChannelHeld = true;
        ChannelHoldUntil = DateTime.UtcNow.Add(fallbackDuration);
    }

    /// <summary>위협 감지 시 채집·상호작용 홀드를 즉시 끊는다 — 홀드 채로 맞다 죽는 사고 방지.</summary>
    public void CancelChannelHold()
    {
        IsChannelHeld = false;
        ChannelHoldUntil = DateTime.MinValue;
    }

    /// <summary>마지막 피격 시각 (#222) — 피격 중에는 이동 계획 홀드를 무시하는 판단 입력.</summary>
    public DateTime LastDamagedAtUtc { get; set; } = DateTime.MinValue;

    // 유휴 감시 (#222): 6초 이상 제자리면 원인 진단 로그를 남긴다 — "가만히 서 있는 봇" 추적.
    public Vector3f? IdleWatchLastPosition { get; set; }
    public DateTime IdleWatchLastMovedAtUtc { get; set; } = DateTime.MinValue;
    public DateTime IdleWatchLastLoggedAtUtc { get; set; } = DateTime.MinValue;

    // 유휴 배회 (#222): 도착 대기(캠프 리스폰·사격 대기)로 서 있지 않게 주변을 서성인다.
    public DateTime NextIdleWanderAtUtc { get; set; } = DateTime.MinValue;

    // 부츠·열쇠 (#222 M4): 사람과 같은 규칙으로 봇도 쓴다.
    public DateTime BootsSpeedUntilUtc { get; set; } = DateTime.MinValue;
    public int FreeSummonCharges { get; set; }

    // 빈손 이속 (#223): 지시 판단 틱이 갱신 — 사람과 같은 배율로 도주가 성립하게.
    public bool IsSwarmBareHanded { get; set; }

    // 빈손 가속 만료 (#229 12단계): 마지막 오브를 잃은 직후 2초만 빨라진다.
    // 빈손인 내내 빠르면 "패배 직전"이 아니라 도주 특화 상태가 된다.
    public DateTime SwarmBareSpeedUntilUtc { get; set; } = DateTime.MinValue;

    public int PendingRngInteractId { get; set; }


    /// <summary>이번 매치에서 이 봇이 탐색을 끝낸 방. 방을 이동해도 유지한다.</summary>

    /// <summary>이번 매치에서 이 봇이 실제 RNG 탐색을 완료한 상호작용 지점.</summary>



    /// <summary>Current equipped battle tool, used to synchronize remote bot visuals.</summary>
    public int EquippedBattleItemId { get; set; }

    /// <summary>Server-authoritative movement multiplier from currently living Wind orbs.</summary>
    public float WindMoveSpeedMultiplier { get; set; } = 1f;

    /// <summary>Temporary movement slow applied by a wave counter.</summary>
    public DateTime WaveSlowUntilUtc { get; set; }


    public DateTime RngCollectProgressStartTime { get; set; } = DateTime.MinValue;


    /// <summary>잼 승점 지갑 (#222 M3) — 매치 단위, 소환석과 분리.</summary>
    public int JamCount { get; set; }

    // === #219 스웜 개봉 채집 채널 (레거시 RNG 필드와 분리 — 미션 틱 간섭 방지) ===
    /// <summary>채집 중인 스웜 스팟 Id. 0이면 채널 없음.</summary>
    public int SwarmExploreSpotId { get; set; }

    /// <summary>스웜 채집 채널 시작 시각. MinValue면 채널 없음 — 시작 후 1.5초 경과 시 개봉 확정.</summary>
    public DateTime SwarmExploreStartedAtUtc { get; set; } = DateTime.MinValue;

    // #229: 문 잠금해제 게이지. 사람과 같은 규칙 — 맞으면 풀린다(LastDamagedAtUtc 참조).
    public int SwarmDoorUnlockDoorId { get; set; }
    public DateTime SwarmDoorUnlockStartedAtUtc { get; set; } = DateTime.MinValue;
}
