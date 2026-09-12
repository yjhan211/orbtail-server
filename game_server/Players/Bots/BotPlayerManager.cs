using game_server.matches;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.players.bots;

public readonly record struct SwarmBotDodgeAdvice(float DirectionX, float DirectionY, float HoldSeconds);

/// <summary>
///     매치 하나의 봇 등록·조회와 경로 재계산 순서를 보관한다.
///     호출자는 매치 잠금을 보유하며, 행동 결정과 실제 이동은 별도 서비스가 처리한다.
/// </summary>
public class BotPlayerManager
{
    private const double BotInitialDecisionDelayMinSeconds = 0.15;
    private const double BotInitialDecisionDelayMaxSeconds = 1.2;

    private readonly long _matchingId;
    private List<Bot> _bots = [];
    private bool _registered;

    private int _movementPlanningCursor;
    private MapId _mapId = Config.SWARM_MATCH_MAP;

    private readonly ILogger _logger;

    private readonly Random _rng = Random.Shared;

    internal BotPlayerManager(long matchingId, ILogger logger)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(matchingId);
        _matchingId = matchingId;
        _logger = logger;
    }

    public void RegisterBots(MapId mapId, IReadOnlyList<long> botPlayerIds, IReadOnlyDictionary<long, Cell> spawnCells)
    {
        _mapId = mapId;

        var bots = botPlayerIds.Select(botPlayerId =>
        {
            if (!spawnCells.TryGetValue(botPlayerId, out Cell? assignedSpawn))
            {
                throw new InvalidOperationException($"Bot {botPlayerId} has no spawn assignment in match {_matchingId}.");
            }
            var startCell = Cell.Clone(assignedSpawn);
            var startArea = GameMapData.GetCurrentArea(mapId, startCell);
            if (startArea == AreaType.None)
            {
                startArea = AreaType.S2Corridor9;
            }

            var startPosition = CellToWorldPosition(mapId, startCell);
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
                LoopWaitUntil = now.AddSeconds(BotInitialDecisionDelayMinSeconds + _rng.NextDouble() *
                    (BotInitialDecisionDelayMaxSeconds - BotInitialDecisionDelayMinSeconds))
            };
        }).ToList();

        _bots = bots;
        _registered = true;
        _movementPlanningCursor = 0;

        _logger.LogInformation(
            "Bots registered: Count={Count}, MatchingId={MatchingId}, MapId={MapId}, IDs=[{Ids}]",
            bots.Count, _matchingId, mapId,
            string.Join(",", bots.Select(b => $"{b.PlayerId}@{b.Player.CurrentArea}")));
    }

    public MapId MapId => _mapId;

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

    internal static List<int> BuildBotWearItems(long playerId)
    {
        var list = new List<int>(BotDefaultWearItemIds);
        int idx = (int)(Math.Abs(playerId) % BotCustomizationItems.Length);
        list.Add(BotCustomizationItems[idx]);

        return list;
    }

    /// <summary>생성 시 초기화한 봇 프로필을 변경 없이 조회한다.</summary>
    public PlayerInfo? GetPlayerProfile(long botPlayerId) =>
        GetBot(botPlayerId)?.Player.Profile;

    public GameObjectInfo? SynthesizeGameObjectInfo(long botPlayerId)
    {
        var bot = GetBot(botPlayerId);
        if (bot == null) return null;
        var state = bot.Player.State;
        return new GameObjectInfo(ObjectType.PLAYER, bot.PlayerId, _mapId, _matchingId, bot.Player.Cell!)
        {
            Position = new Vector3f(bot.Player.Position!.X, bot.Player.Position!.Y, bot.Player.Position!.Z),
            Rotation = bot.Player.Rotation,
            State = state
        };
    }

    internal static Vector3f CellToWorldPosition(MapId mapId, Cell cell) =>
        MapCoordinateConverter.CellToWorld(mapId, cell);

    public List<Bot> GetBots() => _bots;

    public Bot? GetBot(long playerId) => _bots.FirstOrDefault(b => b.PlayerId == playerId);

    public bool HasBots() => _registered;

    internal void Release()
    {
        _bots.Clear();
        _registered = false;
        _movementPlanningCursor = 0;
    }

    public static bool IsBotPlayerId(long playerId) => playerId < 0;

    /// <summary>경로 탐색 비용을 분산하기 위해 매 틱 다음 봇을 순환 선택한다.</summary>
    internal long SelectMovementPlanningBot(IReadOnlyList<Bot> activeBots)
    {
        int cursor = _movementPlanningCursor = (_movementPlanningCursor + 1) % activeBots.Count;
        return activeBots[cursor % activeBots.Count].PlayerId;
    }

}
