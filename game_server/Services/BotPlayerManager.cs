using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using network.common;
using network.common.data;
using network.common.data.models;
using network.helpers;

namespace game_server.services;

/// <summary>
///     遊??뚮젅?댁뼱 ?곹깭 愿由? v0.2.0 遺??寃고빀 ?쒖뒪???뺥빀 (#26).
///     - 留ㅼ묶 遊?梨꾩? ???앹꽦??遊뉗쓽 ?멸쾶???곹깭 異붿쟻 + ?됰룞 AI ?쒓났.
///     - ?듭떖 ?숈옉? partial ?뚯씪濡?遺꾨━:
///         - BotPlayerManager.Movement.cs : 紐⑹쟻???대룞 / ?먯뇙 ?뚰뵾
///         - BotPlayerManager.Mission.cs  : 遺???뚯닔 / 寃고빀 / ?щ낫?二?/ ?됱텧
///         - BotPlayerManager.Interaction.cs : 1:1 ?숆린???먮룞 ?묐떟
/// </summary>
public partial class BotPlayerManager
{
    /// <summary>
    ///     클리어 전이라 문이 잠긴 방 목록 제공자. 사람은 이동 검증이 문을 막지만 봇은
    ///     서버가 직접 걷게 하므로, 같은 규칙을 봇 경로 결정에서 강제한다.
    /// </summary>
    private Func<long, IReadOnlyCollection<AreaType>>? _lockedRoomAreasProvider;

    public void SetLockedRoomAreasProvider(Func<long, IReadOnlyCollection<AreaType>> provider) =>
        _lockedRoomAreasProvider = provider ?? throw new ArgumentNullException(nameof(provider));

    // ?꾨줈??0: ?쒖꽦 怨듦컙 = 3쨌4痢?6援ъ뿭(1쨌2痢??대룞??李⑤떒, 3??留??대룞).
    //   諛??뺤떊???뚮났 媛??: Classroom3(2-1)/ExamRoom(怨좎궗??/Classroom4(3-1)/BroadcastRoom(諛⑹넚??
    //   蹂듬룄(transit, ?뚮났 ?놁쓬 + ?κ린 泥대쪟 ???몄젒 諛?媛뺤젣 ?좊룄): Corridor
    // ?뚮났???쇱뼱?섎뒗 "諛?(蹂듬룄 ?쒖쇅). ?寃?異붿쟻/?좊낫湲?湲곗쿃??湲곗? 援ъ뿭.
    private static readonly AreaType[] Proto0Rooms =
    {
        AreaType.Classroom3,
        AreaType.ExamRoom,
        AreaType.Classroom4,
        AreaType.BroadcastRoom,
    };

    private static readonly AreaType[] Proto0SpawnAreas =
    {
        AreaType.AdminOffice,
        AreaType.StaffRoom,
        AreaType.Classroom2,
        AreaType.Library,
        AreaType.Classroom3,
        AreaType.ExamRoom,
        AreaType.Classroom4,
        AreaType.BroadcastRoom,
    };

    // ?꾨줈??0: 遊뉗씠 ?뚮났(?寃?異붿쟻) ????좊낫湲?理쒖? ?몄썝 諛?濡?媛???뺣쪧. ?쒕떇 ?몃툕.
    private const double Proto0TestProbability = 0.3;

    // ?꾨줈??0: ?먰븯??諛??寃?諛??좊낫湲?諛????꾩갑??癒몃Т???쒓컙(珥?.
    // ???숈븞 ?뚮났쨌湲곗쿃???볦씠怨? 留뚮즺 ?꾩뿉???ㅼ쓬 寃곗젙(癒몃Ъ湲??좊낫湲????쒕떎.
    // (?놁쑝硫?癒몃Ъ湲?寃곗젙??留???250ms) ?ш뎬由쇰릺???좊낫湲??뺣쪧??怨㏓컮濡??곗졇 ?섍?踰꾨┛??)
    private const double Proto0InitialDecisionDelayMinSeconds = 0.15;
    private const double Proto0InitialDecisionDelayMaxSeconds = 1.2;
    private const double Proto0RoomDwellMinSeconds = 1.25;
    private const double Proto0RoomDwellMaxSeconds = 2.25;

