using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;

namespace game_server.services;

/// <summary>
///     봇 플레이어 상태 관리. v0.2.0 부품 결합 시스템 정합 (#26).
///     - 매칭 봇 채움 시 생성된 봇의 인게임 상태 추적 + 행동 AI 제공.
///     - 핵심 동작은 partial 파일로 분리:
///         - BotPlayerManager.Movement.cs : 목적성 이동 / 폐쇄 회피
///         - BotPlayerManager.Mission.cs  : 부품 회수 / 결합 / 사보타주 / 색출
///         - BotPlayerManager.Interaction.cs : 1:1 동기턴 자동 응답
/// </summary>
public partial class BotPlayerManager
{
    // 폐쇄 불가 6구역 + 폐쇄 대상 9구역 + 외부 1구역(강당) 등 봇이 이동 가능한 전체 16구역.
    // 직책별 발견 구역(Material TargetArea)은 이 안에 포함되어 있다.
    private static readonly AreaType[] MovableAreas =
    {
        // 0층
        AreaType.Junkyard,
        AreaType.Ground,
        // 1층
        AreaType.AdminOffice,
        AreaType.Corridor1F,
        AreaType.StaffRoom,
        AreaType.Gym,
        AreaType.Storage,
        // 2층
        AreaType.Classroom2,
        AreaType.Corridor2F,
        AreaType.Library,
        // 3층
        AreaType.Classroom3,
        AreaType.Corridor3F,
        AreaType.ExamRoom,
        // 4층
        AreaType.Classroom4,
        AreaType.Corridor4F,
        AreaType.BroadcastRoom,
    };

    // 봇 행동 시정수
    private const int BotMoveIntervalSeconds = 12;        // 봇 이동 주기 (자기 직책 발견 구역 순회)
    private const int BotMissionTickIntervalSeconds = 4;  // 봇 미션 행동 (회수/결합) 주기
    private const int BotMoveStaminaCost = 3;             // 이동 시 스태미나 소모
    private const int DetectScoreThreshold = 18;          // 색출 휴리스틱 임계값 — 함정 흔적 발견 누적 점수

    // matchingId → 봇 목록
    private readonly ConcurrentDictionary<long, List<BotPlayerState>> _botStates = new();

    // matchingId → 인스턴스가 사용하는 MapId. 봇 ENTER/MOVE 패킷의 LastMapId/Position 변환에 필요.
    private readonly ConcurrentDictionary<long, MapId> _botMapIds = new();

    private readonly ILogger _logger;

    // H1 결정론 시드 — DemoMode 활성화 시 시드 기반 RNG, 아니면 Random.Shared 위임.
    // 모든 봇 의사결정(이동/색출/응답/사보타주)이 본 인스턴스 사용.
    private readonly Random _rng = DemoMode.IsActive ? new Random(DemoMode.Seed) : Random.Shared;

