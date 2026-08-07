using game_server.services;
using Microsoft.Extensions.Logging.Abstractions;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class SurvivorBotLoopRegressionTests
{
    private const int RecorderT1 = 107000003;
    private const int RecorderT2 = 107000004;
    private const int RecorderT3 = 107000006;
    private const int CannedCoffee = 201000011;
    private const int DoubleShotCoffee = 201000020;

    public SurvivorBotLoopRegressionTests()
    {
        GameDataHelper.SetBasePath(Path.Combine(FindRepositoryRoot(), "network"));
        GameDataHelper.Initialize();
    }

    [Theory]
    [InlineData(201000008, 201000019)]
    [InlineData(CannedCoffee, DoubleShotCoffee)]
    public void BotConsumableLoadoutMergesEachRecoveryRecipe(int inputItemId, int outputItemId)
    {
        const long matchingId = 194199;
        const long botPlayerId = -1941991;
        var inventory = new InGameInventoryManager();
        inventory.Initialize();
        inventory.AddItem(matchingId, botPlayerId, inputItemId);
        inventory.AddItem(matchingId, botPlayerId, inputItemId);

        var mergedItemIds = BotConsumableLoadout.MergeAvailable(
            inventory, matchingId, botPlayerId);

        Assert.Equal(new[] { outputItemId }, mergedItemIds);
        Assert.Equal(0, inventory.GetPlayerInventory(matchingId, botPlayerId).GetItemCount(inputItemId));
        Assert.Equal(1, inventory.GetPlayerInventory(matchingId, botPlayerId).GetItemCount(outputItemId));
        Assert.Null(inventory.GetEquippedBattleItem(matchingId, botPlayerId));
    }

    [Fact]
    public void UnarmedBotWaitsInsteadOfEnteringLegacyFarmingRoute()
    {
        const long matchingId = 194200;
        const long botPlayerId = -1942001;
        var fixture = CreateFixture(matchingId, botPlayerId, AreaType.Classroom3);
        var bot = fixture.BotManager.GetBot(matchingId, botPlayerId)!;
        SetBotPosition(bot, AreaType.Ground);
        bot.EquippedBattleItemId = 0;
        bot.Path.Clear();
        bot.PathIndex = 0;
        bot.LoopWaitUntil = DateTime.MinValue;

        fixture.BotManager.ProcessBotMovementTick(
            matchingId,
            fixture.ClosureManager,
            fixture.AreaStockManager,
            new Dictionary<long, AreaType>(),
            fixture.ChecklistManager,
            fixture.InventoryManager,
            fixture.GroundItemManager,
            Array.Empty<BotCombatTargetSnapshot>());

        Assert.Empty(bot.Path);
        Assert.Equal(AreaType.None, bot.MovementDestination);
        Assert.True(bot.LoopWaitUntil > DateTime.UtcNow);
    }

    [Fact]
    public void BotKitesWithinItsRoomWhenNearbyAfterimagesCloseIn()
    {
        const long matchingId = 1942000;
        const long botPlayerId = -19420001;
        var fixture = CreateFixture(matchingId, botPlayerId, AreaType.Classroom3);
        var bot = fixture.BotManager.GetBot(matchingId, botPlayerId)!;
        bot.EquippedBattleItemId = RecorderT1;
        bot.Path.Clear();
        bot.PathIndex = 0;
        bot.LoopWaitUntil = DateTime.MinValue;
        bot.NextPveKiteRepathAt = DateTime.MinValue;

        var threat = new MonsterCombatTarget(
            202001,
            MapId.School,
            AreaType.Classroom3,
            new Vector3f(bot.Position.X + 1f, bot.Position.Y, 0f),
            107000010);
        var result = fixture.BotManager.ProcessBotMovementTick(
            matchingId,
            fixture.ClosureManager,
            fixture.AreaStockManager,
            new Dictionary<long, AreaType>(),
            fixture.ChecklistManager,
            fixture.InventoryManager,
            fixture.GroundItemManager,
            Array.Empty<BotCombatTargetSnapshot>(),
            [threat]);

        Assert.Contains(result.Movements, movement => movement.BotPlayerId == botPlayerId);
        Assert.Equal(AreaType.None, bot.MovementDestination);
        Assert.True(bot.NextPveKiteRepathAt > DateTime.UtcNow);
        Assert.All(bot.Path.Skip(bot.PathIndex), step => Assert.Equal(AreaType.Classroom3, step.Area));
    }
    [Fact]
    public void BotPveRouteUsesBoardAffinityBeforeLegacyWander()
    {
        const long matchingId = 1942005;
        const long botPlayerId = -19420051;
        var fixture = CreateFixture(matchingId, botPlayerId, AreaType.Classroom3);
        var bot = fixture.BotManager.GetBot(matchingId, botPlayerId)!;
        SetBotPosition(bot, AreaType.Classroom3);
        bot.EquippedBattleItemId = 107000010;
        bot.Path.Clear();
        bot.PathIndex = 0;
        bot.LoopWaitUntil = DateTime.MinValue;
        fixture.InventoryManager.AddItem(matchingId, botPlayerId, 107000010);

        var favorableCell = GameMapData.GetAreaSpawnCell(MapId.School, AreaType.Classroom4);
        var unfavorableCell = GameMapData.GetAreaSpawnCell(MapId.School, AreaType.ExamRoom);
        var targets = new[]
        {
            new MonsterCombatTarget(
                202001, MapId.School, AreaType.Classroom4,
                new Vector3f((favorableCell.X - favorableCell.Y) / 2f, (favorableCell.X + favorableCell.Y) / 4f, 0f),
                107000020, IsCore: true),
            new MonsterCombatTarget(
                202002, MapId.School, AreaType.ExamRoom,
                new Vector3f((unfavorableCell.X - unfavorableCell.Y) / 2f, (unfavorableCell.X + unfavorableCell.Y) / 4f, 0f),
                107000030, IsCore: true)
        };

        fixture.BotManager.ProcessBotMovementTick(
            matchingId,
            fixture.ClosureManager,
            fixture.AreaStockManager,
            new Dictionary<long, AreaType>(),
            fixture.ChecklistManager,
            fixture.InventoryManager,
            fixture.GroundItemManager,
            Array.Empty<BotCombatTargetSnapshot>(),
            targets);

        Assert.Equal(AreaType.Classroom4, bot.MovementDestination);
        Assert.NotEmpty(bot.Path);
        Assert.Equal(AreaType.Classroom4, bot.Path[^1].Area);
    }
    [Fact]
    public void BotFollowingTargetInCorridorSelectsRoomDestination()
    {
        const long matchingId = 1942001;
        const long botPlayerId = -19420011;
        var fixture = CreateFixture(matchingId, botPlayerId, AreaType.Classroom3);
        var bot = fixture.BotManager.GetBot(matchingId, botPlayerId)!;
        bot.EquippedBattleItemId = RecorderT1;
        bot.Path.Clear();
        bot.PathIndex = 0;
        bot.LoopWaitUntil = DateTime.MinValue;

        fixture.BotManager.ProcessBotMovementTick(
            matchingId,
            fixture.ClosureManager,
            fixture.AreaStockManager,
            new Dictionary<long, AreaType> { [bot.TargetPlayerId] = AreaType.Corridor },
            fixture.ChecklistManager,
            fixture.InventoryManager,
            fixture.GroundItemManager,
            Array.Empty<BotCombatTargetSnapshot>());

        Assert.NotEqual(AreaType.None, bot.MovementDestination);
        Assert.False(bot.MovementDestination.IsCorridor());
        Assert.NotEmpty(bot.Path);
        Assert.False(bot.Path[^1].Area.IsCorridor());
    }
    [Fact]
    public void BotMergesStoredRecoveryItemsAndUsesThemOnlyAtThreshold()
    {
        const long matchingId = 194201;
        const long botPlayerId = -1942011;
        var fixture = CreateFixture(matchingId, botPlayerId, AreaType.Classroom3);
        var bot = fixture.BotManager.GetBot(matchingId, botPlayerId)!;
        var missionManager = new MissionManager(NullLogger.Instance);
        var itemPoolManager = new ItemPoolManager();
        itemPoolManager.Initialize();
        fixture.InventoryManager.AddItem(matchingId, botPlayerId, CannedCoffee);
        fixture.InventoryManager.AddItem(matchingId, botPlayerId, CannedCoffee);
        bot.Stamina = 80;
        bot.Corruption = 0;

        BackdateMissionTick(bot);
        var healthyTick = fixture.BotManager.ProcessBotMissionTick(
            matchingId,
            missionManager,
            fixture.InventoryManager,
            itemPoolManager,
            fixture.AreaStockManager,
            fixture.GroundItemManager,
            fixture.ChecklistManager);

        Assert.Contains(healthyTick.ConsumableMerges,
            entry => entry.botPlayerId == botPlayerId && entry.itemId == DoubleShotCoffee);
        Assert.Equal(1, fixture.InventoryManager.GetPlayerInventory(matchingId, botPlayerId)
            .GetItemCount(DoubleShotCoffee));
        Assert.Equal(80, bot.Stamina);
        Assert.Equal(DateTime.MinValue, bot.LastAutoConsumableUseTime);

        bot.Stamina = 10;
        BackdateMissionTick(bot);
        fixture.BotManager.ProcessBotMissionTick(
            matchingId,
            missionManager,
            fixture.InventoryManager,
            itemPoolManager,
            fixture.AreaStockManager,
            fixture.GroundItemManager,
            fixture.ChecklistManager);

        Assert.Equal(40, bot.Stamina);
        Assert.Equal(0, fixture.InventoryManager.GetPlayerInventory(matchingId, botPlayerId)
            .GetItemCount(DoubleShotCoffee));
        Assert.NotEqual(DateTime.MinValue, bot.LastAutoConsumableUseTime);
    }

    /// <summary>
    ///     2026-08-02 match-2063 회귀: 병합이 한 번도 없던 봇은 인벤토리에 오브가 장착돼 있어도
    ///     봇 상태 필드가 0으로 남았다. 이동 계획이 이를 시작 오브 미보유로 오판해 목적지를
    ///     고르지 못했고, 봇 3마리가 같은 방에 굳은 채 전원 탈락했다.
    /// </summary>
    [Fact]
    public void BotSyncsEquippedBattleItemIdWhenTheEquippedOrbDoesNotChange()
    {
        const long matchingId = 194211;
        const long botPlayerId = -1942111;
        var fixture = CreateFixture(matchingId, botPlayerId, AreaType.Classroom3);
        var bot = fixture.BotManager.GetBot(matchingId, botPlayerId)!;
        var missionManager = new MissionManager(NullLogger.Instance);
        var itemPoolManager = new ItemPoolManager();
        itemPoolManager.Initialize();
        bot.Stamina = 80;

        // 시작 오브 지급 경로 모사 — 인벤토리에는 장착됐지만 봇 상태 필드는 아직 비어 있다.
        // 오브가 1개뿐이라 병합이 없고, 따라서 장착 아이템도 끝까지 바뀌지 않는다.
        var startingOrb = fixture.InventoryManager.AddItem(matchingId, botPlayerId, RecorderT1);
        Assert.True(fixture.InventoryManager.TryEquipBattleItem(
            matchingId, botPlayerId, startingOrb.ItemUid, out _));
        bot.EquippedBattleItemId = 0;

        BackdateMissionTick(bot);
        fixture.BotManager.ProcessBotMissionTick(
            matchingId,
            missionManager,
            fixture.InventoryManager,
            itemPoolManager,
            fixture.AreaStockManager,
            fixture.GroundItemManager,
            fixture.ChecklistManager);

        Assert.Equal(RecorderT1, bot.EquippedBattleItemId);

        // 교체가 없는 후속 틱에서도 동기화가 풀리면 안 된다.
        BackdateMissionTick(bot);
        fixture.BotManager.ProcessBotMissionTick(
            matchingId,
            missionManager,
            fixture.InventoryManager,
            itemPoolManager,
            fixture.AreaStockManager,
            fixture.GroundItemManager,
            fixture.ChecklistManager);

        Assert.Equal(RecorderT1, bot.EquippedBattleItemId);
    }

    [Fact]
    public void FarmingToT2CombatEliminationAndDropRemainsContinuous()
    {
        const long matchingId = 194202;
        const long attackerId = -1942021;
        const long victimId = -1942022;
        var botManager = new BotPlayerManager(NullLogger.Instance);
        botManager.RegisterBots(matchingId, MapId.School,
        [
            BotInfo(attackerId, victimId, AreaType.Classroom3),
            BotInfo(victimId, attackerId, AreaType.Classroom3)
        ]);
        var attacker = botManager.GetBot(matchingId, attackerId)!;
        var victim = botManager.GetBot(matchingId, victimId)!;
        SetBotPosition(attacker, AreaType.Classroom3);
        victim.CurrentArea = AreaType.Classroom3;
        victim.Cell = attacker.Cell;
        victim.Position = new Vector3f(attacker.Position.X + 1f, attacker.Position.Y, 0f);

        var inventory = new InGameInventoryManager();
        inventory.Initialize();
        var ground = new GroundItemManager();
        ground.InitializeMatching(matchingId);
        ground.SpawnItems(
            matchingId,
            AreaType.Classroom3,
            attacker.Position.X,
            attacker.Position.Y,
            [RecorderT1, RecorderT1],
            discovererPlayerId: attackerId,
            discovererPickupWindow: GroundItemManager.DiscovererPickupWindow);

        for (int pickupIndex = 0; pickupIndex < 2; pickupIndex++)
        {
            Assert.True(botManager.TryAutoPickupGroundItem(
                attacker, matchingId, inventory, ground, out var pickup));
            Assert.NotNull(pickup);
            Assert.Equal(RecorderT1, pickup.Value.Item.ItemId);
        }

        var loadout = BotBattleItemLoadout.CombineAndEquip(
            inventory, matchingId, attackerId, new Random(194202));
        Assert.Equal(RecorderT2, loadout.EquippedItemId);
        attacker.EquippedBattleItemId = loadout.EquippedItemId;
        inventory.AddItem(matchingId, victimId, CannedCoffee);

        var eventLog = new GameEventLogManager();
        var start = new DateTime(2026, 7, 19, 1, 0, 0, DateTimeKind.Utc);
        eventLog.LogSurvivorTierReached(
            matchingId, attackerId, RecorderT2, 2, isBot: true, new DateTimeOffset(start));
        var resolver = new ProximityAutoCombatResolver();
        var actors = new[]
        {
            CombatActor(attackerId, attacker.Position, RecorderT2),
            VisibleUnarmedActor(victimId, victim.Position, weaponItemId: 0)
        };

        void OnAcquired(ProximityCombatTargetEvent targetEvent) => eventLog.LogSurvivorTargetAcquired(
            matchingId,
            targetEvent.AttackerPlayerId,
            targetEvent.TargetPlayerId,
            targetEvent.Area.ToString(),
            targetEvent.WeaponItemId,
            targetEvent.TargetWeaponItemId,
            isBot: true,
            targetEvent.OccurredAtUtc);
        void OnLost(ProximityCombatTargetEvent targetEvent) => eventLog.LogSurvivorTargetLost(
            matchingId,
            targetEvent.AttackerPlayerId,
            targetEvent.TargetPlayerId,
            targetEvent.Reason,
            isBot: true,
            targetEvent.OccurredAtUtc);

        Assert.Empty(resolver.Resolve(
            matchingId, actors, start, onTargetAcquired: OnAcquired, onTargetLost: OnLost));
        Assert.Empty(resolver.Resolve(
            matchingId, actors, start.AddMilliseconds(ProximityAutoCombatResolver.AimDuration.TotalMilliseconds - 1),
            onTargetAcquired: OnAcquired, onTargetLost: OnLost));

        DateTime attackAt = start.Add(ProximityAutoCombatResolver.AimDuration);
        while (victim.Corruption < Config.SURVIVOR_MAX_CORRUPTION)
        {
            var attack = Assert.Single(resolver.Resolve(
                matchingId, actors, attackAt, onTargetAcquired: OnAcquired, onTargetLost: OnLost));
            bool isLethal = victim.Corruption < Config.SURVIVOR_MAX_CORRUPTION && victim.Corruption + attack.Damage >= Config.SURVIVOR_MAX_CORRUPTION;
            eventLog.LogSurvivorHit(
                matchingId,
                attack.AttackerPlayerId,
                attack.TargetPlayerId,
                attack.WeaponItemId,
                attack.Damage,
                isLethal,
                isBot: true,
                new DateTimeOffset(attackAt));
            botManager.ApplyProximityAutoCombatDamage(victim, attack.Damage);
            attackAt = attackAt.AddSeconds(BattleItemCombatData.Get(RecorderT2)!.AttackIntervalSeconds);
        }

        Assert.True(botManager.TryFinalizeProximityAutoCombatElimination(victim, matchingId));
        eventLog.LogElimination(
            matchingId, victimId, EliminationReason.MENTAL_ZERO.ToString(), isBot: true);
        var removed = inventory.TakeAllItems(matchingId, victimId);
        var droppedItemIds = removed
            .SelectMany(item => Enumerable.Repeat(item.ItemId, item.Count))
            .Where(GroundItemPickupPolicy.ShouldDropOnElimination)
            .ToList();
        var dropped = ground.SpawnItems(
            matchingId,
            victim.CurrentArea,
            victim.Position.X,
            victim.Position.Y,
            droppedItemIds);

        Assert.DoesNotContain(dropped, item => item.ItemId == CannedCoffee);
        var events = eventLog.GetRecent(matchingId);
        Assert.Single(events, entry => entry.Type == "SURVIVOR_FIRST_T2");
        Assert.Single(events, entry => entry.Type == "SURVIVOR_ENCOUNTER_START");
        Assert.Contains(events, entry => entry.Type == "SURVIVOR_HIT");
        var elimination = Assert.Single(events, entry => entry.Type == "SURVIVOR_COMBAT_ELIMINATION");
        Assert.Equal(attackerId, elimination.PlayerId);
        Assert.Equal(victimId, elimination.TargetPlayerId);
        Assert.Equal(1, elimination.KillCount);
        Assert.True(elimination.IsFirstMilestone);
        Assert.Single(events, entry => entry.Type == "SURVIVOR_FIRST_ELIMINATION");
        Assert.Contains(events, entry => entry.Type == "ELIMINATE" && entry.PlayerId == victimId);
    }

    [Fact]
    public void DoorwayAndTierGapTelemetryRecordsResponseWindowAndEscape()
    {
        const long matchingId = 194203;
        const long attackerId = -1942031;
        const long victimId = -1942032;
        var resolver = new ProximityAutoCombatResolver();
        var eventLog = new GameEventLogManager();
        var start = new DateTime(2026, 7, 19, 2, 0, 0, DateTimeKind.Utc);
        var actors = new List<ProximityCombatActor>
        {
            CombatActor(attackerId, new Vector3f(0f, 0f, 0f), RecorderT3),
            VisibleUnarmedActor(victimId, new Vector3f(1f, 0f, 0f), RecorderT1)
        };

        void OnAcquired(ProximityCombatTargetEvent targetEvent) => eventLog.LogSurvivorTargetAcquired(
            matchingId,
            targetEvent.AttackerPlayerId,
            targetEvent.TargetPlayerId,
            targetEvent.Area.ToString(),
            targetEvent.WeaponItemId,
            targetEvent.TargetWeaponItemId,
            isBot: true,
            targetEvent.OccurredAtUtc);
        void OnLost(ProximityCombatTargetEvent targetEvent) => eventLog.LogSurvivorTargetLost(
            matchingId,
            targetEvent.AttackerPlayerId,
            targetEvent.TargetPlayerId,
            targetEvent.Reason,
            isBot: true,
            targetEvent.OccurredAtUtc);

        Assert.Empty(resolver.Resolve(
            matchingId, actors, start, onTargetAcquired: OnAcquired, onTargetLost: OnLost));
        var firstAttackAt = start.Add(ProximityAutoCombatResolver.AimDuration);
        var firstAttack = Assert.Single(resolver.Resolve(
            matchingId, actors, firstAttackAt, onTargetAcquired: OnAcquired, onTargetLost: OnLost));
        eventLog.LogSurvivorHit(
            matchingId, attackerId, victimId, RecorderT3, firstAttack.Damage, false, true,
            new DateTimeOffset(firstAttackAt));

        var secondAttackAt = firstAttackAt.AddSeconds(1.15);
        var secondAttack = Assert.Single(resolver.Resolve(
            matchingId, actors, secondAttackAt, onTargetAcquired: OnAcquired, onTargetLost: OnLost));
        eventLog.LogSurvivorHit(
            matchingId, attackerId, victimId, RecorderT3, secondAttack.Damage, false, true,
            new DateTimeOffset(secondAttackAt));

        actors[1] = actors[1] with { Position = new Vector3f(30f, 0f, 0f) };
        Assert.Empty(resolver.Resolve(
            matchingId, actors, secondAttackAt.AddMilliseconds(50), onTargetAcquired: OnAcquired, onTargetLost: OnLost));

        var events = eventLog.GetRecent(matchingId)
            .Where(entry => entry.PlayerId == attackerId)
            .OrderBy(entry => entry.TimestampUnixMs)
            .ToList();
        var encounter = Assert.Single(events, entry => entry.Type == "SURVIVOR_ENCOUNTER_START");
        Assert.Equal(3, encounter.WeaponTier);
        Assert.Equal(1, encounter.TargetWeaponTier);
        Assert.True(encounter.IsFirstMilestone);

        var hits = events.Where(entry => entry.Type == "SURVIVOR_HIT").ToList();
        Assert.Equal(2, hits.Count);
        int aimMs = (int)ProximityAutoCombatResolver.AimDuration.TotalMilliseconds;
        Assert.Equal(aimMs, hits[0].ElapsedMilliseconds);
        Assert.Null(hits[0].PreviousHitGapMilliseconds);
        Assert.InRange(hits[1].ElapsedMilliseconds!.Value, aimMs + 1149, aimMs + 1150);
        Assert.InRange(hits[1].PreviousHitGapMilliseconds!.Value, 1149, 1150);

        var escaped = Assert.Single(events, entry => entry.Type == "SURVIVOR_ENCOUNTER_END");
        Assert.True(escaped.Escaped);
        Assert.Equal("out_of_range_or_los", escaped.Outcome);
        Assert.InRange(escaped.ElapsedMilliseconds!.Value, aimMs + 1199, aimMs + 1200);
        Assert.Equal(2, escaped.HitCount);
    }

    [Fact]
    public void BotT1ToT3CombineAndEquipIsStableAcrossRepeatedRuns()
    {
        for (int iteration = 0; iteration < 32; iteration++)
        {
            long matchingId = 194300 + iteration;
            long botPlayerId = -1943000 - iteration;
            var inventory = new InGameInventoryManager();
            inventory.Initialize();
            for (int itemIndex = 0; itemIndex < 4; itemIndex++)
                inventory.AddItem(matchingId, botPlayerId, RecorderT1);

            var result = BotBattleItemLoadout.CombineAndEquip(
                inventory, matchingId, botPlayerId, new Random(iteration));

            Assert.Equal(3, result.CombinedItemIds.Count);
            Assert.Equal(3, BattleItemCombatData.Get(result.EquippedItemId)!.Tier);
            Assert.Equal(result.EquippedItemId,
                inventory.GetEquippedBattleItem(matchingId, botPlayerId)!.ItemId);
            Assert.Equal(0, inventory.GetPlayerInventory(matchingId, botPlayerId)
                .GetItemCount(RecorderT1));
        }
    }

    [Fact]
    public void FirstTierMilestonesAreLoggedOncePerMatch()
    {
        const long matchingId = 194204;
        var eventLog = new GameEventLogManager();
        var start = new DateTimeOffset(2026, 7, 19, 3, 0, 0, TimeSpan.Zero);

        eventLog.LogSurvivorTierReached(matchingId, -1, RecorderT2, 2, true, start);
        eventLog.LogSurvivorTierReached(matchingId, -2, RecorderT2, 2, true, start.AddSeconds(1));
        eventLog.LogSurvivorTierReached(matchingId, -3, RecorderT3, 3, true, start.AddSeconds(2));
        eventLog.LogSurvivorTierReached(matchingId, -4, RecorderT3, 3, true, start.AddSeconds(3));

        var events = eventLog.GetRecent(matchingId);
        Assert.Single(events, entry => entry.Type == "SURVIVOR_FIRST_T2");
        Assert.Single(events, entry => entry.Type == "SURVIVOR_FIRST_T3");
    }


    [Fact]
    public void ClosureWarningInterruptsLootAndRoutesBotToAnOpenRoom()
    {
        const long matchingId = 194205;
        const long botPlayerId = -1942051;
        var now = new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc);
        var botManager = new BotPlayerManager(NullLogger.Instance);
        botManager.RegisterBots(matchingId, MapId.School, [BotInfo(botPlayerId, botPlayerId - 1, AreaType.Classroom3)]);
        var bot = botManager.GetBot(matchingId, botPlayerId)!;
        SetBotPosition(bot, AreaType.Classroom3);
        bot.IsInInteraction = true;
        bot.InteractionStayUntil = DateTime.UtcNow.AddSeconds(10);
        bot.PendingRngInteractId = 1234;
        bot.LastWalkStepTime = DateTime.UtcNow.AddSeconds(-1);

        var closure = new AreaClosureManager(
            NullLogger.Instance,
            new MatchingConfigService(null!, NullLogger.Instance),
            () => now);
        closure.InitializeMatching(matchingId);
        now = now.AddSeconds(150); // Classroom3 closes in the second 15-second warning window.
        closure.CheckClosureSchedule(matchingId);
        Assert.Contains(AreaType.Classroom3, closure.GetClientStateSnapshot(matchingId).WarningAreas);

        var stock = new AreaItemStockManager(new ZeroRandom());
        stock.InitializeMatching(matchingId);
        var inventory = new InGameInventoryManager();
        inventory.Initialize();
        var ground = new GroundItemManager();
        ground.InitializeMatching(matchingId);
        ground.SpawnItems(matchingId, AreaType.Classroom3, bot.Position.X, bot.Position.Y, [CannedCoffee]);
        var checklist = new ChecklistManager(NullLogger.Instance);
        checklist.Initialize();

        var tick = botManager.ProcessBotMovementTick(
            matchingId,
            closure,
            stock,
            new Dictionary<long, AreaType>(),
            checklist,
            inventory,
            ground,
            Array.Empty<BotCombatTargetSnapshot>());

        Assert.False(bot.IsInInteraction);
        Assert.Equal(0, bot.PendingRngInteractId);
        Assert.NotEqual(AreaType.None, bot.EvacuationDestination);
        Assert.NotEqual(AreaType.Classroom3, bot.EvacuationDestination);
        Assert.False(bot.EvacuationDestination.IsCorridor());
        Assert.NotEmpty(bot.Path);
        Assert.Equal(bot.EvacuationDestination, bot.Path[^1].Area);
        Assert.Empty(tick.GroundItemPickups);
    }

    [Fact]
    public void FinalClosureWithoutAnyRefuge_KeepsBotMovingInLastStand()
    {
        const long matchingId = 1942052;
        const long botPlayerId = -1942052;
        var fixture = CreateFixture(matchingId, botPlayerId, AreaType.Classroom3);
        var bot = fixture.BotManager.GetBot(matchingId, botPlayerId)!;
        SetBotPosition(bot, AreaType.Classroom3);
        bot.Path.Clear();
        bot.PathIndex = 0;
        bot.LoopWaitUntil = DateTime.MinValue;
        bot.LastWalkStepTime = DateTime.UtcNow.AddSeconds(-1);

        var closureState = fixture.ClosureManager.InitializeMatching(matchingId);
        foreach (AreaType area in GameMapData.GetAreas(MapId.School)
                     .Select(region => region.AreaType)
                     .Distinct()
                     .Where(area => area != AreaType.None && !area.IsCorridor()))
        {
            closureState.ClosedAreas.Add(area);
        }

        var tick = fixture.BotManager.ProcessBotMovementTick(
            matchingId,
            fixture.ClosureManager,
            fixture.AreaStockManager,
            new Dictionary<long, AreaType>(),
            fixture.ChecklistManager,
            fixture.InventoryManager,
            fixture.GroundItemManager,
            Array.Empty<BotCombatTargetSnapshot>());

        Assert.Equal(AreaType.None, bot.EvacuationDestination);
        Assert.Equal(DateTime.MinValue, bot.LoopWaitUntil);
        Assert.NotEmpty(bot.Path);
        Assert.Contains(tick.Movements, movement => movement.BotPlayerId == botPlayerId);
    }
    [Fact]
    public void StrongerNearbyOpponentMakesBotLeaveTheRoomInsteadOfKitingInPlace()
    {
        const long matchingId = 194206;
        const long botPlayerId = -1942061;
        const long enemyPlayerId = -1942062;
        var fixture = CreateFixture(matchingId, botPlayerId, AreaType.Classroom3);
        var bot = fixture.BotManager.GetBot(matchingId, botPlayerId)!;
        SetBotPosition(bot, AreaType.Classroom3);
        bot.Path.Clear();
        bot.PathIndex = 0;

        fixture.BotManager.UpdateCombatMovementIntent(
            bot,
            matchingId,
            fixture.ClosureManager,
            [
                new BotCombatTargetSnapshot(botPlayerId, AreaType.Classroom3, bot.Position, RecorderT1),
                new BotCombatTargetSnapshot(
                    enemyPlayerId,
                    AreaType.Classroom3,
                    new Vector3f(bot.Position.X + 0.5f, bot.Position.Y, 0f),
                    RecorderT3)
            ]);

        // 전투 반응 지연이 제거되어 후퇴 결정은 첫 판단 호출에서 즉시 경로로 이어진다.
        Assert.NotEmpty(bot.Path);
        Assert.NotEqual(AreaType.Classroom3, bot.Path[^1].Area);
        Assert.False(bot.Path[^1].Area.IsCorridor());
        Assert.Equal(bot.Path[^1].Area, bot.EvacuationDestination);

        var committedPath = bot.Path.ToArray();
        bot.NextCombatRepathAt = DateTime.MinValue;
        fixture.BotManager.UpdateCombatMovementIntent(
            bot,
            matchingId,
            fixture.ClosureManager,
            [
                new BotCombatTargetSnapshot(botPlayerId, AreaType.Classroom3, bot.Position, RecorderT1),
                new BotCombatTargetSnapshot(
                    enemyPlayerId,
                    AreaType.Classroom3,
                    new Vector3f(bot.Position.X + 0.5f, bot.Position.Y, 0f),
                    RecorderT3)
            ]);

        Assert.Equal(committedPath, bot.Path);
    }

    [Fact]
    public void CommittedLootRouteIsNotOverwrittenByAHealthyNearbyOpponent()
    {
        const long matchingId = 194207;
        const long botPlayerId = -1942071;
        const long enemyPlayerId = -1942072;
        var fixture = CreateFixture(matchingId, botPlayerId, AreaType.Classroom3);
        var bot = fixture.BotManager.GetBot(matchingId, botPlayerId)!;
        SetBotPosition(bot, AreaType.Classroom3);
        bot.MovementDestination = AreaType.Classroom4;
        bot.Path = BotPathfinder.FindPath(
            MapId.School,
            bot.CurrentArea,
            bot.Cell,
            bot.MovementDestination,
            GameMapData.GetAreaSpawnCell(MapId.School, bot.MovementDestination))!;
        var committedPath = bot.Path.ToArray();

        fixture.BotManager.UpdateCombatMovementIntent(
            bot,
            matchingId,
            fixture.ClosureManager,
            [
                new BotCombatTargetSnapshot(botPlayerId, AreaType.Classroom3, bot.Position, RecorderT1),
                new BotCombatTargetSnapshot(
                    enemyPlayerId,
                    AreaType.Classroom3,
                    new Vector3f(bot.Position.X + 0.5f, bot.Position.Y, 0f),
                    RecorderT1)
            ]);

        Assert.Equal(AreaType.Classroom4, bot.MovementDestination);
        Assert.Equal(committedPath, bot.Path);
    }

    [Fact]
    public void LowHealthBotAbandonsLootRouteToEscapeNearbyOpponent()
    {
        const long matchingId = 194208;
        const long botPlayerId = -1942081;
        const long enemyPlayerId = -1942082;
        var fixture = CreateFixture(matchingId, botPlayerId, AreaType.Classroom3);
        var bot = fixture.BotManager.GetBot(matchingId, botPlayerId)!;
        SetBotPosition(bot, AreaType.Classroom3);
        bot.Corruption = (int)(Config.SURVIVOR_MAX_CORRUPTION * 0.8f);
        bot.MovementDestination = AreaType.Classroom4;
        bot.Path = BotPathfinder.FindPath(
            MapId.School,
            bot.CurrentArea,
            bot.Cell,
            bot.MovementDestination,
            GameMapData.GetAreaSpawnCell(MapId.School, bot.MovementDestination))!;

        fixture.BotManager.UpdateCombatMovementIntent(
            bot,
            matchingId,
            fixture.ClosureManager,
            [
                new BotCombatTargetSnapshot(botPlayerId, AreaType.Classroom3, bot.Position, RecorderT1, bot.Corruption),
                new BotCombatTargetSnapshot(
                    enemyPlayerId,
                    AreaType.Classroom3,
                    new Vector3f(bot.Position.X + 0.5f, bot.Position.Y, 0f),
                    RecorderT1)
            ]);

        // 전투 반응 지연이 제거되어 저체력 후퇴는 첫 판단 호출에서 즉시 루트를 버린다.
        Assert.Equal(AreaType.None, bot.MovementDestination);
        Assert.NotEqual(AreaType.None, bot.EvacuationDestination);
        Assert.NotEqual(AreaType.Classroom3, bot.EvacuationDestination);
        Assert.Equal(AreaType.Classroom3, bot.RecentCombatRetreatOrigin);
        Assert.True(bot.CombatRetreatOriginBlockedUntil > DateTime.UtcNow);
    }

    [Fact]
    public void CombatRetreatDoesNotImmediatelyRouteBackToThreatRoomForLoot()
    {
        const long matchingId = 194209;
        const long botPlayerId = -1942091;
        const long enemyPlayerId = -1942092;
        var fixture = CreateFixture(matchingId, botPlayerId, AreaType.Classroom3);
        var bot = fixture.BotManager.GetBot(matchingId, botPlayerId)!;
        SetBotPosition(bot, AreaType.Classroom3);
        bot.Corruption = (int)(Config.SURVIVOR_MAX_CORRUPTION * 0.8f);
        bot.EquippedBattleItemId = 107000020;

        fixture.BotManager.UpdateCombatMovementIntent(
            bot,
            matchingId,
            fixture.ClosureManager,
            [
                new BotCombatTargetSnapshot(botPlayerId, AreaType.Classroom3, bot.Position, 107000020, bot.Corruption),
                new BotCombatTargetSnapshot(
                    enemyPlayerId,
                    AreaType.Classroom3,
                    new Vector3f(bot.Position.X + 0.5f, bot.Position.Y, 0f),
                    RecorderT3)
            ]);

        // 전투 반응 지연이 제거되어 후퇴는 첫 판단 호출에서 즉시 확정된다.
        AreaType escapeArea = bot.EvacuationDestination;
        Assert.NotEqual(AreaType.None, escapeArea);
        SetBotPosition(bot, escapeArea);
        bot.Path.Clear();
        bot.PathIndex = 0;
        bot.LoopWaitUntil = DateTime.MinValue;

        fixture.BotManager.ProcessBotMovementTick(
            matchingId,
            fixture.ClosureManager,
            fixture.AreaStockManager,
            new Dictionary<long, AreaType>(),
            fixture.ChecklistManager,
            fixture.InventoryManager,
            fixture.GroundItemManager,
            Array.Empty<BotCombatTargetSnapshot>());

        Assert.NotEqual(AreaType.Classroom3, bot.MovementDestination);
    }

    /// <summary>
    ///     클리어 전 방은 문이 잠긴다. 사람은 이동 검증이 막지만 봇은 서버가 직접 걷게
    ///     하므로, 잠긴 방이 봇의 목적지·사냥 후보에서 빠지는지 경로 결정 수준에서 검증한다.
    /// </summary>
    [Fact]
    public void BotsDoNotRouteIntoLockedRooms()
    {
        const long matchingId = 194212;
        const long botPlayerId = -1942121;
        var fixture = CreateFixture(matchingId, botPlayerId, AreaType.Classroom3);
        var bot = fixture.BotManager.GetBot(matchingId, botPlayerId)!;
        SetBotPosition(bot, AreaType.Corridor);
        bot.EquippedBattleItemId = RecorderT1;
        bot.Path.Clear();
        bot.PathIndex = 0;
        bot.LoopWaitUntil = DateTime.MinValue;

        // Classroom4는 클리어 전(잠김), ExamRoom은 클리어됨(열림)으로 설정한다.
        fixture.BotManager.SetLockedRoomAreasProvider(_ => [AreaType.Classroom4]);

        var favorableCell = GameMapData.GetAreaSpawnCell(MapId.School, AreaType.Classroom4);
        var openCell = GameMapData.GetAreaSpawnCell(MapId.School, AreaType.ExamRoom);
        var targets = new[]
        {
            // 잠긴 방 쪽이 사냥 점수는 더 높다 (핵 존재). 그래도 골라선 안 된다.
            new MonsterCombatTarget(
                202001, MapId.School, AreaType.Classroom4,
                new Vector3f((favorableCell.X - favorableCell.Y) / 2f, (favorableCell.X + favorableCell.Y) / 4f, 0f),
                107000010, IsCore: true),
            new MonsterCombatTarget(
                202002, MapId.School, AreaType.ExamRoom,
                new Vector3f((openCell.X - openCell.Y) / 2f, (openCell.X + openCell.Y) / 4f, 0f),
                107000030)
        };

        fixture.BotManager.ProcessBotMovementTick(
            matchingId,
            fixture.ClosureManager,
            fixture.AreaStockManager,
            new Dictionary<long, AreaType>(),
            fixture.ChecklistManager,
            fixture.InventoryManager,
            fixture.GroundItemManager,
            Array.Empty<BotCombatTargetSnapshot>(),
            targets);

        Assert.NotEqual(AreaType.Classroom4, bot.MovementDestination);
        Assert.All(bot.Path, step => Assert.NotEqual(AreaType.Classroom4, step.Area));
    }

    /// <summary>자기 방이 잠겨 있으면(클리어 전) 봇도 방 밖으로 나가는 경로를 만들지 않는다.</summary>
    [Fact]
    public void BotInsideLockedRoomStaysUntilCleared()
    {
        const long matchingId = 194213;
        const long botPlayerId = -1942131;
        var fixture = CreateFixture(matchingId, botPlayerId, AreaType.Classroom3);
        var bot = fixture.BotManager.GetBot(matchingId, botPlayerId)!;
        SetBotPosition(bot, AreaType.Classroom3);
        bot.EquippedBattleItemId = RecorderT1;
        bot.Path.Clear();
        bot.PathIndex = 0;
        bot.LoopWaitUntil = DateTime.MinValue;

        fixture.BotManager.SetLockedRoomAreasProvider(_ => [AreaType.Classroom3]);

        var outsideCell = GameMapData.GetAreaSpawnCell(MapId.School, AreaType.ExamRoom);
        var targets = new[]
        {
            new MonsterCombatTarget(
                202001, MapId.School, AreaType.ExamRoom,
                new Vector3f((outsideCell.X - outsideCell.Y) / 2f, (outsideCell.X + outsideCell.Y) / 4f, 0f),
                107000030, IsCore: true)
        };

        fixture.BotManager.ProcessBotMovementTick(
            matchingId,
            fixture.ClosureManager,
            fixture.AreaStockManager,
            new Dictionary<long, AreaType>(),
            fixture.ChecklistManager,
            fixture.InventoryManager,
            fixture.GroundItemManager,
            Array.Empty<BotCombatTargetSnapshot>(),
            targets);

        Assert.All(bot.Path.Skip(bot.PathIndex), step => Assert.Equal(AreaType.Classroom3, step.Area));
        Assert.NotEqual(AreaType.ExamRoom, bot.MovementDestination);
    }

    [Fact]
    public void OrbEquippedBotsDoNotUseLegacyLootRouting()
    {
        const long matchingId = 194210;
        long[] botIds = [-1942101, -1942102, -1942103];
        var botManager = new BotPlayerManager(NullLogger.Instance);
        botManager.RegisterBots(
            matchingId,
            MapId.School,
            botIds.Select(id => BotInfo(id, id - 10, AreaType.Classroom3)).ToList());

        foreach (long botId in botIds)
        {
            var bot = botManager.GetBot(matchingId, botId)!;
            SetBotPosition(bot, AreaType.Classroom3);
            bot.EquippedBattleItemId = 107000020;
            bot.Path.Clear();
            bot.PathIndex = 0;
            bot.LoopWaitUntil = DateTime.MinValue;
        }

        var closure = new AreaClosureManager(
            NullLogger.Instance,
            new MatchingConfigService(null!, NullLogger.Instance));
        closure.InitializeMatching(matchingId);
        var stock = new AreaItemStockManager(new ZeroRandom());
        stock.InitializeMatching(matchingId);
        var inventory = new InGameInventoryManager();
        inventory.Initialize();
        var ground = new GroundItemManager();
        ground.InitializeMatching(matchingId);
        var checklist = new ChecklistManager(NullLogger.Instance);
        checklist.Initialize();

        botManager.ProcessBotMovementTick(
            matchingId,
            closure,
            stock,
            new Dictionary<long, AreaType>(),
            checklist,
            inventory,
            ground,
            Array.Empty<BotCombatTargetSnapshot>());

        Assert.All(botIds, id => Assert.Equal(0, botManager.GetBot(matchingId, id)!.PendingRngInteractId));
    }

    [Theory(Skip = "#219 클론 맵 전환: 옛 학교 지형 전제 — 클론 데이터 스택(벽·연결·문) 완성 후 재작성")]
    [InlineData(AreaType.Gym, AreaType.Corridor, 173, 88, 172, 88, 170, 88)]
    [InlineData(AreaType.Corridor, AreaType.Gym, 172, 88, 173, 88, 175, 88)]
    public void BotAreaArrivalClearsDoorwayBeforeStopping(
        AreaType fromArea,
        AreaType toArea,
        int startX,
        int startY,
        int entryX,
        int entryY,
        int expectedX,
        int expectedY)
    {
        var entryCell = new Cell(entryX, entryY);

        var path = BotPathfinder.FindPath(
            MapId.School,
            fromArea,
            new Cell(startX, startY),
            toArea,
            entryCell);

        Assert.NotNull(path);
        Assert.Contains(path, step => step.IsAreaTransition && step.Cell.Equals(entryCell));
        Assert.Equal(toArea, path[^1].Area);
        Assert.False(path[^1].IsAreaTransition);
        Assert.Equal(new Cell(expectedX, expectedY), path[^1].Cell);
    }

    [Fact(Skip = "#219 클론 맵 전환: 옛 학교 지형 전제 — 클론 데이터 스택(벽·연결·문) 완성 후 재작성")]
    public void DefaultBotAreaArrivalsClearEverySchoolDoorway()
    {
        foreach (var fromArea in Enum.GetValues<AreaType>())
        {
            foreach (var toArea in GameAreaConnectionData.GetConnections(MapId.School, fromArea)
                         .Where(connection =>
                             !BotPathfinder.IsConnectionStaticallyLocked(MapId.School, connection))
                         .Select(connection => connection.ToArea)
                         .Distinct())
            {
                var entryCell = GameAreaConnectionData.GetSpawnCell(MapId.School, fromArea, toArea);
                Assert.NotNull(entryCell);

                var path = BotPathfinder.FindPath(
                    MapId.School,
                    fromArea,
                    GameMapData.GetAreaSpawnCell(MapId.School, fromArea),
                    toArea,
                    entryCell);

                Assert.NotNull(path);
                Assert.NotEmpty(path);
                Assert.NotEqual(entryCell, path[^1].Cell);
                Assert.Equal(toArea, path[^1].Area);
                Assert.Equal(toArea, GameMapData.GetCurrentArea(MapId.School, path[^1].Cell));
                Assert.True(GameMapData.IsMoveablePosition(MapId.School, path[^1].Cell));
            }
        }
    }

    private static ProximityCombatActor CombatActor(long playerId, Vector3f position, int weaponItemId)
    {
        var combatData = BattleItemCombatData.Get(weaponItemId)!;
        return new ProximityCombatActor(
            playerId,
            AreaType.Classroom3,
            position,
            weaponItemId,
            combatData.AttackRange,
            combatData.Damage,
            combatData.AttackIntervalSeconds,
            combatData.ProjectileWidth,
            combatData.EffectDurationSeconds);
    }

    private static ProximityCombatActor VisibleUnarmedActor(
        long playerId,
        Vector3f position,
        int weaponItemId)
    {
        return new ProximityCombatActor(
            playerId,
            AreaType.Classroom3,
            position,
            weaponItemId,
            0f,
            0,
            0f);
    }

    private static BotMatchingInfo BotInfo(long playerId, long targetPlayerId, AreaType startArea)
    {
        return new BotMatchingInfo
        {
            PlayerId = playerId,
            TargetPlayerId = targetPlayerId,
            MyJobTitle = JobTitle.SCIENCE_MEMBER,
            TargetJobTitle = JobTitle.HEALTH_MEMBER,
            StartArea = startArea
        };
    }

    private static BotFixture CreateFixture(long matchingId, long botPlayerId, AreaType startArea)
    {
        var botManager = new BotPlayerManager(NullLogger.Instance);
        botManager.RegisterBots(matchingId, MapId.School, [BotInfo(botPlayerId, botPlayerId - 1, startArea)]);
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

        return new BotFixture(
            botManager,
            closureManager,
            areaStockManager,
            inventoryManager,
            groundItemManager,
            checklistManager);
    }

    private static void SetBotPosition(BotPlayerState bot, AreaType area)
    {
        var cell = GameMapData.GetAreaSpawnCell(MapId.School, area);
        bot.CurrentArea = area;
        bot.Cell = cell;
        bot.Position = MapCoordinateConverter.CellToWorld(MapId.School, cell);
    }

    private static void BackdateMissionTick(BotPlayerState bot)
    {
        bot.LastMissionTickTime = DateTime.UtcNow.AddSeconds(-2);
    }

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

    private sealed class ZeroRandom : Random
    {
        public override int Next(int maxValue) => 0;
    }

    private sealed record BotFixture(
        BotPlayerManager BotManager,
        AreaClosureManager ClosureManager,
        AreaItemStockManager AreaStockManager,
        InGameInventoryManager InventoryManager,
        GroundItemManager GroundItemManager,
        ChecklistManager ChecklistManager);
}
