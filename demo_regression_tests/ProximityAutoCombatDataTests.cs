using System.Text.Json;
using network.common.data.helpers;
using network.common.data;

namespace demo_regression_tests;

public class ProximityAutoCombatDataTests
{
    [Fact]
    public void ChalkWeaponDamageIncreasesWithEachMergeTier()
    {
        string csvRoot = Path.Combine(FindRepositoryRoot(), "network", "Common", "csv");
        var damageByItemId = CsvHelper.LoadCsv(Path.Combine(csvRoot, "item_info_consumable.csv"))
            .Where(row => row["item_id"] is "201000015" or "201000016" or "201000017")
            .ToDictionary(
                row => int.Parse(row["item_id"]),
                row => JsonSerializer.Deserialize<List<List<int>>>(row["buff_list"])![0][1]);

        Assert.Equal(6, damageByItemId[201000015]);
        Assert.Equal(10, damageByItemId[201000016]);
        Assert.Equal(14, damageByItemId[201000017]);
    }

    [Fact]
    public void ChalkMergeRouteBuildsTowardTheStrongestWeapon()
    {
        string csvRoot = Path.Combine(FindRepositoryRoot(), "network", "Common", "csv");
        var recipes = CsvHelper.LoadCsv(Path.Combine(csvRoot, "battle_item_recipe.csv"))
            .Where(row => row["category"] == "chalk")
            .ToDictionary(row => int.Parse(row["output_item_id"]));

        Assert.Equal(
            [201000015, 201000015],
            JsonSerializer.Deserialize<List<int>>(recipes[201000016]["input_item_ids"]));
        Assert.Equal(
            [201000016, 201000016],
            JsonSerializer.Deserialize<List<int>>(recipes[201000017]["input_item_ids"]));
    }

    [Fact]
    public void GuardianCombatDataDefinesOneOrbAcrossThreeTiers()
    {
        string csvRoot = Path.Combine(FindRepositoryRoot(), "network", "Common", "csv");
        BattleItemCombatData.Initialize(
            CsvHelper.LoadCsv(Path.Combine(csvRoot, "battle_item_combat.csv")));

        var definitions = BattleItemCombatData.GetAll();

        Assert.Equal([107000003, 107000004, 107000006], definitions.Select(definition => definition.ItemId));
        Assert.All(definitions, definition =>
        {
            Assert.Equal("orb", definition.Family);
            Assert.InRange(definition.Tier, 1, 3);
            Assert.True(definition.AttackRange > 0f);
            Assert.True(definition.Damage > 0);
            Assert.True(definition.AttackIntervalSeconds > 0f);
            Assert.Equal(0.25f, definition.ProjectileWidth);
            Assert.Equal(0f, definition.EffectDurationSeconds);
        });

        AssertGuardianTierGrowth("orb", definitions);
    }
    [Fact]
    public void GuardianCombatDataMatchesBothUnityMirrors()
    {
        string repoRoot = FindRepositoryRoot();
        byte[] canonical = File.ReadAllBytes(
            Path.Combine(repoRoot, "network", "Common", "csv", "battle_item_combat.csv"));

        foreach (string clientRoot in new[] { "Resources", "StreamingAssets" })
        {
            Assert.Equal(
                canonical,
                File.ReadAllBytes(Path.Combine(
                    repoRoot, "client", "Assets", clientRoot, "Common", "csv", "battle_item_combat.csv")));
        }
    }

    [Fact]
    public void ConsumableItemDataMatchesBothUnityMirrors()
    {
        string repoRoot = FindRepositoryRoot();
        byte[] canonical = File.ReadAllBytes(
            Path.Combine(repoRoot, "network", "Common", "csv", "item_info_consumable.csv"));

        Assert.Equal(
            canonical,
            File.ReadAllBytes(Path.Combine(
                repoRoot,
                "client",
                "Assets",
                "Resources",
                "Common",
                "csv",
                "item_info_consumable.csv")));
        Assert.Equal(
            canonical,
            File.ReadAllBytes(Path.Combine(
                repoRoot,
                "client",
                "Assets",
                "StreamingAssets",
                "Common",
                "csv",
                "item_info_consumable.csv")));
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

    private static void AssertGuardianTierGrowth(
        string family,
        IReadOnlyCollection<BattleItemCombatDefinition> definitions)
    {
        var tiers = definitions.Where(definition => definition.Family == family)
            .GroupBy(definition => definition.Tier)
            .ToDictionary(group => group.Key, group => group.ToList());
        var tierOne = Assert.Single(tiers[1]);

        Assert.All(tiers[2], tierTwo =>
        {
            Assert.True(tierTwo.Damage > tierOne.Damage);
            Assert.Equal(tierOne.AttackIntervalSeconds / 2f, tierTwo.AttackIntervalSeconds, 3);
        });
        Assert.All(tiers[3], tierThree =>
        {
            Assert.True(tierThree.Damage > tiers[2].Max(tierTwo => tierTwo.Damage));
            Assert.Equal(tierOne.AttackIntervalSeconds / 4f, tierThree.AttackIntervalSeconds, 3);
        });
    }
}
