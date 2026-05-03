using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;

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
    /// </summary>
    public void RegisterBots(long matchingId, List<BotMatchingInfo> botInfoList)
    {
        var bots = botInfoList.Select(info =>
        {
            var visitQueue = BuildJobAreaQueue(info.MyJobTitle);
            var startArea = visitQueue.Count > 0
                ? visitQueue[0]
                : MovableAreas[_rng.Next(MovableAreas.Length)];

            return new BotPlayerState
            {
                PlayerId = info.PlayerId,
                TargetPlayerId = info.TargetPlayerId,
                MyJobTitle = info.MyJobTitle,
                TargetJobTitle = info.TargetJobTitle,
                CurrentArea = startArea,
                Stamina = 40,    // 디버깅용 시작값 (정식: 100) — 실제 플레이어와 동일
                Corruption = 66, // 게임 시작 시 오염도 시작값
                ManittoStatus = ManittoStatus.ACTIVE,
                LastMoveTime = DateTime.UtcNow,
                LastMissionTickTime = DateTime.UtcNow,
                GameStartTime = DateTime.UtcNow,
                JobAreaQueue = visitQueue,
                JobAreaQueueIndex = 0
            };
        }).ToList();

        _botStates[matchingId] = bots;

        _logger.LogInformation(
            "봇 {Count}명 등록(목적성 동선): MatchingId={MatchingId}, IDs=[{Ids}]",
            bots.Count, matchingId, string.Join(",", bots.Select(b => $"{b.PlayerId}({b.MyJobTitle})")));
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

    /// <summary>봇이 마지막으로 사보타주를 시도한 시각 — 시한부 진입 후 쿨다운</summary>
    public DateTime LastSabotageTryTime { get; set; } = DateTime.MinValue;

    /// <summary>색출 휴리스틱 누적 점수 — 마니또 후보 추리용 (자기 race 진행 방해 흔적 등)</summary>
    public int DetectionUrgency { get; set; }

    /// <summary>봇이 마지막으로 1:1 응답한 상대 (자기 자신과 동일 PlayerId면 응답 X)</summary>
    public long LastInteractRespondedTo { get; set; }
}