    public BotPlayerManager(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    ///     매칭에 봇 등록. 직책별 발견 구역 큐를 미리 셔플해 동선에 목적성을 부여한다.
    ///     #125: 봇 위치(Cell/Position) 초기화 — 영역별 스폰 셀에서 시작. 클라 AREA_PLAYER_ENTER 동등.
    /// </summary>
    public void RegisterBots(long matchingId, MapId mapId, List<BotMatchingInfo> botInfoList)
    {
        _botMapIds[matchingId] = mapId;

        var bots = botInfoList.Select(info =>
        {
            var visitQueue = BuildJobAreaQueue(info.MyJobTitle);

            // 봇 시작 영역 = 직책 미션 큐의 첫 영역 (없으면 무작위 fallback).
            var startArea = visitQueue.Count > 0
                ? visitQueue[0]
                : MovableAreas[_rng.Next(MovableAreas.Length)];

            var startCell = GameMapData.GetAreaSpawnCell(mapId, startArea);
            var startPosition = CellToWorldPosition(startCell);

            return new BotPlayerState
            {
                PlayerId = info.PlayerId,
                TargetPlayerId = info.TargetPlayerId,
                MyJobTitle = info.MyJobTitle,
                TargetJobTitle = info.TargetJobTitle,
                Name = $"Bot{Math.Abs(info.PlayerId)}",
                CurrentArea = startArea,
                Cell = startCell,
                Position = startPosition,
                Rotation = 0f,
                Stamina = 100,   // 실제 플레이어와 동일
                Corruption = 66, // 게임 시작 시 오염도 시작값
                ManittoStatus = ManittoStatus.ACTIVE,
                LastMoveTime = DateTime.UtcNow,
                LastMissionTickTime = DateTime.UtcNow,
                LastCellWanderTime = DateTime.UtcNow,
                GameStartTime = DateTime.UtcNow,
                JobAreaQueue = visitQueue,
                JobAreaQueueIndex = 0
            };
        }).ToList();

        _botStates[matchingId] = bots;

        _logger.LogInformation(
            "봇 {Count}명 등록(목적성 동선): MatchingId={MatchingId}, MapId={MapId}, IDs=[{Ids}]",
            bots.Count, matchingId, mapId,
            string.Join(",", bots.Select(b => $"{b.PlayerId}({b.MyJobTitle}@{b.CurrentArea})")));
    }

    /// <summary>
    ///     매칭에서 사용 중인 MapId 조회. 등록되지 않은 매칭이면 MapId.School 폴백.
    /// </summary>
    public MapId GetMatchingMapId(long matchingId)
    {
        return _botMapIds.TryGetValue(matchingId, out var mapId) ? mapId : MapId.School;
    }

    /// <summary>
    ///     봇 기본 의상 (user_server SetupNewPlayer 5종, 액세서리는 직책별로 차등).
    /// </summary>
    private static readonly int[] BotDefaultWearItemIds =
    {
        101000003, // Hair
        102000003, // Face
        104000005, // Top
        105000005, // Bottom
        106000003  // Shoes
    };

    /// <summary>
    ///     봇 직책별 액세서리 (시연 시각 식별용).
    /// </summary>
    private static readonly Dictionary<JobTitle, int> BotAccessoryByJob = new()
    {
        { JobTitle.BROADCAST_MEMBER, 103000001 },  // 리본 헤어밴드
        { JobTitle.DISCIPLINE_MEMBER, 103000004 }, // 프리뮬라
        { JobTitle.SCIENCE_MEMBER, 103000005 },    // 뽀송 귀마개
        { JobTitle.HEALTH_MEMBER, 103000006 }      // 베레모
    };

    /// <summary>
    ///     봇 기본 의상 + 직책별 액세서리 조합 wear list 생성.
    /// </summary>
    private static List<int> BuildBotWearItems(JobTitle jobTitle)
    {
        var list = new List<int>(BotDefaultWearItemIds);
        if (BotAccessoryByJob.TryGetValue(jobTitle, out var accessoryId))
            list.Add(accessoryId);
        return list;
    }

    /// <summary>
    ///     봇의 PlayerInfo를 합성해서 반환 — G_TO_C_AREA_PLAYER_ENTER / G_TO_C_PLAYER_INFO 등
    ///     실제 플레이어 패킷 동등 시각화에 사용.
    ///     #125: 봇은 Redis에 저장되지 않으므로 매 호출 시 BotPlayerState로부터 합성.
    ///     #127: 기본 의상 5종 + 직책별 액세서리(시연 식별).
    /// </summary>
    public PlayerInfo? SynthesizePlayerInfo(long matchingId, long botPlayerId)
    {
        var bot = GetBot(matchingId, botPlayerId);
        if (bot == null) return null;

        var mapId = GetMatchingMapId(matchingId);
        var info = new PlayerInfo
        {
            PlayerId = bot.PlayerId,
            Name = bot.Name,
            State = PlayerState.NONE,
            LastMapId = mapId,
            LastMapSubId = matchingId,
            LastCell = bot.Cell,
            Hp = 5000,
            Stamina = bot.Stamina,
            WearItemIdList = BuildBotWearItems(bot.MyJobTitle)
        };
        info.ObjectInfo = new GameObjectInfo(ObjectType.PLAYER, bot.PlayerId, mapId, matchingId, bot.Cell)
        {
            Position = bot.Position,
            Velocity = new Vector3f(0f, 0f, 0f),
            Rotation = bot.Rotation
        };
        return info;
    }

    /// <summary>
    ///     Cell → World 변환. GameClientSession의 동일 함수와 동일 공식이지만
    ///     BotPlayerManager가 game_server.network에 의존하지 않도록 본 클래스 내부에 두었다.
    /// </summary>
    internal static Vector3f CellToWorldPosition(Cell cell)
    {
        float wX = (cell.X - cell.Y) / 2f;
        float wY = (cell.X + cell.Y) / 4f;
        return new Vector3f(wX, wY, 0f);
    }

    /// <summary>
    ///     매칭의 봇 목록 조회 (탈락 포함)
    /// </summary>
    public List<BotPlayerState> GetBots(long matchingId)
    {
        return _botStates.TryGetValue(matchingId, out var bots) ? bots : [];
    }

    /// <summary>
    ///     특정 봇 조회
    /// </summary>
    public BotPlayerState? GetBot(long matchingId, long playerId)
    {
        if (!_botStates.TryGetValue(matchingId, out var bots)) return null;
        return bots.FirstOrDefault(b => b.PlayerId == playerId);
    }

    /// <summary>
    ///     해당 매칭에 봇이 있는지 확인
    /// </summary>
    public bool HasBots(long matchingId) => _botStates.ContainsKey(matchingId);

    /// <summary>
    ///     봇의 마니또 상태 변경 (체인 단절 / 시한부 진입 등)
    /// </summary>
    public void SetBotManittoStatus(long matchingId, long botPlayerId, ManittoStatus status)
    {
        var bot = GetBot(matchingId, botPlayerId);
        if (bot == null) return;
        bot.ManittoStatus = status;
        _logger.LogInformation("봇 마니또 상태 변경: BotId={BotId}, Status={Status}", botPlayerId, status);
    }

    /// <summary>
    ///     매칭 정리 (게임 종료 시 호출)
    /// </summary>
    public void CleanupMatching(long matchingId)
    {
        _botStates.TryRemove(matchingId, out _);
        _botMapIds.TryRemove(matchingId, out _);
    }

    /// <summary>
    ///     PlayerId가 봇인지 확인 (음수 ID — UserServer 매칭 시 -1, -2, ... 부여)
    /// </summary>
    public static bool IsBotPlayerId(long playerId) => playerId < 0;

    /// <summary>
    ///     탈락하지 않은 봇만 반환
    /// </summary>
    private static IEnumerable<BotPlayerState> GetActiveBots(IEnumerable<BotPlayerState> bots)
        => bots.Where(b => !b.IsEliminated);
}

/// <summary>
///     봇 플레이어 인게임 상태. v0.2.0 부품 시뮬을 위한 상태 누적.
/// </summary>
public class BotPlayerState
{
    public long PlayerId { get; set; }
    public long TargetPlayerId { get; set; }
    public JobTitle MyJobTitle { get; set; }
    public JobTitle TargetJobTitle { get; set; }
    public AreaType CurrentArea { get; set; }
    public int Stamina { get; set; } = 100;
    public int Corruption { get; set; }
    public bool IsEliminated { get; set; }
    public ManittoStatus ManittoStatus { get; set; } = ManittoStatus.ACTIVE;
    public DateTime LastMoveTime { get; set; } = DateTime.UtcNow;

