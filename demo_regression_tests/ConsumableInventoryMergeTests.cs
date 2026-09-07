using game_server.services;
using network.common;
using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

public sealed class ConsumableInventoryMergeTests
{
    private const int BandageItemId = 201000008;
    private const int CannedCoffeeItemId = 201000011;
    private const int FirstAidKitItemId = 201000018;
    private const int CompressionBandageItemId = 201000019;
    private const int DoubleShotCoffeeItemId = 201000020;

    public ConsumableInventoryMergeTests()
    {
        GameDataHelper.SetBasePath(Path.Combine(FindRepositoryRoot(), "network"));
        GameDataHelper.Initialize();
    }

    [Theory]
    [InlineData(BandageItemId, CompressionBandageItemId)]
    [InlineData(CannedCoffeeItemId, DoubleShotCoffeeItemId)]
    public void IdenticalConsumablesHaveOneManualMergeRecipe(int inputItemId, int outputItemId)
    {
        var recipe = Assert.Single(BattleItemRecipeData.GetMatchingRecipes([inputItemId, inputItemId]));

        Assert.Equal(outputItemId, recipe.OutputItemId);
        Assert.Equal("consumable_merge", recipe.Category);
        Assert.Equal("primary", recipe.RouteType);
        Assert.Equal(1, recipe.CraftSeconds);
    }

    [Fact]
    public void CrossTypeFinalAndFirstAidMergesAreNotDefined()
    {
        Assert.Empty(BattleItemRecipeData.GetMatchingRecipes([BandageItemId, CannedCoffeeItemId]));
        Assert.Empty(BattleItemRecipeData.GetMatchingRecipes([FirstAidKitItemId, FirstAidKitItemId]));
        Assert.Empty(BattleItemRecipeData.GetMatchingRecipes([CompressionBandageItemId, CompressionBandageItemId]));
        Assert.Empty(BattleItemRecipeData.GetMatchingRecipes([DoubleShotCoffeeItemId, DoubleShotCoffeeItemId]));
    }

