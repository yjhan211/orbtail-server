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
    public void BotMovementSkipsChecklistAndQueuesRealRoomExplore()
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
        Assert.True(bot.PendingRngInteractId > 0);
        Assert.Equal(InteractionType.RNG_COLLECT,
            GameInteractableData.Get(bot.PendingRngInteractId)!.InteractionType);
    }

    [Fact]
    public void BotMovementSkipsCooldownMarkers()
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
        int expectedMarkerId = markerIds[0];

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

            Assert.Equal(expectedMarkerId, bot.PendingRngInteractId);
        }
        finally
        {
            RngCollectCooldownStore.ClearMatching(matchingId);
        }
    }

    [Fact]
    public void DepletedRoomIsMarkedCompleteWithoutQueueingAnotherExplore()
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
        Assert.Contains(AreaType.Classroom3, bot.CompletedRoomExploreAreas);
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
    public void CompletedLegacyMissionStillAllowsRecorderLootPickupAndEquip()
    {
        const long matchingId = 194104;
        const long botPlayerId = -1941041;
        var fixture = CreateFixture(matchingId, botPlayerId, AreaType.Classroom3);
        var bot = fixture.BotManager.GetBot(matchingId, botPlayerId)!;
        var marker = GameInteractableData.GetByZone((int)AreaType.Classroom3)
            .First(info => info.InteractionType == InteractionType.RNG_COLLECT &&
                           (info.CellX != 0 || info.CellY != 0));
        bot.Cell = new Cell(marker.CellX, marker.CellY);
        bot.Position = new Vector3f(
            (marker.CellX - marker.CellY) / 2f,
            (marker.CellX + marker.CellY) / 4f,
            0f);

        var missionManager = new MissionManager(NullLogger.Instance);
        missionManager.InitializePlayer(matchingId, botPlayerId, JobTitle.SCIENCE_MEMBER);
        missionManager.GetState(matchingId, botPlayerId)!.IsCompleted = true;
        var itemPoolManager = new ItemPoolManager();
        itemPoolManager.Initialize();

        try
        {
            Assert.True(fixture.BotManager.TrySendBotToInteract(
                matchingId,
                botPlayerId,
                AreaType.Classroom3,
                marker.Id,
                fixture.ClosureManager));

            BackdateMissionTick(bot);
            var started = fixture.BotManager.ProcessBotMissionTick(
                matchingId,
                missionManager,
                fixture.InventoryManager,
                itemPoolManager,
                fixture.AreaStockManager,
                fixture.GroundItemManager,
                fixture.ChecklistManager);
            Assert.Single(started.BotExploreStarts);

            BackdateMissionTick(bot);
            bot.RngCollectProgressStartTime = DateTime.UtcNow.AddSeconds(-3);
            var completed = fixture.BotManager.ProcessBotMissionTick(
                matchingId,
                missionManager,
                fixture.InventoryManager,
                itemPoolManager,
                fixture.AreaStockManager,
                fixture.GroundItemManager,
                fixture.ChecklistManager);
            var spawnedRecorder = Assert.Single(completed.GroundItemSpawns);
            Assert.Equal(107000010, spawnedRecorder.ItemId);
            Assert.Contains(marker.Id, bot.ExploredRngInteractIds);
            Assert.Equal(GroundItemClaimStatus.Reserved,
                fixture.GroundItemManager.TryClaim(
                    matchingId, spawnedRecorder.GroundItemUid, botPlayerId - 99, AreaType.Classroom3,
                    spawnedRecorder.PositionX, spawnedRecorder.PositionY, _ => true, out _));

            bot.LastWalkStepTime = DateTime.UtcNow.AddSeconds(-1);
            var pickup = fixture.BotManager.ProcessBotMovementTick(
                matchingId,
                fixture.ClosureManager,
                fixture.AreaStockManager,
                new Dictionary<long, AreaType>(),
                fixture.ChecklistManager,
                fixture.InventoryManager,
                fixture.GroundItemManager,
                Array.Empty<BotCombatTargetSnapshot>());
            Assert.Single(pickup.GroundItemPickups);
            Assert.Equal(107000010,
                fixture.InventoryManager.GetEquippedBattleItem(matchingId, botPlayerId)!.ItemId);
            Assert.Equal(107000010, bot.EquippedBattleItemId);

            BackdateMissionTick(bot);
            var equipped = fixture.BotManager.ProcessBotMissionTick(
                matchingId,
                missionManager,
                fixture.InventoryManager,
                itemPoolManager,
                fixture.AreaStockManager,
                fixture.GroundItemManager,
                fixture.ChecklistManager);
            Assert.Empty(equipped.BattleItemEquips);
        }
        finally
        {
            RngCollectCooldownStore.ClearMatching(matchingId);
        }
    }

    private static BotLootFixture CreateFixture(long matchingId, long botPlayerId, AreaType startArea)
    {
        var botManager = new BotPlayerManager(NullLogger.Instance);
        botManager.RegisterBots(matchingId, MapId.School,
        [
            new BotMatchingInfo
            {
                PlayerId = botPlayerId,
                TargetPlayerId = botPlayerId - 1,
                MyJobTitle = JobTitle.SCIENCE_MEMBER,
                TargetJobTitle = JobTitle.HEALTH_MEMBER,
                StartArea = startArea
            }
        ]);
        var bot = botManager.GetBot(matchingId, botPlayerId)!;
        bot.LoopWaitUntil = DateTime.MinValue;
        bot.LastWalkStepTime = DateTime.UtcNow.AddSeconds(-1);

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
            [botPlayerId],
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
}
