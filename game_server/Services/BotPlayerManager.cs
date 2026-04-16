using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data.models;

namespace game_server.services;

/// <summary>
///     봇 플레이어 상태 관리.
///     매칭 봇 채움(30초 대기) 시 생성된 봇의 인게임 상태를 추적하고 기본 AI를 제공한다.
/// </summary>
public class BotPlayerManager
{
    private const int BotMoveIntervalSeconds = 15; // 봇 이동 주기

    // 봇이 이동 가능한 구역 (전체 16구역)
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

    // matchingId → 봇 목록
    private readonly ConcurrentDictionary<long, List<BotPlayerState>> _botStates = new();
    private readonly ILogger _logger;

    public BotPlayerManager(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    ///     매칭에 봇 등록
    /// </summary>
    public void RegisterBots(long matchingId, List<BotMatchingInfo> botInfoList)
    {
        var bots = botInfoList.Select(info => new BotPlayerState
        {
            PlayerId = info.PlayerId,
            TargetPlayerId = info.TargetPlayerId,
            MyJobTitle = info.MyJobTitle,
            TargetJobTitle = info.TargetJobTitle,
            CurrentArea = MovableAreas[Random.Shared.Next(MovableAreas.Length)],
            Stamina = 100,
            Corruption = 0,
            ManittoStatus = ManittoStatus.ACTIVE,
            LastMoveTime = DateTime.UtcNow
        }).ToList();

        _botStates[matchingId] = bots;

        _logger.LogInformation("봇 {Count}명 등록: MatchingId={MatchingId}, IDs=[{Ids}]",
            bots.Count, matchingId, string.Join(",", bots.Select(b => b.PlayerId)));
    }

    /// <summary>
    ///     매칭의 봇 목록 조회
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
    ///     봇 AI 틱: 주기적 이동 + 자원 변동
    /// </summary>
    public void ProcessBotTick(long matchingId, int corruptionDelta, AreaClosureManager areaClosureManager)
    {
        if (!_botStates.TryGetValue(matchingId, out var bots)) return;

        foreach (var bot in bots)
        {
            if (bot.IsEliminated) continue;

            // 오염도 적용
            int totalCorruptionDelta = corruptionDelta;
            if (bot.ManittoStatus == ManittoStatus.TERMINAL)
                totalCorruptionDelta += 5; // 시한부 추가

            bot.Corruption = Math.Clamp(bot.Corruption + totalCorruptionDelta, 0, 100);

            // 폐쇄 구역 체류 시 스태미나 감소
            if (areaClosureManager.IsAreaClosed(matchingId, bot.CurrentArea))
                bot.Stamina = Math.Max(0, bot.Stamina - 20);

            // 탈락 체크
            if (bot.Stamina <= 0 || bot.Corruption >= 100)
            {
                bot.IsEliminated = true;
                _logger.LogInformation("봇 탈락: MatchingId={MatchingId}, BotId={BotId}, 사유={Reason}",
                    matchingId, bot.PlayerId, bot.Stamina <= 0 ? "스태미나" : "오염도");
            }

            // 주기적 이동
            if ((DateTime.UtcNow - bot.LastMoveTime).TotalSeconds >= BotMoveIntervalSeconds)
            {
                var openAreas = MovableAreas
                    .Where(a => !areaClosureManager.IsAreaClosed(matchingId, a))
                    .ToArray();

                if (openAreas.Length > 0)
                {
                    bot.CurrentArea = openAreas[Random.Shared.Next(openAreas.Length)];
                    bot.Stamina = Math.Max(0, bot.Stamina - 3); // 이동 스태미나 소모
                }

                bot.LastMoveTime = DateTime.UtcNow;
            }
        }
    }

    /// <summary>
    ///     봇의 마니또 상태 변경
    /// </summary>
    public void SetBotManittoStatus(long matchingId, long botPlayerId, ManittoStatus status)
    {
        var bot = GetBot(matchingId, botPlayerId);
        if (bot == null) return;
        bot.ManittoStatus = status;
        _logger.LogInformation("봇 마니또 상태 변경: BotId={BotId}, Status={Status}", botPlayerId, status);
    }

    /// <summary>
    ///     매칭 정리
    /// </summary>
    public void CleanupMatching(long matchingId)
    {
        _botStates.TryRemove(matchingId, out _);
    }

    /// <summary>
    ///     PlayerId가 봇인지 확인 (음수 ID)
    /// </summary>
    public static bool IsBotPlayerId(long playerId) => playerId < 0;
}

/// <summary>
///     봇 플레이어 인게임 상태
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
}