    private enum Proto0BotPolicy
    {
        SimpleTracker,
        DisguiseMvp
    }

    private static readonly BotProto0Profile[] Proto0Profiles =
    {
        BotProto0Profile.SurvivalFirst,
        BotProto0Profile.StealthFirst,
        BotProto0Profile.AggressiveProbe,
        BotProto0Profile.CrowdSeeking,
        BotProto0Profile.QuietRoomSeeking,
    };

    private const Proto0BotPolicy ActiveProto0BotPolicy = Proto0BotPolicy.DisguiseMvp;
    private const double Proto0FollowDelayMinSeconds = 3;
    private const double Proto0FollowDelayMaxSeconds = 8;
    private const double Proto0FakeMoveCooldownSeconds = 12;
    private const double Proto0ProbeCooldownSeconds = 15;
    private const int Proto0CrowdedRoomThreshold = 3;

    private const int BotMoveIntervalSeconds = 12;
    private const int BotMissionTickIntervalSeconds = 1;
    private const int DetectScoreThreshold = 18;          // ?됱텧 ?대━?ㅽ떛 ?꾧퀎媛????⑥젙 ?붿쟻 諛쒓껄 ?꾩쟻 ?먯닔
    private const int InitialStamina = 100;
    private const int InitialCorruption = 0;

    // matchingId ??遊?紐⑸줉
    private readonly ConcurrentDictionary<long, List<BotPlayerState>> _botStates = new();

    // Cell BFS is expensive enough that replanning every bot in one 50 ms tick stalls broadcasts.
    // Rotate one planning slot per matching while every bot keeps walking its existing path.
    private readonly ConcurrentDictionary<long, int> _botMovementPlanningCursors = new();

    // matchingId ???몄뒪?댁뒪媛 ?ъ슜?섎뒗 MapId. 遊?ENTER/MOVE ?⑦궥??LastMapId/Position 蹂?섏뿉 ?꾩슂.
    private readonly ConcurrentDictionary<long, MapId> _botMapIds = new();

    private readonly ILogger _logger;

    // H1 寃곗젙濡??쒕뱶 ??legacy mode ?쒖꽦?????쒕뱶 湲곕컲 RNG, ?꾨땲硫?Random.Shared ?꾩엫.
    // 紐⑤뱺 遊??섏궗寃곗젙(?대룞/?됱텧/?묐떟/?щ낫?二???蹂??몄뒪?댁뒪 ?ъ슜.
    private readonly Random _rng = Random.Shared;

