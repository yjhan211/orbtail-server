using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class BotSurvivorLootingTests
{
    public BotSurvivorLootingTests()
    {
        GameDataHelper.SetBasePath(Path.Combine(FindRepositoryRoot(), "network"));
        GameDataHelper.Initialize();
    }

    [Fact]
    public void SurvivorP0DoesNotCreateChecklistTasks()
    {
        const long matchingId = 194100;
        const long botPlayerId = -1941001;
        var checklist = new ChecklistManager(NullLogger.Instance);
        checklist.Initialize();

        checklist.StartRound(
            matchingId,
            1,
            [botPlayerId],
            _ => new ChecklistChainContext(TargetAlive: true, ManittoAlive: true));

        Assert.False(Config.CHECKLIST_SYSTEM_ENABLED);
        Assert.Empty(checklist.GetActiveTasks(matchingId, botPlayerId));
        Assert.Null(checklist.GetNextActiveGeneralInteractTask(matchingId, botPlayerId));
    }

    [Fact]
    public void BotMovementSkipsChecklistAndDoesNotQueueLegacyExplore()
    {
        const long matchingId = 194101;
        const long botPlayerId = -1941011;
        var fixture = CreateFixture(matchingId, botPlayerId, AreaType.Classroom3);
        var bot = fixture.BotManager.GetBot(matchingId, botPlayerId)!;

        fixture.BotManager.ProcessBotMovementTick(
            matchingId,
            fixture.ClosureManager,
            fixture.AreaStockManager,
            new Dictionary<long, AreaType>(),
            fixture.ChecklistManager,
            fixture.InventoryManager,
            fixture.GroundItemManager,
            Array.Empty<BotCombatTargetSnapshot>());

        Assert.Equal(0, bot.PendingChecklistTaskId);
        Assert.Equal(0, bot.PendingChecklistInteractId);
        Assert.Equal(0, bot.PendingRngInteractId);
        Assert.Empty(bot.InteractQueueInArea);
    }

    [Fact]
    public void BotMovementIgnoresLegacyRngMarkersEvenWhenOffCooldown()
    {
        const long matchingId = 194102;
        const long botPlayerId = -1941021;
        var fixture = CreateFixture(matchingId, botPlayerId, AreaType.Classroom3);
        var bot = fixture.BotManager.GetBot(matchingId, botPlayerId)!;
        var markerIds = GameInteractableData.GetByZone((int)AreaType.Classroom3)
            .Where(info => info.InteractionType == InteractionType.RNG_COLLECT &&
                           (info.CellX != 0 || info.CellY != 0))
            .Select(info => info.Id)
            .ToArray();
        Assert.True(markerIds.Length >= 2);

        try
        {
            foreach (int markerId in markerIds.Skip(1))
                RngCollectCooldownStore.SetCooldown(matchingId, markerId, 30);

            fixture.BotManager.ProcessBotMovementTick(
                matchingId,
                fixture.ClosureManager,
                fixture.AreaStockManager,
                new Dictionary<long, AreaType>(),
                fixture.ChecklistManager,
                fixture.InventoryManager,
                fixture.GroundItemManager,
                Array.Empty<BotCombatTargetSnapshot>());

            Assert.Equal(0, bot.PendingRngInteractId);
        }
        finally
        {
            RngCollectCooldownStore.ClearMatching(matchingId);
        }
    }

    [Theory]
    [InlineData(201000008, 40, 70, 40, 55)]
    [InlineData(201000011, 40, 70, 55, 70)]
    public void ImmediateUsePickupAppliesRecoveryAndDisappearsEvenWhenBotInventoryIsFull(
        int itemId,
        int initialStamina,
        int initialCorruption,
        int expectedStamina,
        int expectedCorruption)
    {
        long matchingId = 194106 + itemId;
        long botPlayerId = -matchingId * 10 - 1;
        var fixture = CreateFixture(matchingId, botPlayerId, AreaType.Classroom3);
        var bot = fixture.BotManager.GetBot(matchingId, botPlayerId)!;
        bot.Stamina = initialStamina;
        bot.Corruption = initialCorruption;

        for (int i = 0; i < Config.SURVIVOR_INVENTORY_SLOT_COUNT; i++)
            Assert.True(fixture.InventoryManager.TryAddItemWithCapacity(
                matchingId, botPlayerId, 107000003 + i, Config.SURVIVOR_INVENTORY_SLOT_COUNT, out _));

        var groundItem = Assert.Single(fixture.GroundItemManager.SpawnItems(
            matchingId,
            bot.CurrentArea,
            bot.Position.X,
            bot.Position.Y,
            [itemId]));

        Assert.True(fixture.BotManager.TryAutoPickupGroundItem(
            bot,
            matchingId,
            fixture.InventoryManager,
            fixture.GroundItemManager,
            out BotGroundItemPickup? pickup));
        Assert.True(pickup.HasValue);
        Assert.True(pickup.Value.AutoUsed);
        Assert.Equal(expectedStamina, bot.Stamina);
        Assert.Equal(expectedCorruption, bot.Corruption);
        Assert.Equal(Config.SURVIVOR_INVENTORY_SLOT_COUNT,
            fixture.InventoryManager.GetAllItems(matchingId, botPlayerId).Count);
        Assert.DoesNotContain(
            fixture.GroundItemManager.GetSnapshot(matchingId, bot.CurrentArea),
            item => item.GroundItemUid == groundItem.GroundItemUid);
    }

    [Fact]
    public void BotAutomaticallyCollectsWorldSummonStoneWithoutUsingInventory()
    {
        const long matchingId = 194120;
        const long botPlayerId = -1941201;
        var fixture = CreateFixture(matchingId, botPlayerId, AreaType.Classroom3);
        var bot = fixture.BotManager.GetBot(matchingId, botPlayerId)!;
        var stones = new SummonStoneManager();
        // 봇 반응 지연 (#222): 갓 떨어진 돌은 못 줍고, 지연 창이 지나야 반응한다.
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero));
        var groundItemManager = new GroundItemManager(clock);
        groundItemManager.InitializeMatching(matchingId);

        Assert.Single(groundItemManager.SpawnItems(
            matchingId,
            bot.CurrentArea,
            bot.Position.X,
            bot.Position.Y,
            [Config.SUMMON_STONE_GROUND_ITEM_ID]));

        Assert.False(fixture.BotManager.TryAutoPickupGroundItem(
            bot,
            matchingId,
            fixture.InventoryManager,
            groundItemManager,
            stones,
            out _));

        clock.Advance(BotPlayerManager.SummonStoneBotReactionDelay);
        Assert.True(fixture.BotManager.TryAutoPickupGroundItem(
            bot,
            matchingId,
            fixture.InventoryManager,
            groundItemManager,
            stones,
            out BotGroundItemPickup? pickup));
        Assert.True(pickup.HasValue);
        Assert.Equal(1, pickup.Value.SummonStoneAmount);
        Assert.Equal(1, pickup.Value.SummonStoneBalance);
        Assert.Equal(1, stones.GetSnapshot(matchingId, botPlayerId).StoneCount);
        Assert.Empty(fixture.InventoryManager.GetAllItems(matchingId, botPlayerId));
    }
    [Fact]
    public void BotDoesNotEvaluateLegacyRoomStockForMovement()
    {
        const long matchingId = 194103;
        const long botPlayerId = -1941031;
        var fixture = CreateFixture(matchingId, botPlayerId, AreaType.Classroom3);
        var bot = fixture.BotManager.GetBot(matchingId, botPlayerId)!;
        while (fixture.AreaStockManager.TryConsumeDrop(matchingId, (int)AreaType.Classroom3, out _))
        {
        }

        fixture.BotManager.ProcessBotMovementTick(
            matchingId,
            fixture.ClosureManager,
            fixture.AreaStockManager,
            new Dictionary<long, AreaType>(),
            fixture.ChecklistManager,
            fixture.InventoryManager,
            fixture.GroundItemManager,
            Array.Empty<BotCombatTargetSnapshot>());

        Assert.Equal(0, bot.PendingRngInteractId);
        Assert.DoesNotContain(AreaType.Classroom3, bot.CompletedRoomExploreAreas);
        Assert.Empty(bot.InteractQueueInArea);
    }

    [Fact]
    public void ExploreDropIsReservedForDiscovererThenBecomesPublic()
    {
        const long matchingId = 194105;
        const long discovererPlayerId = -1941051;
        const long otherPlayerId = -1941052;
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero));
        var manager = new GroundItemManager(clock);
        var spawned = manager.SpawnItems(
            matchingId,
            AreaType.Classroom3,
            0f,
            0f,
            [107000003, 201000011],
            discovererPlayerId: discovererPlayerId,
            discovererPickupWindow: GroundItemManager.DiscovererPickupWindow);

        Assert.Equal(GroundItemClaimStatus.Reserved,
            manager.TryClaim(matchingId, spawned[0].GroundItemUid, otherPlayerId, AreaType.Classroom3,
                spawned[0].PositionX, spawned[0].PositionY, _ => true, out _));
        Assert.Equal(GroundItemClaimStatus.Success,
            manager.TryClaim(matchingId, spawned[0].GroundItemUid, discovererPlayerId, AreaType.Classroom3,
                spawned[0].PositionX, spawned[0].PositionY, _ => true, out _));

        clock.Advance(GroundItemManager.DiscovererPickupWindow);
        var expired = Assert.Single(manager.ExpireClaimReservations(matchingId));
        Assert.Equal(spawned[1].GroundItemUid, expired.GroundItemUid);
        Assert.Equal(discovererPlayerId, expired.DiscovererPlayerId);
        Assert.Equal(GroundItemClaimStatus.Success,
            manager.TryClaim(matchingId, spawned[1].GroundItemUid, otherPlayerId, AreaType.Classroom3,
                spawned[1].PositionX, spawned[1].PositionY, _ => true, out _));

        var releasedItem = Assert.Single(manager.SpawnItems(
            matchingId, AreaType.Classroom3, 0f, 0f, [107000003],
            discovererPlayerId: discovererPlayerId,
            discovererPickupWindow: GroundItemManager.DiscovererPickupWindow));
        manager.ReleaseClaimReservationsForPlayer(matchingId, discovererPlayerId);

        Assert.Equal(GroundItemClaimStatus.Success,
            manager.TryClaim(matchingId, releasedItem.GroundItemUid, otherPlayerId, AreaType.Classroom3,
                releasedItem.PositionX, releasedItem.PositionY, _ => true, out _));
    }

    [Fact]
    public void CompletedLegacyMissionDoesNotRestartLegacyExplore()
    {
        const long matchingId = 194104;
        const long botPlayerId = -1941041;
        var fixture = CreateFixture(matchingId, botPlayerId, AreaType.Classroom3);
        var bot = fixture.BotManager.GetBot(matchingId, botPlayerId)!;
        var missionManager = new MissionManager(NullLogger.Instance);
        missionManager.InitializePlayer(matchingId, botPlayerId, JobTitle.SCIENCE_MEMBER);
        missionManager.GetState(matchingId, botPlayerId)!.IsCompleted = true;
        var itemPoolManager = new ItemPoolManager();
        itemPoolManager.Initialize();

        BackdateMissionTick(bot);
        var result = fixture.BotManager.ProcessBotMissionTick(
            matchingId,
            missionManager,
            fixture.InventoryManager,
            itemPoolManager,
            fixture.AreaStockManager,
            fixture.GroundItemManager,
            fixture.ChecklistManager);

        Assert.Empty(result.BotExploreStarts);
        Assert.Empty(result.GroundItemSpawns);
        Assert.Equal(0, bot.PendingRngInteractId);
    }

    [Fact]
    public void MovementPlanningRotatesAcrossActiveBots()
    {
        const long matchingId = 194107;
        long[] botPlayerIds = [-1941071, -1941072, -1941073];
        var fixture = CreateFixture(matchingId, botPlayerIds, AreaType.Classroom3);
        var planningBotIds = new List<long>();

        for (int i = 0; i < botPlayerIds.Length; i++)
        {
            var result = fixture.BotManager.ProcessBotMovementTick(
                matchingId,
                fixture.ClosureManager,
                fixture.AreaStockManager,
                new Dictionary<long, AreaType>(),
                fixture.ChecklistManager,
                fixture.InventoryManager,
                fixture.GroundItemManager,
                Array.Empty<BotCombatTargetSnapshot>());
            planningBotIds.Add(result.PlanningBotId);
        }

        Assert.Equal(botPlayerIds.Length, planningBotIds.Distinct().Count());
        Assert.All(planningBotIds, planningBotId => Assert.Contains(planningBotId, botPlayerIds));
    }

    private static BotLootFixture CreateFixture(long matchingId, long botPlayerId, AreaType startArea) =>
        CreateFixture(matchingId, [botPlayerId], startArea);

    private static BotLootFixture CreateFixture(
        long matchingId,
        IReadOnlyList<long> botPlayerIds,
        AreaType startArea)
    {
        var botManager = new BotPlayerManager(NullLogger.Instance);
        botManager.RegisterBots(matchingId, MapId.School,
            botPlayerIds.Select(botPlayerId => new BotMatchingInfo
            {
                PlayerId = botPlayerId,
                TargetPlayerId = botPlayerId - 1,
                MyJobTitle = JobTitle.SCIENCE_MEMBER,
                TargetJobTitle = JobTitle.HEALTH_MEMBER,
                StartArea = startArea
            }).ToList());
        foreach (long botPlayerId in botPlayerIds)
        {
            var bot = botManager.GetBot(matchingId, botPlayerId)!;
            bot.LoopWaitUntil = DateTime.MinValue;
            bot.LastWalkStepTime = DateTime.UtcNow.AddSeconds(-1);
        }

        var config = new MatchingConfigService(null!, NullLogger.Instance);
        var closureManager = new AreaClosureManager(NullLogger.Instance, config);
        closureManager.InitializeMatching(matchingId);

        var areaStockManager = new AreaItemStockManager(new ZeroRandom());
        areaStockManager.InitializeMatching(matchingId);
        var inventoryManager = new InGameInventoryManager();
        inventoryManager.Initialize();
        var groundItemManager = new GroundItemManager();
        groundItemManager.InitializeMatching(matchingId);
        var checklistManager = new ChecklistManager(NullLogger.Instance);
        checklistManager.Initialize();
        checklistManager.StartRound(
            matchingId,
            1,
            botPlayerIds,
            _ => new ChecklistChainContext(TargetAlive: true, ManittoAlive: true));

        return new BotLootFixture(
            botManager,
            closureManager,
            areaStockManager,
            inventoryManager,
            groundItemManager,
            checklistManager);
    }

    private static void BackdateMissionTick(BotPlayerState bot) =>
        bot.LastMissionTickTime = DateTime.UtcNow.AddSeconds(-2);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "network", "Common", "csv")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output path.");
    }

    private sealed class ManualTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta)
        {
            _utcNow = _utcNow.Add(delta);
        }
    }

    private sealed class ZeroRandom : Random
    {
        public override int Next(int maxValue) => 0;
    }

    private sealed record BotLootFixture(
        BotPlayerManager BotManager,
        AreaClosureManager ClosureManager,
        AreaItemStockManager AreaStockManager,
        InGameInventoryManager InventoryManager,
        GroundItemManager GroundItemManager,
        ChecklistManager ChecklistManager);

    [Fact]
    public void CutOrbDrops_ExpireAfterTheirLifetime()
    {
        // #229: 절단 낙수는 오브 그대로 떨어지고 짧은 수명을 갖는다. 수명이 없으면
        // 후반에 바닥이 오브밭이 되어 "지금 주울까 도망갈까"가 사라진다.
        const long matchingId = 194130;
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 16, 0, 0, 0, TimeSpan.Zero));
        var manager = new GroundItemManager(clock);
        manager.InitializeMatching(matchingId);

        var withLifetime = manager.SpawnItems(
            matchingId, AreaType.Gym, 10f, 10f, [Config.SUMMON_STONE_GROUND_ITEM_ID],
            lifetime: TimeSpan.FromSeconds(12));
        var forever = manager.SpawnItems(
            matchingId, AreaType.Gym, 12f, 12f, [Config.SUMMON_STONE_GROUND_ITEM_ID]);
        Assert.Single(withLifetime);
        Assert.Single(forever);

        // 수명 전에는 아무것도 안 사라진다.
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Empty(manager.ExpireGroundItems(matchingId));
        Assert.True(manager.Exists(matchingId, withLifetime[0].GroundItemUid));

        // 수명이 지나면 그것만 사라지고, 수명 없는 낙수는 남는다.
        clock.Advance(TimeSpan.FromSeconds(2));
        var expired = manager.ExpireGroundItems(matchingId);
        Assert.Single(expired);
        Assert.Equal(withLifetime[0].GroundItemUid, expired[0].GroundItemUid);
        Assert.False(manager.Exists(matchingId, withLifetime[0].GroundItemUid));
        Assert.True(manager.Exists(matchingId, forever[0].GroundItemUid));
    }
}