    [Fact]
    public void DuplicatePickupConsumablesOccupySeparateSlots()
    {
        var inventory = new PlayerInGameInventory(1941);

        Assert.True(inventory.TryAddItemWithCapacity(BandageItemId, 6, out var first));
        Assert.True(inventory.TryAddItemWithCapacity(BandageItemId, 6, out var second));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.ItemUid, second!.ItemUid);
        Assert.Equal(2, inventory.GetAllItems().Count);
        Assert.All(inventory.GetAllItems(), item => Assert.Equal(1, item.Count));
    }

    [Fact]
    public void RepeatedMergesProduceSeparateFinalItemInstances()
    {
        var inventory = new PlayerInGameInventory(1942);
        for (int i = 0; i < 4; i++)
            inventory.AddItem(BandageItemId);

        Assert.True(inventory.TryCombineItems(
            [BandageItemId, BandageItemId],
            CompressionBandageItemId,
            out var firstChanges));
        Assert.True(inventory.TryCombineItems(
            [BandageItemId, BandageItemId],
            CompressionBandageItemId,
            out var secondChanges));

        Assert.Equal(3, firstChanges.Count);
        Assert.Equal(3, secondChanges.Count);
        Assert.Equal(0, inventory.GetItemCount(BandageItemId));
        var outputs = inventory.GetAllItems()
            .Where(item => item.ItemId == CompressionBandageItemId)
            .ToList();
        Assert.Equal(2, outputs.Count);
        Assert.Equal(2, outputs.Select(item => item.ItemUid).Distinct().Count());
        Assert.All(outputs, item => Assert.Equal(1, item.Count));
    }

    [Theory]
    [InlineData(BandageItemId, 3, 15)]
    [InlineData(CannedCoffeeItemId, 1, 15)]
    [InlineData(CompressionBandageItemId, 3, 30)]
    [InlineData(DoubleShotCoffeeItemId, 1, 30)]
    public void StoredConsumablesKeepTheirRecoveryValue(int itemId, int expectedBuffId, int expectedValue)
    {
        var item = GameItemData.Get(itemId);

        Assert.NotNull(item);
        Assert.True(item.IsConsumable);
        var buff = Assert.Single(item.ConsumableBuffList);
        Assert.Equal(expectedBuffId, buff.id);
        Assert.Equal(expectedValue, buff.value);
        Assert.Equal(0, buff.interval);
    }

    [Fact]
    public void ConsumableDataCsvsMatchUnityMirrors()
    {
        string repoRoot = FindRepositoryRoot();
        foreach (string fileName in new[]
                 {
                     "item_info.csv",
                     "item_info_consumable.csv",
                     "battle_item_recipe.csv"
                 })
        {
            byte[] canonical = File.ReadAllBytes(
                Path.Combine(repoRoot, "network", "Common", "csv", fileName));
            Assert.Equal(canonical, File.ReadAllBytes(Path.Combine(
                repoRoot, "client", "Assets", "Resources", "Common", "csv", fileName)));
            Assert.Equal(canonical, File.ReadAllBytes(Path.Combine(
                repoRoot, "client", "Assets", "StreamingAssets", "Common", "csv", fileName)));
        }
    }

    [Theory]
    [InlineData(BandageItemId, CompressionBandageItemId)]
    [InlineData(CannedCoffeeItemId, DoubleShotCoffeeItemId)]
    public void MergedConsumablesUseDedicatedSingleSprites(int baseItemId, int mergedItemId)
    {
        string repoRoot = FindRepositoryRoot();
        string spriteRoot = Path.Combine(repoRoot, "client", "Assets", "Resources", "ItemSprites");
        // 클라 에셋이 없는 체크아웃(서버·스크립트만 담은 공개 분리본)에서는 검사할 대상이 없다.
        if (!Directory.Exists(spriteRoot)) return;

        string baseSpritePath = Path.Combine(spriteRoot, $"{baseItemId}.png");
        string mergedSpritePath = Path.Combine(spriteRoot, $"{mergedItemId}.png");
        string mergedMetaPath = mergedSpritePath + ".meta";

        Assert.True(File.Exists(mergedSpritePath));
        Assert.True(File.Exists(mergedMetaPath));
        Assert.False(File.ReadAllBytes(baseSpritePath).SequenceEqual(File.ReadAllBytes(mergedSpritePath)));

        string meta = File.ReadAllText(mergedMetaPath);
        Assert.Contains("spriteMode: 1", meta);
        Assert.Contains("alphaIsTransparency: 1", meta);

        string spriteCache = File.ReadAllText(Path.Combine(
            repoRoot, "client", "Assets", "Scripts", "Constants", "ItemSpriteCache.cs"));
        Assert.DoesNotContain($"[{mergedItemId}]", spriteCache);
    }

    [Fact]
    public void RecipeInputConsumablesRemainTapUsable()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "client", "Assets", "Scripts", "UserInterfaces", "Lobby", "ItemSlot.cs"));

        Assert.Contains(
            "bool supportsDirectUse = itemData != null && (itemData.IsConsumable || itemData.IsInstallation);",
            source);
        Assert.Contains(
            "if (IsBattleMergeMaterial && !supportsDirectUse)",
            source);
    }

    [Fact]
    public void HealthRecoveryBuffsUseHealthSlotVisuals()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "client", "Assets", "Scripts", "UserInterfaces", "Lobby", "ItemSlot.cs"));

        Assert.Contains("BuffSubType.HEALTH_ADD", source);
        Assert.DoesNotContain("ConsumableBuffList[0]", source);
        Assert.Contains("SetSlotVisuals(isCondition, isHealth, showWear);", source);
        Assert.Contains("SetSlotVisuals(isCondition, isHealth, false);", source); // 선물 부품(PART_GIFT) 시각 분기는 part 레이어와 함께 삭제(#255)
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
}
