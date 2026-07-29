using System.Text.Json;
using network.common.data;
using network.common.data.helpers;

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
    public void GuardianCombatDataDefinesLegacyAndThreeColoredOrbTierLines()
    {
        string csvRoot = Path.Combine(FindRepositoryRoot(), "network", "Common", "csv");
        BattleItemCombatData.Initialize(
            CsvHelper.LoadCsv(Path.Combine(csvRoot, "battle_item_combat.csv")));

        var definitions = BattleItemCombatData.GetAll()
            .Where(definition => definition.Family == "orb")
            .ToArray();
        int[][] orbLines =
        [
            [107000003, 107000004, 107000006],
            [107000010, 107000011, 107000012],
            [107000020, 107000021, 107000022],
            [107000030, 107000031, 107000032]
        ];

        Assert.Equal(orbLines.SelectMany(line => line), definitions.Select(definition => definition.ItemId));
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
        foreach (int[] orbLine in orbLines)
            AssertGuardianTierGrowth(definitions.Where(definition => orbLine.Contains(definition.ItemId)).ToArray());
    }

    [Fact]
    public void CompassCombatDataDefinesTheRiggedMeleeWeapon()
    {
        string csvRoot = Path.Combine(FindRepositoryRoot(), "network", "Common", "csv");
        BattleItemCombatData.Initialize(
            CsvHelper.LoadCsv(Path.Combine(csvRoot, "battle_item_combat.csv")));

        var definition = Assert.Single(
            BattleItemCombatData.GetAll(), candidate => candidate.ItemId == 107000015);

        Assert.Equal("compass", definition.Family);
        Assert.Equal(1, definition.Tier);
        Assert.Equal(1.8f, definition.AttackRange);
        Assert.Equal(8, definition.Damage);
        Assert.Equal(1.2f, definition.AttackIntervalSeconds);
        Assert.Equal(0f, definition.ProjectileWidth);
        Assert.Equal(0f, definition.EffectDurationSeconds);
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

    [Fact]
    public void AuthoritativeProximityDamageFeedsNumericWorldPopup()
    {
        string repoRoot = FindRepositoryRoot();
        string gameServerSource = ReadNormalizedSource(
            repoRoot, "game_server", "GameServer.ProximityAutoCombat.cs");
        string sessionSource = ReadNormalizedSource(
            repoRoot, "game_server", "Network", "GameClientSession.ProximityAutoCombat.cs");
        string mapSource = ReadNormalizedSource(
            repoRoot, "client", "Assets", "Scripts", "Managers", "Map", "MapManager.PlayerVisibility.cs");
        string playerSource = ReadNormalizedSource(
            repoRoot, "client", "Assets", "Scripts", "Components", "Player", "Player.cs");

        Assert.Contains("targetSession.ApplyProximityAutoCombatHit(", gameServerSource);
        Assert.Contains("attack.WeaponItemId,", gameServerSource);
        Assert.Contains("damage);", gameServerSource);
        Assert.Contains("weaponItemId,\n            damage);", sessionSource);
        Assert.Contains(
            "localPlayerIsAttacker: true,\n                    packet.DamageValue);",
            mapSource);
        Assert.Contains(
            "int damageValue = authoritativeDamageValue > 0\n" +
            "                ? authoritativeDamageValue",
            mapSource);
        Assert.Contains("_guardianHitDisplay.Play(Mathf.Max(1, damageValue), damageColor", playerSource);
        Assert.DoesNotContain("오염 +", playerSource);
    }

    [Fact]
    public void NeutralAfterimageMonstersDisableTheirOrbGlyphWhenReusedFromThePool()
    {
        string source = ReadNormalizedSource(
            FindRepositoryRoot(), "client", "Assets", "Scripts", "Components", "MapObject",
            "EmotionAfterimageOrbTheme.cs");

        Assert.Contains("private int _itemId = -1;", source);
        Assert.Contains("_core.enabled = itemId > 0;", source);
    }
    [Fact]
    public void ObserversSeeBotOrbProjectilesWhenTheTargetIsAnAfterimageMonster()
    {
        string repoRoot = FindRepositoryRoot();
        string serverSource = ReadNormalizedSource(
            repoRoot, "game_server", "GameServer.EmotionAfterimageMonsters.cs");
        string mapSource = ReadNormalizedSource(
            repoRoot, "client", "Assets", "Scripts", "Managers", "Map", "MapManager.PlayerVisibility.cs");

        Assert.Contains(
            "if (primaryHit && EmotionAfterimagePveCombatRules.ShouldEmitWaveProjectilePresentation(attack.IsWaveAreaSecondary))\n" +
            "                BroadcastObservedProximityAttackVfx(attack, matchingSessions);",
            serverSource);
        Assert.Contains("if (packet.TargetPlayerId < 0)", mapSource);
        Assert.Contains("PlayObservedGuardianProjectileAtMonster(attacker, monster, packet.WeaponItemId);", mapSource);
    }
    [Fact]
    public void LocalDamageDisplayPulsesInventoryDecoWhileTheTotalIsPresented()
    {
        string source = ReadNormalizedSource(
            FindRepositoryRoot(), "client", "Assets", "Scripts", "UserInterfaces", "InGame", "Display",
            "DamageComboDisplay.cs");

        Assert.Contains("InventoryDecoPath = \"InGameCanvas/Displays/Inventory/Deco\"", source);
        Assert.Contains("PulseInventoryDeco();", source);
        Assert.Contains("private IEnumerator PulseInventoryDecoRoutine()", source);
        Assert.Contains("Time.unscaledDeltaTime", source);
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

    private static string ReadNormalizedSource(string repositoryRoot, params string[] pathParts)
    {
        string[] fullPathParts = [repositoryRoot, .. pathParts];
        return File.ReadAllText(Path.Combine(fullPathParts))
            .Replace("\r\n", "\n");
    }

    private static void AssertGuardianTierGrowth(
        IReadOnlyCollection<BattleItemCombatDefinition> definitions)
    {
        var tiers = definitions
            .GroupBy(definition => definition.Tier)
            .ToDictionary(group => group.Key, group => group.ToList());
        var tierOne = Assert.Single(tiers[1]);

        Assert.All(tiers[2], tierTwo =>
        {
            Assert.True(tierTwo.Damage > tierOne.Damage);
            Assert.True(tierTwo.AttackIntervalSeconds < tierOne.AttackIntervalSeconds);
        });
        Assert.All(tiers[3], tierThree =>
        {
            Assert.True(tierThree.Damage > tiers[2].Max(tierTwo => tierTwo.Damage));
            Assert.True(tierThree.AttackIntervalSeconds < tiers[2].Min(tierTwo => tierTwo.AttackIntervalSeconds));
        });
    }
}
