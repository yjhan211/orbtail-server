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
    private const double BotInitialDecisionDelayMinSeconds = 0.15;
    private const double BotInitialDecisionDelayMaxSeconds = 1.2;

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

        var bots = botPlayerIds.Select(botPlayerId =>
        {
            if (!spawnCells.TryGetValue(botPlayerId, out Cell? assignedSpawn))
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
                    Profile = { Name = $"Player{Math.Abs(botPlayerId)}", Hp = 5000, State = PlayerState.IDLE, WearItemIdList = BuildBotWearItems(botPlayerId) },
                    CurrentArea = startArea,
                    Cell = startCell,
                    Position = startPosition,
                    Rotation = 0f
                },
                LoopWaitUntil = now.AddSeconds(BotInitialDecisionDelayMinSeconds + Random.Shared.NextDouble() *
                    (BotInitialDecisionDelayMaxSeconds - BotInitialDecisionDelayMinSeconds))
            };
        }).ToList();

        _bots = bots;
        _movementPlanningCursor = 0;

        _logger.LogInformation("Bots registered: Count={Count}, MatchingId={MatchingId}, MapId={MapId}, IDs=[{Ids}]", bots.Count, matchingId, Config.SWARM_MATCH_MAP, string.Join(",", bots.Select(b => $"{b.PlayerId}@{b.Player.CurrentArea}")));
    }

    internal static List<int> BuildBotWearItems(long playerId)
    {
        var list = new List<int>(BotDefaultWearItemIds);
        int idx = (int)(Math.Abs(playerId) % BotCustomizationItems.Length);
        list.Add(BotCustomizationItems[idx]);

        return list;
    }

    public PlayerInfo? GetPlayerProfile(long botPlayerId) => GetBot(botPlayerId)?.Player.Profile;
    public GameObjectInfo? SynthesizeGameObjectInfo(long matchingId, long botPlayerId)
    {
        var bot = GetBot(botPlayerId);
        if (bot == null) return null;
        var state = bot.Player.State;
        return new GameObjectInfo(ObjectType.PLAYER, bot.PlayerId, Config.SWARM_MATCH_MAP, matchingId, bot.Player.Cell!)
        {
            Position = new Vector3f(bot.Player.Position!.X, bot.Player.Position!.Y, bot.Player.Position!.Z),
            Rotation = bot.Player.Rotation,
            State = state
        };
    }

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
