namespace demo_regression_tests;

public class ProximityAutoCombatDataTests
{
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
    public void OrbEffectStatesArePrunedWhenTheTargetLeavesTheCurrentArea()
    {
        string source = ReadMapManagerSources(FindRepositoryRoot());

        Assert.Contains("PruneOutOfAreaOrbEffectStates();", source);
        Assert.Contains("PruneOrbEffectStateIfOutOfArea(player);", source);
        Assert.Contains("_orbEffectStates.Remove(playerId);", source);
        Assert.Contains("_orbEffectStates.Remove(player.Info.PlayerId);", source);
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

    /// <summary>MapManager 파셜 전체를 이어 붙여 읽는다 (#252 분할 후 책임별 파셜에 흩어져 있다).</summary>
    private static string ReadMapManagerSources(string repoRoot)
    {
        string dir = Path.Combine(repoRoot, "client", "Assets", "Scripts", "Managers", "Map");
        var builder = new System.Text.StringBuilder();
        foreach (string file in Directory.GetFiles(dir, "MapManager*.cs"))
            builder.AppendLine(File.ReadAllText(file).Replace("\r\n", "\n"));
        return builder.ToString();
    }

    private static string ReadNormalizedSource(string repositoryRoot, params string[] pathParts)
    {
        string[] fullPathParts = [repositoryRoot, .. pathParts];
        return File.ReadAllText(Path.Combine(fullPathParts))
            .Replace("\r\n", "\n");
    }
}
