using game_server.logging;
using game_server.players;
using game_server.combat;
using game_server.field;
using game_server.orbs;
using game_server.matches;
using game_server.sessions;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;

namespace game_server.players.bots;

/// <summary>
///     투사체 회피 조언 (#232 §9): 비켜설 월드 방향과, 그 위협의 앞머리가 이 봇을 지나갈 때까지의 시간.
///     봇은 이 시간 동안 회피를 커밋한다 — 띠 밖으로 나가면 서서 기다리고, 원래 경로로 되돌아가지 않는다.
/// </summary>
public readonly record struct SwarmBotDodgeAdvice(float DirectionX, float DirectionY, float HoldSeconds);

/// <summary>매치 하나의 봇과 이동 계획 상태를 관리한다. 호출은 해당 매치 잠금 안에서 실행한다.</summary>
public partial class BotPlayerManager
{
    // 같은 매치의 문 상태를 이동 판정에 사용한다.
    private readonly DoorState _doors;

    // 회피 판단 시 같은 매치의 최신 공격 스냅샷을 읽는다.
    private readonly SunOrbAttackState _sunOrbAttacks;

    // 배회 폴백(잔상 사냥 실패 시)에서 최저 인원 방으로 흩어질 확률 — 봇이 한 방에 뭉치지 않게.
    private const double SwarmWanderScatterProbability = 0.3;

    private const double BotInitialDecisionDelayMinSeconds = 0.15;
    private const double BotInitialDecisionDelayMaxSeconds = 1.2;
    private const double BotRoomDwellMinSeconds = 1.25;
    private const double BotRoomDwellMaxSeconds = 2.25;

    private const int BotMissionTickIntervalSeconds = 1;

    private readonly long _matchingId;
    private List<BotPlayerState> _bots = [];
    private bool _registered;
    private bool _released;

    // Cell BFS is expensive enough that replanning every bot in one 50 ms tick stalls broadcasts.
    // Rotate one planning slot per matching while every bot keeps walking its existing path.
    private int _movementPlanningCursor;

    private MapId _mapId = Config.SWARM_MATCH_MAP;

    private readonly ILogger _logger;
    private readonly GameEventLogManager _eventLogs;

    private readonly Random _rng = Random.Shared;

    internal BotPlayerManager(long matchingId, ILogger logger, DoorState doors, SunOrbAttackState sunOrbAttacks, GameEventLogManager eventLogs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(matchingId);
        _matchingId = matchingId;
        _logger = logger;
        _eventLogs = eventLogs;
        _doors = doors ?? throw new ArgumentNullException(nameof(doors));
        _sunOrbAttacks = sunOrbAttacks ?? throw new ArgumentNullException(nameof(sunOrbAttacks));
    }

    /// <summary>
    ///     매치 구성이 정한 봇 ID와 스폰으로 봇 상태를 만든다. 스폰은 MatchSpawnData가 사람과 함께 배정한 값이다.
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
                Player =
                {
                    CurrentArea = startArea,
                    Cell = startCell,
                    Position = startPosition,
                    Rotation = 0f
                },
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
            string.Join(",", bots.Select(b => $"{b.PlayerId}@{b.Player.CurrentArea}")));
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

        return list;
    }

    public PlayerInfo? CreatePlayerInfo(long matchingId, long botPlayerId)
    {
        var bot = GetBot(matchingId, botPlayerId);
        if (bot == null) return null;

        var mapId = GetMatchingMapId(matchingId);
        var state = bot.Player.State;
        var info = bot.Player.Profile;
        info.Name = bot.Name;
        info.State = state;
        info.Hp = 5000;
        info.WearItemIdList = BuildBotWearItems(bot);
        return info;
    }

    public GameObjectInfo? SynthesizeGameObjectInfo(long matchingId, long botPlayerId)
    {
        var bot = GetBot(matchingId, botPlayerId);
        if (bot == null) return null;
        var state = bot.Player.State;
        return new GameObjectInfo(ObjectType.PLAYER, bot.PlayerId, GetMatchingMapId(matchingId), matchingId, bot.Player.Cell!)
        {
            Position = new Vector3f(bot.Player.Position!.X, bot.Player.Position!.Y, bot.Player.Position!.Z),
            Rotation = bot.Player.Rotation,
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
        _released = true;
        _bots.Clear();
        _registered = false;
        _movementPlanningCursor = 0;
    }

    public static bool IsBotPlayerId(long playerId) => playerId < 0;

}