    public BotPlayerManager(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    ///     留ㅼ묶??遊??깅줉. 吏곸콉蹂?諛쒓껄 援ъ뿭 ?먮? 誘몃━ ?뷀뵆???숈꽑??紐⑹쟻?깆쓣 遺?ы븳??
    ///     #125: 遊??꾩튂(Cell/Position) 珥덇린?????곸뿭蹂??ㅽ룿 ??먯꽌 ?쒖옉. ?대씪 AREA_PLAYER_ENTER ?숇벑.
    /// </summary>
    public void RegisterBots(long matchingId, MapId mapId, List<BotMatchingInfo> botInfoList)
    {
        _botMapIds[matchingId] = mapId;

        var bots = botInfoList.Select((info, index) =>
        {
            var hasAssignedSpawn = info.SpawnCell is { X: not 0 } || info.SpawnCell is { Y: not 0 };
            var startCell = hasAssignedSpawn
                ? Cell.Clone(info.SpawnCell)
                : GameMapData.GetAreaSpawnCell(mapId, IsAllowedAssignedStartArea(mapId, info.StartArea)
                    ? info.StartArea
                    : Proto0SpawnAreas[_rng.Next(Proto0SpawnAreas.Length)]);
            var startArea = GameMapData.GetCurrentArea(mapId, startCell);
            if (startArea == AreaType.None)
            {
                startArea = AreaType.Corridor;
            }

            var startPosition = CellToWorldPosition(mapId, startCell);
            var now = DateTime.UtcNow;

            return new BotPlayerState
            {
                PlayerId = info.PlayerId,
                TargetPlayerId = info.TargetPlayerId,
                MyJobTitle = info.MyJobTitle,
                TargetJobTitle = info.TargetJobTitle,
                Name = $"Player{Math.Abs(info.PlayerId)}",
                CurrentArea = startArea,
                Cell = startCell,
                Position = startPosition,
                Rotation = 0f,
                Persona = PersonaType.None,
                ActiveBuffIds = info.ActiveBuffIds is { Count: > 0 }
                    ? new List<int>(info.ActiveBuffIds)
                    : new List<int>(),
                Proto0Profile = Proto0Profiles[index % Proto0Profiles.Length],
                Stamina = InitialStamina,
                Corruption = InitialCorruption,
                ManittoStatus = ManittoStatus.ACTIVE,
                LastMoveTime = now,
                LastMissionTickTime = now.AddMilliseconds(-_rng.Next(BotMissionTickIntervalSeconds * 1000)),
                LastCellWanderTime = now,
                GameStartTime = now,
                NextPveKiteRepathAt = now.AddMilliseconds(
                    Math.Abs(info.PlayerId % 8) * 100d),
                LoopWaitUntil = now.AddSeconds(RandomRange(
                    Proto0InitialDecisionDelayMinSeconds,
                    Proto0InitialDecisionDelayMaxSeconds)),
                JobAreaQueue = new List<AreaType>(),
                JobAreaQueueIndex = 0
            };
        }).ToList();

        _botStates[matchingId] = bots;
        _botMovementPlanningCursors[matchingId] = 0;

        _logger.LogInformation(
            "遊?{Count}紐??깅줉(紐⑹쟻???숈꽑): MatchingId={MatchingId}, MapId={MapId}, IDs=[{Ids}]",
            bots.Count, matchingId, mapId,
            string.Join(",", bots.Select(b => $"{b.PlayerId}({b.MyJobTitle}/{b.Persona}@{b.CurrentArea})")));
    }

    private static bool IsAllowedAssignedStartArea(MapId mapId, AreaType area)
    {
        return IsSecludedFarmingArea(mapId, area);
    }

    /// <summary>
    /// Rooms where an unarmed survivor can farm without deliberately lingering in a corridor or a large open zone.
    /// Corridors may still be crossed by the pathfinder while travelling between these rooms.
    /// </summary>
    private static bool IsSecludedFarmingArea(MapId mapId, AreaType area)
    {
        if (area == AreaType.None || area.IsCorridor())
            return false;

        return area is not (AreaType.Ground or AreaType.Gym or AreaType.Storage) &&
               GameMapData.GetAreas(mapId).Any(region => region.AreaType == area);
    }



    /// <summary>
    ///     留ㅼ묶?먯꽌 ?ъ슜 以묒씤 MapId 議고쉶. ?깅줉?섏? ?딆? 留ㅼ묶?대㈃ MapId.School ?대갚.
    /// </summary>
    public MapId GetMatchingMapId(long matchingId)
    {
        return _botMapIds.TryGetValue(matchingId, out var mapId) ? mapId : MapId.School;
    }

    /// <summary>
    ///     遊?湲곕낯 ?섏긽 (user_server SetupNewPlayer 5醫? ?≪꽭?쒕━??吏곸콉蹂꾨줈 李⑤벑).
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
    ///     遊?而ㅼ뒪?곕쭏?댁쭠 ?꾩씠????由щ낯 ?ㅼ뼱諛대뱶 / ?꾨━裕щ씪 / 戮??洹留덇컻 / 踰좊젅紐?
    ///     遊뉖쭏??playerId濡??쒕줈 ?ㅻⅨ 1醫낆쓣 諛곗젙?쒕떎(4醫???遊?4紐?1:1).
    /// </summary>
    private static readonly int[] BotCustomizationItems =
    {
        103000001, // 由щ낯 ?ㅼ뼱諛대뱶
        103000004, // ?꾨━裕щ씪
        103000005, // 戮??洹留덇컻
        103000006
    };

    /// <summary>
    ///     遊?湲곕낯 ?섏긽 + 遊뉖퀎 而ㅼ뒪?곕쭏?댁쭠 ?꾩씠??1醫?議고빀 wear list ?앹꽦.
    /// </summary>
    private static List<int> BuildBotWearItems(BotPlayerState bot)
    {
        var list = new List<int>(BotDefaultWearItemIds);
        int idx = (int)(Math.Abs(bot.PlayerId) % BotCustomizationItems.Length);
        list.Add(BotCustomizationItems[idx]);
        if (bot.EquippedBattleItemId > 0)
            list.Add(bot.EquippedBattleItemId);
        return list;
    }

    /// <summary>
    ///     遊뉗쓽 PlayerInfo瑜??⑹꽦?댁꽌 諛섑솚 ??G_TO_C_AREA_PLAYER_ENTER / G_TO_C_PLAYER_INFO ??    ///     ?ㅼ젣 ?뚮젅?댁뼱 ?⑦궥 ?숇벑 ?쒓컖?붿뿉 ?ъ슜.
    ///     #125: 遊뉗? Redis????λ릺吏 ?딆쑝誘濡?留??몄텧 ??BotPlayerState濡쒕????⑹꽦.
    ///     #127: 湲곕낯 ?섏긽 5醫?+ 吏곸콉蹂??≪꽭?쒕━(?쒖뿰 ?앸퀎).
    /// </summary>
    public PlayerInfo? SynthesizePlayerInfo(long matchingId, long botPlayerId)
    {
        var bot = GetBot(matchingId, botPlayerId);
        if (bot == null) return null;

        var mapId = GetMatchingMapId(matchingId);
        // 遊뉗씠 RNG progress 以묒씠硫?EXPLORE_1濡??⑹꽦 ???곸뿭 吏꾩엯 ???대씪媛 遊?罹먮┃???먯깋 ?좊땲 利됱떆 ?쒖떆.
        var state = bot.RestUntil != DateTime.MinValue && DateTime.UtcNow < bot.RestUntil
            ? PlayerState.SLEEP
            : bot.RngCollectProgressStartTime != DateTime.MinValue ||
              Config.CHECKLIST_SYSTEM_ENABLED &&
              bot.ChecklistActivityProgressStartTime != DateTime.MinValue
                ? PlayerState.EXPLORE_1
                : PlayerState.IDLE;
        var info = new PlayerInfo
        {
            PlayerId = bot.PlayerId,
            Name = bot.Name,
            State = state,
            LastMapId = mapId,
            LastMapSubId = matchingId,
            LastCell = bot.Cell,
            Hp = 5000,
            Stamina = bot.Stamina,
            WearItemIdList = BuildBotWearItems(bot)
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
    ///     Cell ??World 蹂?? GameClientSession???숈씪 ?⑥닔? ?숈씪 怨듭떇?댁?留?    ///     BotPlayerManager媛 game_server.network???섏〈?섏? ?딅룄濡?蹂??대옒???대????먯뿀??
    /// </summary>
    internal static Vector3f CellToWorldPosition(MapId mapId, Cell cell) =>
        MapCoordinateConverter.CellToWorld(mapId, cell);

    /// <summary>
    ///     留ㅼ묶??遊?紐⑸줉 議고쉶 (?덈씫 ?ы븿)
    /// </summary>
    public List<BotPlayerState> GetBots(long matchingId)
    {
        return _botStates.TryGetValue(matchingId, out var bots) ? bots : [];
    }

    public IReadOnlyList<long> GetActiveMatchingIds()
    {
        return _botStates.Keys.ToList();
    }

    /// <summary>
    ///     ?뱀젙 遊?議고쉶
    /// </summary>
    public BotPlayerState? GetBot(long matchingId, long playerId)
    {
        if (!_botStates.TryGetValue(matchingId, out var bots)) return null;
        return bots.FirstOrDefault(b => b.PlayerId == playerId);
    }

    /// <summary>?꾨줈??0: ?뱀젙 ?곸뿭???앹〈 遊???(?뺤떊???뚮났 2/N ?몄썝 怨꾩궛??.</summary>
    public int CountBotsInArea(long matchingId, AreaType area)
    {
        if (!_botStates.TryGetValue(matchingId, out var bots)) return 0;
        return bots.Count(b => !b.IsEliminated && b.CurrentArea == area);
    }

    /// <summary>
    ///     ?대떦 留ㅼ묶??遊뉗씠 ?덈뒗吏 ?뺤씤
    /// </summary>
    public bool HasBots(long matchingId) => _botStates.ContainsKey(matchingId);

    /// <summary>
    ///     遊뉗쓽 留덈땲???곹깭 蹂寃?(泥댁씤 ?⑥젅 / ?쒗븳遺 吏꾩엯 ??
    /// </summary>
    public void SetBotManittoStatus(long matchingId, long botPlayerId, ManittoStatus status)
    {
        var bot = GetBot(matchingId, botPlayerId);
        if (bot == null) return;
        bot.ManittoStatus = status;
        _logger.LogInformation("遊?留덈땲???곹깭 蹂寃? BotId={BotId}, Status={Status}", botPlayerId, status);
    }

    /// <summary>
    ///     留ㅼ묶 ?뺣━ (寃뚯엫 醫낅즺 ???몄텧)
    /// </summary>
    public void CleanupMatching(long matchingId)
    {
        _botStates.TryRemove(matchingId, out _);
        _botMapIds.TryRemove(matchingId, out _);
        _botMovementPlanningCursors.TryRemove(matchingId, out _);
    }

    /// <summary>
    ///     PlayerId媛 遊뉗씤吏 ?뺤씤 (?뚯닔 ID ??UserServer 留ㅼ묶 ??-1, -2, ... 遺??
    /// </summary>
    public static bool IsBotPlayerId(long playerId) => playerId < 0;

    /// <summary>
    ///     ?덈씫?섏? ?딆? 遊뉖쭔 諛섑솚
    /// </summary>
    private static IEnumerable<BotPlayerState> GetActiveBots(IEnumerable<BotPlayerState> bots)
        => bots.Where(b => !b.IsEliminated);
}

/// <summary>
///     遊??뚮젅?댁뼱 ?멸쾶???곹깭. v0.2.0 遺???쒕????꾪븳 ?곹깭 ?꾩쟻.
/// </summary>
public enum BotProto0Profile
{
    SurvivalFirst,
    StealthFirst,
    AggressiveProbe,
    CrowdSeeking,
    QuietRoomSeeking
}

public class BotPlayerState
{
    public long PlayerId { get; set; }
    public long TargetPlayerId { get; set; }
    public JobTitle MyJobTitle { get; set; }
    public JobTitle TargetJobTitle { get; set; }
    public AreaType CurrentArea { get; set; }
    public int Stamina { get; set; } = 100;
    public int Corruption { get; set; } = 0;
    public long LastProximityAttackerPlayerId { get; set; }
    public bool IsForcedFollowActive { get; set; }
    public bool IsEliminated { get; set; }
    public ManittoStatus ManittoStatus { get; set; } = ManittoStatus.ACTIVE;
    public DateTime LastMoveTime { get; set; } = DateTime.UtcNow;
    public PersonaType Persona { get; set; } = PersonaType.None;
    public List<int> ActiveBuffIds { get; set; } = new();
    public BotProto0Profile Proto0Profile { get; set; } = BotProto0Profile.SurvivalFirst;
    public AreaType LastSeenTargetArea { get; set; } = AreaType.None;
    public DateTime NextTargetFollowAllowedAt { get; set; } = DateTime.MinValue;
    public DateTime LastFakeMoveTime { get; set; } = DateTime.MinValue;
    public DateTime LastProbeMoveTime { get; set; } = DateTime.MinValue;

    /// <summary>遊??쒖떆 ?대쫫 (PlayerInfo.Name ?숇벑) ??留ㅼ묶 ??吏곸콉+ID濡??⑹꽦.</summary>
    public string Name { get; set; } = "";

    /// <summary>遊??꾩옱 ? (?ㅼ젣 ?뚮젅?댁뼱 ObjectInfo.Cell ?숇벑). ?곸뿭 ?꾪솚/? wander ??媛깆떊.</summary>
    public Cell Cell { get; set; } = new(0, 0);

    /// <summary>遊??붾뱶 醫뚰몴 (?ㅼ젣 ?뚮젅?댁뼱 ObjectInfo.Position ?숇벑).</summary>
    public Vector3f Position { get; set; } = new(0f, 0f, 0f);

    /// <summary>遊?濡쒗뀒?댁뀡 (?ㅼ젣 ?뚮젅?댁뼱 ObjectInfo.Rotation ?숇벑).</summary>
    public float Rotation { get; set; }

    /// <summary>留덉?留?? wander(?곸뿭 ???대룞) ?쒓컖. Phase 2 ???곸뿭 ???먯뿰 ?대룞.</summary>
    public DateTime LastCellWanderTime { get; set; } = DateTime.UtcNow;

    // === #127 walking pathfinding ===
    /// <summary>?꾩옱 ?곕씪媛??寃쎈줈. 鍮꾩뼱?덉쑝硫??ㅼ쓬 ?깆뿉 ???寃?寃곗젙.</summary>
    public List<BotPathfinder.Step> Path { get; set; } = new();

    /// <summary>Path?먯꽌 ?ㅼ쓬?쇰줈 ?꾨떖???몃뜳?? Path 湲몄씠? 媛숈쑝硫??꾩갑 ?꾨즺.</summary>
    public int PathIndex { get; set; }

    /// <summary>?꾩옱 吏꾪뻾 諛⑺뼢(?붾뱶 醫뚰몴) 횞 walkSpeed. ?대씪 ?좊땲硫붿씠?섏슜.</summary>
    public Vector3f WalkVelocity { get; set; } = new(0f, 0f, 0f);

    /// <summary>留덉?留?walk ??泥섎━ ?쒓컖. 250ms 媛꾧꺽 遊??대룞 ??대㉧媛 ?ъ슜.</summary>
    public DateTime LastWalkStepTime { get; set; } = DateTime.UtcNow;

    /// <summary>?꾩갑 ??walking step ?ㅽ궢 醫낅즺 ?쒓컖 (?먯뿰?ㅻ윭???댁떇).</summary>
    public DateTime LoopWaitUntil { get; set; } = DateTime.MinValue;

    /// <summary>?곸뿭 ?꾪솚 吏곸쟾 ?꾩뼱 ?욎뿉???좎떆 硫덉땄 醫낅즺 ?쒓컖 (?ы깉 ?ㅼ뼱媛???쒓컖???⑥꽌).</summary>
    public DateTime TransitionPauseUntil { get; set; } = DateTime.MinValue;

    /// <summary>
    ///     현재 지역에 들어온 시각. 방 사냥이 진전 없이 길어졌는지 판정하는 기준이다.
    ///     정상적인 팩 정리는 20초 안에 끝나므로, 이 시각이 오래되면 그 방을 목적지 후보에서 뺀다.
    /// </summary>
    public DateTime RoomHuntStartedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>잠긴 문 차단 로그의 중복 억제 — 같은 방에 연속으로 막히면 한 번만 남긴다.</summary>
    public AreaType LastLockedDoorBlockArea { get; set; } = AreaType.None;

    /// <summary>
    ///     정체가 감지되어 현재 방을 떠나야 한다는 요청. 이동 루프 상단에서 세우고
    ///     잔상 사냥 계획이 소비한다. 목적지 커밋이 사냥 계획을 가로막기 때문에 두 단계로 나눈다.
    /// </summary>
    public bool RoomHuntEscapeRequested { get; set; }

    /// <summary>1:1 ?곹샇?묒슜 ?묐떟/???吏꾪뻾 以? true硫?遊?walking/?≪뀡 紐⑤몢 ?뺤? (?ㅼ젣 ?뚮젅?댁뼱? ?숇벑).</summary>
    public bool IsInInteraction { get; set; }

    /// <summary>?곹샇?묒슜 ?섎씫 ??遊??뺤? ?좎? 醫낅즺 ?쒓컖. WalkStep?????쒓컖 ?댄썑 IsInInteraction???먮룞 ?댁젣.</summary>
    public DateTime InteractionStayUntil { get; set; } = DateTime.MinValue;

    public AreaType PendingForcedInteractArea { get; set; } = AreaType.None;

    public int PendingForcedInteractId { get; set; }

    public int PendingChecklistTaskId { get; set; }

    public int PendingChecklistInteractId { get; set; }

    public DateTime ChecklistActivityProgressStartTime { get; set; } = DateTime.MinValue;

    public DateTime RestUntil { get; set; } = DateTime.MinValue;

    public DateTime NextRestTickAt { get; set; } = DateTime.MinValue;

    /// <summary>留ㅼ묶 ?쒖옉 ?쒓컖. legacy mode H4 遊?race ?섏씠??罹?怨꾩궛??</summary>
    public DateTime GameStartTime { get; set; } = DateTime.UtcNow;

    // === v0.2.0 遺???쒕? ?곹깭 ===
    /// <summary>留덉?留?誘몄뀡 ?됰룞(?뚯닔/寃고빀) ?쒓컖</summary>
    public DateTime LastMissionTickTime { get; set; } = DateTime.UtcNow;

    /// <summary>Next time the bot may replace its chase or retreat path.</summary>
    public DateTime NextCombatRepathAt { get; set; } = DateTime.MinValue;

    /// <summary>Next time the bot may re-plan a short lateral path around nearby afterimages.</summary>
    public DateTime NextPveKiteRepathAt { get; set; } = DateTime.MinValue;

    /// <summary>Safe room retained while the bot is travelling out of a warned area.</summary>
    public AreaType EvacuationDestination { get; set; } = AreaType.None;

    /// <summary>Room most recently abandoned because of a nearby combat threat.</summary>
    public AreaType RecentCombatRetreatOrigin { get; set; } = AreaType.None;

    /// <summary>Prevents loot routing from immediately sending the bot back into the room it fled.</summary>
    public DateTime CombatRetreatOriginBlockedUntil { get; set; } = DateTime.MinValue;

    /// <summary>Room goal retained while the bot is travelling for loot, an interaction, or a target.</summary>
    public AreaType MovementDestination { get; set; } = AreaType.None;

    /// <summary>Safe room selected during #214 corridor selection.</summary>
    public AreaType SurvivorRoomChoice { get; set; } = AreaType.None;

    /// <summary>?먭린 吏곸콉 諛쒓껄 援ъ뿭 ?쒗쉶 ??(?뷀뵆??4媛?+ ?좏뻾 ?꾩씠???꾩튂)</summary>
    public List<AreaType> JobAreaQueue { get; set; } = new();

    /// <summary>?ㅼ쓬 諛⑸Ц?????몃뜳??/summary>
    public int JobAreaQueueIndex { get; set; }

    /// <summary>遊뉗씠 ?대? ?됱텧 ?쒕룄?덈뒗吏 (1???쒖젙)</summary>
    public bool HasUsedDetection { get; set; }

    /// <summary>遊뉗씠 留덉?留됱쑝濡??붿쟻 ?⑥젙??諛곗튂???쒓컖 ???덈Т ?먯＜ ??源붾룄濡?荑⑤떎??/summary>
    public DateTime LastTracePlaceTime { get; set; } = DateTime.MinValue;

    /// <summary>H6 ???쒖뿰 紐⑤뱶 BR 遊뉗씠 ?꾩꽌愿 ?⑥젙 ?붿쟻??1??諛곗튂?덈뒗吏 (罹?媛뺤젣??.</summary>
    public bool HasPlacedDemoTrapTrace { get; set; }

    /// <summary>遊뉗씠 留덉?留됱쑝濡??щ낫?二쇰? ?쒕룄???쒓컖 ???쒗븳遺 吏꾩엯 ??荑⑤떎??/summary>
    public DateTime LastSabotageTryTime { get; set; } = DateTime.MinValue;

    /// <summary>?됱텧 ?대━?ㅽ떛 ?꾩쟻 ?먯닔 ??留덈땲???꾨낫 異붾━??(?먭린 race 吏꾪뻾 諛⑺빐 ?붿쟻 ??</summary>
    public int DetectionUrgency { get; set; }

    /// <summary>遊뉗씠 留덉?留됱쑝濡?1:1 ?묐떟???곷? (?먭린 ?먯떊怨??숈씪 PlayerId硫??묐떟 X)</summary>
    public long LastInteractRespondedTo { get; set; }

    public long PresenceBookmarkPlayerId { get; set; }

    public Dictionary<long, DateTime> TargetEncounterStartedAtByPlayerId { get; } = new();
    public HashSet<long> TargetInterrogationRequestedInEncounterPlayerIds { get; } = new();

    public void SetPresenceBookmark(long targetPlayerId)
    {
        if (PresenceBookmarkPlayerId == targetPlayerId) return;

        PresenceBookmarkPlayerId = targetPlayerId;
        TargetEncounterStartedAtByPlayerId.Clear();
        TargetInterrogationRequestedInEncounterPlayerIds.Clear();
    }

    public void HoldForInteraction(TimeSpan fallbackDuration)
    {
        IsInInteraction = true;
        InteractionStayUntil = DateTime.UtcNow.Add(fallbackDuration);
        TransitionPauseUntil = DateTime.MinValue;
    }

    // === #134 RNG 梨꾩쭛 ?듯빀 ===
    /// <summary>遊뉗씠 walking?쇰줈 ?묎렐 以묒씤 InteractObject Id. 0?대㈃ ?놁쓬.
    /// ChooseNewWanderTarget?먯꽌 ?곸뿭 + ? ?좏깮 ???ㅼ젙, ?꾩갑 ??RNG 梨꾩쭛 ??0?쇰줈 clear.</summary>
    public int PendingRngInteractId { get; set; }

    /// <summary>?꾩옱 ?곸뿭 ?댁뿉???꾩쭅 ?먯깋?섏? ?딆? InteractObject Id ??
    /// ?곸뿭 吏꾩엯 ??洹??곸뿭??紐⑤뱺 ?꾨낫濡?梨꾩?. RNG 梨꾩쭛 ??泥?踰덉㎏瑜?爰쇰궡 ?ㅼ쓬 ?濡?walking.
    /// 鍮꾨㈃ ChooseNewWanderTarget???ㅼ쓬 ?곸뿭 寃곗젙.</summary>
    public List<int> InteractQueueInArea { get; set; } = new();

    /// <summary>이번 매치에서 이 봇이 탐색을 끝낸 방. 방을 이동해도 유지한다.</summary>
    public HashSet<AreaType> CompletedRoomExploreAreas { get; } = new();

    /// <summary>이번 매치에서 이 봇이 실제 RNG 탐색을 완료한 상호작용 지점.</summary>
    public HashSet<int> ExploredRngInteractIds { get; } = new();

    public AreaType RoomExploreQueueArea { get; set; } = AreaType.None;

    /// <summary>留덉?留됱쑝濡??먮룞 ?뚮え?덉쓣 ?ъ슜???쒓컖 (?ъ궗??荑⑤떎??.</summary>
    public DateTime LastAutoConsumableUseTime { get; set; } = DateTime.MinValue;

    /// <summary>Current equipped battle tool, used to synchronize remote bot visuals.</summary>
    public int EquippedBattleItemId { get; set; }

    /// <summary>Server-authoritative wind resonance movement state.</summary>
    public bool WindResonanceActive { get; set; }

    /// <summary>Temporary movement slow applied by a wave counter.</summary>
    public DateTime WaveSlowUntilUtc { get; set; }


    /// <summary>RNG 梨꾩쭛 progress ?쒖옉 ?쒓컖. 0?대㈃ ?꾩쭅 ?쒖옉 ???? ?쒖옉 ??1.5珥?寃쎄낵 ??寃곌낵 ?곗텧.</summary>
    public DateTime RngCollectProgressStartTime { get; set; } = DateTime.MinValue;

    /// <summary>walking ?쒖옉 ??G_TO_C_EXPLORE_END broadcast媛 ?꾩슂?쒖? ??ChooseNewWanderTarget??set, ?ㅼ쓬 ProcessBotMovementTick?먯꽌 ?섏쭛 + reset.</summary>
    public bool PendingExploreEndBroadcast { get; set; }
}
