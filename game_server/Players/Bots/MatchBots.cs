using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.players.bots;

/// <summary>
///     매치 하나의 봇 등록·조회와 경로 재계산 순서를 보관한다.
///     호출자는 매치 잠금을 보유하며, 행동 결정과 실제 이동은 별도 서비스가 처리한다.
/// </summary>
public class MatchBots
{
    private readonly ILogger _logger;

    private List<Bot> _bots = [];
    private int _movementPlanningCursor;
    public List<Bot> GetBots() => _bots;
    public Bot? GetBot(long playerId) => _bots.FirstOrDefault(b => b.PlayerId == playerId);
    internal MatchBots(ILogger logger)
    {
        _logger = logger;
    }

    public void RegisterBots(long matchingId, IReadOnlyList<long> botPlayerIds, IReadOnlyDictionary<long, Cell> spawnCells)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(matchingId);
        double minDecisionDelay = Config.SWARM_BOT_INITIAL_DECISION_DELAY_MIN_SECONDS;
        double maxDecisionDelay = Config.SWARM_BOT_INITIAL_DECISION_DELAY_MAX_SECONDS;
        if (!double.IsFinite(minDecisionDelay) || !double.IsFinite(maxDecisionDelay) || minDecisionDelay < 0 || maxDecisionDelay < minDecisionDelay)
        {
            throw new InvalidOperationException("Invalid bot initial decision delay range.");
        }

        var bots = botPlayerIds.Select(botPlayerId =>
        {
            if (!spawnCells.TryGetValue(botPlayerId, out var assignedSpawn))
            {
                throw new InvalidOperationException($"Bot {botPlayerId} has no spawn assignment in match {matchingId}.");
            }
            var startCell = Cell.Clone(assignedSpawn);
            var startArea = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, startCell);
            if (startArea == AreaType.None)
            {
                startArea = AreaType.S2Corridor9;
            }

            var startPosition = CellToWorldPosition(Config.SWARM_MATCH_MAP, startCell);
            var now = DateTime.UtcNow;

            return new Bot
            {
                PlayerId = botPlayerId,
                Player =
                {
                    Profile = { Name = $"Player{Math.Abs(botPlayerId)}", WearItemIdList = BuildBotWearItems(botPlayerId) },
                    CurrentArea = startArea,
                    Cell = startCell,
                    Position = startPosition,
                    Rotation = 0f
                },
                LoopWaitUntil = now.AddSeconds(minDecisionDelay + Random.Shared.NextDouble() *
                    (maxDecisionDelay - minDecisionDelay))
            };
        }).ToList();

        _bots = bots;
        _movementPlanningCursor = 0;

        _logger.LogInformation("Bots registered: Count={Count}, MatchingId={MatchingId}, MapId={MapId}, IDs=[{Ids}]", bots.Count, matchingId, Config.SWARM_MATCH_MAP, string.Join(",", bots.Select(b => $"{b.PlayerId}@{b.Player.CurrentArea}")));
    }

    internal static List<int> BuildBotWearItems(long playerId)
    {
        var list = new List<int>(Config.SWARM_BOT_DEFAULT_WEAR_ITEM_IDS);
        var customizationItems = Config.SWARM_BOT_CUSTOMIZATION_ITEM_IDS;
        if (customizationItems.Length == 0)
        {
            throw new InvalidOperationException("Bot customization items must not be empty.");
        }
        int idx = (int)(Math.Abs(playerId) % customizationItems.Length);
        list.Add(customizationItems[idx]);

        return list;
    }

    public PlayerInfo? GetPlayerProfile(long botPlayerId) => GetBot(botPlayerId)?.Player.Profile;
    public PlayerPresenceInfo? GetPlayerObjectInfo(long botPlayerId) => GetBot(botPlayerId)?.Player.CreatePlayerObjectInfo();

    internal static Vector3f CellToWorldPosition(MapId mapId, Cell cell) => MapCoordinateConverter.CellToWorld(mapId, cell);

    public bool HasBots() => _bots.Count > 0;

    internal void Release()
    {
        _bots.Clear();
        _movementPlanningCursor = 0;
    }

    internal long SelectMovementPlanningBot(IReadOnlyList<Bot> activeBots)
    {
        int cursor = _movementPlanningCursor = (_movementPlanningCursor + 1) % activeBots.Count;
        return activeBots[cursor % activeBots.Count].PlayerId;
    }
}
