using game_server.services;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class OrbBoardTests
{
    public OrbBoardTests()
    {
        TestGameData.EnsureBattleItemCombatLoaded();
    }

    // 주기는 전 티어 0.8초 고정 (2026-08-24 #268): 티어 주기 단축은 오브 개수 증가와 겹치는
    // 이중 가속이라 퇴역 — 티어 값어치는 발당 피해(12/21/30)와 사거리만 진다. 기저 0.8초는
    // #229 상향값(시작 오브 하나로 구역 보충 초당 1.33마리를 열 수 있는 최소 박자) 그대로다.
    [Theory]
    [InlineData(107000010, 12, 0.8f)]
    [InlineData(107000011, 21, 0.8f)]
    [InlineData(107000012, 30, 0.8f)]
    [InlineData(107000020, 12, 0.8f)]
    [InlineData(107000021, 21, 0.8f)]
    [InlineData(107000022, 30, 0.8f)]
    public void SunAndWindUseTheSameTierAttackTable(int itemId, int damage, float interval)
    {
        Assert.Equal(damage, OrbData.GetSwarmPveAttackDamage(itemId));
        Assert.Equal(interval, OrbData.GetSwarmPveAttackIntervalSeconds(itemId));
    }

    [Fact]
    public void SunPassiveCountsLivingOrbsWithoutTierWeight()
    {
        Assert.Equal(1f, OrbData.GetSunPveAttackMultiplier([]));
        Assert.Equal(1.15f, OrbData.GetSunPveAttackMultiplier(Items(107000012)));
        Assert.Equal(1.20f, OrbData.GetSunPveAttackMultiplier(Items(107000010, 107000012)));
        Assert.Equal(1.40f, OrbData.GetSunPveAttackMultiplier(
            Items(107000010, 107000010, 107000010, 107000010, 107000010, 107000010, 107000010)));
    }

    [Fact]
    public void WindPassiveCountsLivingOrbsWithoutTierWeight()
    {
        Assert.Equal(1f, OrbData.GetWindMoveSpeedMultiplier([]));
        Assert.Equal(1.06f, OrbData.GetWindMoveSpeedMultiplier(Items(107000022)));
        Assert.Equal(1.08f, OrbData.GetWindMoveSpeedMultiplier(Items(107000020, 107000022)));
        Assert.Equal(1.14f, OrbData.GetWindMoveSpeedMultiplier(
            Items(107000020, 107000020, 107000020, 107000020, 107000020, 107000020)));
    }

    [Theory]
    [InlineData(107000010, OrbColor.Red, 1)]
    [InlineData(107000011, OrbColor.Red, 2)]
    [InlineData(107000012, OrbColor.Red, 3)]
    [InlineData(107000020, OrbColor.Green, 1)]
    [InlineData(107000021, OrbColor.Green, 2)]
    [InlineData(107000022, OrbColor.Green, 3)]
    [InlineData(107000030, OrbColor.Blue, 1)]
    [InlineData(107000031, OrbColor.Blue, 2)]
    [InlineData(107000032, OrbColor.Blue, 3)]
    public void ColoredOrbIdsMapToStableColorAndTier(int itemId, OrbColor expectedColor, int expectedTier)
    {
        Assert.True(OrbData.TryGetColorAndTier(itemId, out var color, out int tier));
        Assert.Equal(expectedColor, color);
        Assert.Equal(expectedTier, tier);
    }
    [Theory]
    [InlineData(107000040, 1, 5)]
    [InlineData(107000041, 2, 10)]
    [InlineData(107000042, 3, 20)]
    public void RecoveryOrbTierDefinesFiveSecondRecoveryAmount(
        int itemId,
        int expectedTier,
        int expectedRecovery)
    {
        Assert.Equal(5f, OrbData.RecoveryTickSeconds);
        Assert.True(OrbData.TryGetRecoveryTier(itemId, out int tier));
        Assert.Equal(expectedTier, tier);
        Assert.Equal(expectedRecovery, OrbData.GetRecoveryAmount(itemId));
        Assert.False(OrbData.IsOrbItem(itemId));
    }

    [Theory]
    [InlineData(107000010, 107000010)]
    [InlineData(107000011, 107000011)]
    [InlineData(107000020, 107000020)]
    [InlineData(107000021, 107000021)]
    [InlineData(107000030, 107000030)]
    [InlineData(107000031, 107000031)]
    public void SameColorSameTierOrbMergeEvolvesToRandomNextTier(int inputA, int inputB)
    {
        var random = new Random(198);
        var outputs = new HashSet<int>();
        for (int i = 0; i < 50; i++)
        {
            Assert.True(OrbData.TryGetRandomMergeOutput(inputA, inputB, random, out int output));
            Assert.True(OrbData.TryGetColorAndTier(inputA, out _, out int inputTier));
            Assert.True(OrbData.TryGetColorAndTier(output, out _, out int outputTier));
            Assert.Equal(inputTier + 1, outputTier);
            outputs.Add(output);
        }

        Assert.All(outputs, output => Assert.True(OrbData.IsOrbItem(output)));
        // 공급 차단 토글(SWARM_SUN/WIND/WAVE_ORB_ENABLED)이 꺼진 색은 머지 출력에도 안 나온다 —
        // 켜진 색이 하나뿐이면 출력 다양성 검증은 성립하지 않는다.
        int enabledColorCount = (Config.SWARM_SUN_ORB_ENABLED ? 1 : 0) +
                                (Config.SWARM_WIND_ORB_ENABLED ? 1 : 0) +
                                (Config.SWARM_WAVE_ORB_ENABLED ? 1 : 0);
        Assert.All(outputs, output =>
        {
            Assert.True(OrbData.TryGetColorAndTier(output, out OrbColor outputColor, out _));
            Assert.True(outputColor switch
            {
                OrbColor.Green => Config.SWARM_WIND_ORB_ENABLED,
                OrbColor.Red => Config.SWARM_SUN_ORB_ENABLED,
                OrbColor.Blue => Config.SWARM_WAVE_ORB_ENABLED,
                _ => false
            });
        });
        Assert.Equal(enabledColorCount > 1, outputs.Count > 1);
    }

    [Theory]
    [InlineData(107000040, 107000041)]
    [InlineData(107000041, 107000042)]
    public void SameTierRecoveryOrbsMergeToTheNextRecoveryTier(int input, int expectedOutput)
    {
        Assert.True(OrbData.CanMerge(input, input));
        Assert.True(OrbData.TryGetRandomMergeOutput(input, input, new Random(198), out int output));
        Assert.Equal(expectedOutput, output);
    }

    [Theory]
    [InlineData(107000010, 107000020)]
    [InlineData(107000010, 107000011)]
    [InlineData(107000012, 107000012)]
    public void DifferentColorDifferentTierOrTierThreeCannotMerge(int inputA, int inputB)
    {
        Assert.False(OrbData.CanMerge(inputA, inputB));
        Assert.False(OrbData.TryGetRandomMergeOutput(inputA, inputB, new Random(1), out _));
    }

    [Fact]
    public void LegacyGuardianOrbDoesNotEnterColoredBoardRules()
    {
        Assert.False(OrbData.TryGetColorAndTier(107000003, out var color, out int tier));
        Assert.Equal(OrbColor.None, color);
        Assert.Equal(0, tier);
    }

    [Fact]
    public void ResonanceUsesEquippedColorAndAnyOtherTier()
    {
        Assert.True(OrbData.TryGetActivePair(107000010, [107000021, 107000012], out var color,
            out int supportTier));
        Assert.Equal(OrbColor.Red, color);
        Assert.Equal(3, supportTier);

        Assert.False(OrbData.TryGetActivePair(107000010, [], out color, out supportTier));
        Assert.Equal(OrbColor.Red, color);
        Assert.Equal(0, supportTier);
    }

    [Fact]
    public void MultiplePairsStillResolveOnlyTheEquippedColorAndNoCrossColorFallback()
    {
        Assert.True(OrbData.TryGetActivePair(107000010,
            [107000011, 107000020, 107000021, 107000030, 107000031], out var color, out int supportTier));
        Assert.Equal(OrbColor.Red, color);
        Assert.Equal(2, supportTier);

        Assert.False(OrbData.TryGetActivePair(107000010,
            [107000020, 107000021, 107000030, 107000031], out color, out supportTier));
        Assert.Equal(OrbColor.Red, color);
        Assert.Equal(0, supportTier);
    }

    [Fact]
    public void BoardResonanceDoesNotRequireAnEquippedOrb()
    {
        Assert.True(OrbData.TryGetActivePair(
            [107000010, 107000020, 107000021],
            out var color,
            out int supportTier));
        Assert.Equal(OrbColor.Green, color);
        Assert.Equal(2, supportTier);

        Assert.False(OrbData.HasActivePair(
            [107000010, 107000020, 107000021],
            OrbColor.Red,
            out _));
        Assert.True(OrbData.HasActivePair(
            [107000010, 107000020, 107000021],
            OrbColor.Green,
            out supportTier));
        Assert.Equal(2, supportTier);
    }

    [Fact]
    public void BoardCanActivateMultipleResonanceColorsAtOnce()
    {
        int[] board = [107000010, 107000011, 107000020, 107000021, 107000030, 107000031];

        Assert.True(OrbData.HasActivePair(board, OrbColor.Red, out int redTier));
        Assert.True(OrbData.HasActivePair(board, OrbColor.Green, out int greenTier));
        Assert.True(OrbData.HasActivePair(board, OrbColor.Blue, out int blueTier));
        Assert.Equal(2, redTier);
        Assert.Equal(2, greenTier);
        Assert.Equal(2, blueTier);
    }

    [Fact]
    public void InventoryResonanceWorksBeforeAnyManualEquip()
    {
        var inventory = new PlayerInGameInventory(198);
        inventory.AddItem(107000030, forceSeparateStack: true);
        inventory.AddItem(107000032, forceSeparateStack: true);

        Assert.Null(inventory.GetEquippedBattleItem());
        Assert.True(inventory.TryGetActiveOrbPair(out var color, out int supportTier));
        Assert.Equal(OrbColor.Blue, color);
        Assert.Equal(3, supportTier);
    }

    [Fact]
    public void FirstColoredOrbPickupAutoEquipsWithoutReplacingItOnLaterPickups()
    {
        InitializeBattleCombatData();
        var manager = new InGameInventoryManager();
        manager.Initialize();

        Assert.True(manager.TryAddItemWithCapacity(10, 100, 107000010, 6, out var firstOrb));
        Assert.Equal(firstOrb!.ItemUid, manager.GetEquippedBattleItem(10, 100)!.ItemUid);

        Assert.True(manager.TryAddItemWithCapacity(10, 100, 107000020, 6, out _));
        Assert.Equal(firstOrb.ItemUid, manager.GetEquippedBattleItem(10, 100)!.ItemUid);
    }

    [Fact]
    public void EquippedInputStaysEquippedAndResonanceRecomputesAfterRandomMerge()
    {
        InitializeBattleCombatData();
        var inventory = new PlayerInGameInventory(198);
        var equippedOrb = inventory.AddItem(107000010, forceSeparateStack: true);
        inventory.AddItem(107000010, forceSeparateStack: true);

        Assert.True(inventory.TryEquipBattleItem(equippedOrb.ItemUid, out _));
        Assert.True(inventory.TryGetActiveOrbPair(out var color, out int supportTier));
        Assert.Equal(OrbColor.Red, color);
        Assert.Equal(1, supportTier);

        Assert.True(inventory.TryCombineOrbs(107000010, 107000010, new Random(1), out int output, out _));
        Assert.Equal(output, inventory.GetEquippedBattleItem()!.ItemId);
        Assert.False(inventory.TryGetActiveOrbPair(out color, out supportTier));
    }

    [Fact]
    public void ReconnectRecomputesTheSameSingleResonanceFromTheAuthoritativeBoard()
    {
        InitializeBattleCombatData();
        var manager = new InGameInventoryManager();
        manager.Initialize();
        Assert.True(manager.TryAddItemWithCapacity(198, 101, 107000010, 6, out _));
        Assert.True(manager.TryAddItemWithCapacity(198, 101, 107000010, 6, out _));
        Assert.True(manager.TryAddItemWithCapacity(198, 101, 107000020, 6, out _));
        Assert.True(manager.TryAddItemWithCapacity(198, 101, 107000020, 6, out _));

        var reconnectedInventory = manager.GetPlayerInventory(198, 101);
        Assert.True(reconnectedInventory.TryGetActiveOrbPair(out var color, out int supportTier));
        Assert.Equal(OrbColor.Red, color);
        Assert.Equal(1, supportTier);
    }
    [Fact]
    public async Task ConcurrentRandomMergeConsumesInputsOnlyOnce()
    {
        InitializeBattleCombatData();
        var manager = new InGameInventoryManager();
        manager.Initialize();
        manager.AddItem(10, 100, 107000010);
        manager.AddItem(10, 100, 107000010);

        var attempts = await Task.WhenAll(
            Task.Run(() => manager.TryCombineOrbs(10, 100, 107000010, 107000010, new Random(1), out _, out _)),
            Task.Run(() => manager.TryCombineOrbs(10, 100, 107000010, 107000010, new Random(2), out _, out _)));

        Assert.Single(attempts, success => success);
        Assert.Single(attempts, success => !success);
        Assert.Equal(1, manager.GetPlayerInventory(10, 100).GetAllItems().Sum(item => item.Count));
    }

    [Fact]
    public void ReconnectingToTheSameMatchReadsTheSingleAuthoritativeMergeResult()
    {
        InitializeBattleCombatData();
        var manager = new InGameInventoryManager();
        manager.Initialize();
        manager.AddItem(198, 100, 107000010);
        manager.AddItem(198, 100, 107000010);

        Assert.True(manager.TryCombineOrbs(198, 100, 107000010, 107000010,
            new Random(198), out int outputItemId, out _));

        // A new session retrieves the same matching/player inventory; it must not replay the merge.
        var reconnectedInventory = manager.GetPlayerInventory(198, 100);
        var output = Assert.Single(reconnectedInventory.GetAllItems());
        Assert.Equal(outputItemId, output.ItemId);
        Assert.Equal(1, output.Count);
        Assert.False(manager.TryCombineOrbs(198, 100, 107000010, 107000010,
            new Random(199), out _, out _));
    }

    private static void InitializeBattleCombatData()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "network", "Common", "csv")))
            directory = directory.Parent;

        if (directory == null)
            throw new DirectoryNotFoundException("Could not locate repository root from test output path.");

        BattleItemCombatData.Initialize(CsvHelper.LoadCsv(Path.Combine(
            directory.FullName, "network", "Common", "csv", "battle_item_combat.csv")));
    }

    private static IReadOnlyList<InGameItemInfo> Items(params int[] itemIds) =>
        itemIds.Select((itemId, index) => new InGameItemInfo
        {
            ItemUid = index + 1,
            ItemId = itemId,
            Count = 1
        }).ToList();
}