    /// <summary>봇 표시 이름 (PlayerInfo.Name 동등) — 매칭 시 직책+ID로 합성.</summary>
    public string Name { get; set; } = "";

    /// <summary>봇 현재 셀 (실제 플레이어 ObjectInfo.Cell 동등). 영역 전환/셀 wander 시 갱신.</summary>
    public Cell Cell { get; set; } = new(0, 0);

    /// <summary>봇 월드 좌표 (실제 플레이어 ObjectInfo.Position 동등).</summary>
    public Vector3f Position { get; set; } = new(0f, 0f, 0f);

    /// <summary>봇 로테이션 (실제 플레이어 ObjectInfo.Rotation 동등).</summary>
    public float Rotation { get; set; }

    /// <summary>마지막 셀 wander(영역 내 이동) 시각. Phase 2 — 영역 내 자연 이동.</summary>
    public DateTime LastCellWanderTime { get; set; } = DateTime.UtcNow;

    // === #127 walking pathfinding ===
    /// <summary>현재 따라가는 경로. 비어있으면 다음 틱에 새 타겟 결정.</summary>
    public List<BotPathfinder.Step> Path { get; set; } = new();

    /// <summary>Path에서 다음으로 도달할 인덱스. Path 길이와 같으면 도착 완료.</summary>
    public int PathIndex { get; set; }

    /// <summary>현재 진행 방향(월드 좌표) × walkSpeed. 클라 애니메이션용.</summary>
    public Vector3f WalkVelocity { get; set; } = new(0f, 0f, 0f);

    /// <summary>마지막 walk 틱 처리 시각. 250ms 간격 봇 이동 타이머가 사용.</summary>
    public DateTime LastWalkStepTime { get; set; } = DateTime.UtcNow;

    /// <summary>도착 후 walking step 스킵 종료 시각 (자연스러운 휴식).</summary>
    public DateTime LoopWaitUntil { get; set; } = DateTime.MinValue;

    /// <summary>영역 전환 직전 도어 앞에서 잠시 멈춤 종료 시각 (포탈 들어가는 시각적 단서).</summary>
    public DateTime TransitionPauseUntil { get; set; } = DateTime.MinValue;

    /// <summary>1:1 상호작용 응답/대화 진행 중. true면 봇 walking/액션 모두 정지 (실제 플레이어와 동등).</summary>
    public bool IsInInteraction { get; set; }

    /// <summary>상호작용 수락 후 봇 정지 유지 종료 시각. WalkStep이 이 시각 이후 IsInInteraction을 자동 해제.</summary>
    public DateTime InteractionStayUntil { get; set; } = DateTime.MinValue;

    /// <summary>매칭 시작 시각. DemoMode H4 봇 race 페이스 캡 계산용.</summary>
    public DateTime GameStartTime { get; set; } = DateTime.UtcNow;

    // === v0.2.0 부품 시뮬 상태 ===
    /// <summary>마지막 미션 행동(회수/결합) 시각</summary>
    public DateTime LastMissionTickTime { get; set; } = DateTime.UtcNow;

    /// <summary>자기 직책 발견 구역 순회 큐 (셔플된 4개 + 선행 아이템 위치)</summary>
    public List<AreaType> JobAreaQueue { get; set; } = new();

    /// <summary>다음 방문할 큐 인덱스</summary>
    public int JobAreaQueueIndex { get; set; }

    /// <summary>봇이 이미 색출 시도했는지 (1회 한정)</summary>
    public bool HasUsedDetection { get; set; }

    /// <summary>봇이 마지막으로 흔적 함정을 배치한 시각 — 너무 자주 안 깔도록 쿨다운</summary>
    public DateTime LastTracePlaceTime { get; set; } = DateTime.MinValue;

    /// <summary>H6 — 시연 모드 BR 봇이 도서관 함정 흔적을 1회 배치했는지 (캡 강제용).</summary>
    public bool HasPlacedDemoTrapTrace { get; set; }

    /// <summary>봇이 마지막으로 사보타주를 시도한 시각 — 시한부 진입 후 쿨다운</summary>
    public DateTime LastSabotageTryTime { get; set; } = DateTime.MinValue;

    /// <summary>색출 휴리스틱 누적 점수 — 마니또 후보 추리용 (자기 race 진행 방해 흔적 등)</summary>
    public int DetectionUrgency { get; set; }

    /// <summary>봇이 마지막으로 1:1 응답한 상대 (자기 자신과 동일 PlayerId면 응답 X)</summary>
    public long LastInteractRespondedTo { get; set; }

    // === #134 RNG 채집 통합 ===
    /// <summary>봇이 walking으로 접근 중인 InteractObject Id. 0이면 없음.
    /// ChooseNewWanderTarget에서 영역 + 셀 선택 시 설정, 도착 후 RNG 채집 시 0으로 clear.</summary>
    public int PendingRngInteractId { get; set; }

    /// <summary>마지막으로 자동 소모품을 사용한 시각 (재사용 쿨다운).</summary>
    public DateTime LastAutoConsumableUseTime { get; set; } = DateTime.MinValue;
}
