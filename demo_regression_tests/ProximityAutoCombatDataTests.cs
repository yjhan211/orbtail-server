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

    [Fact]
    public void ObserversSeeBotOrbProjectilesWhenTheTargetIsAnAfterimageMonster()
    {
        string repoRoot = FindRepositoryRoot();
        string mapSource = ReadMapManagerSources(repoRoot);

        // #238: 레거시 잔상 공격 파이프라인 퇴역 — 현행 스웜의 몬스터 공격 피드백 계약을 검사한다.
        string swarmSource = ReadNormalizedSource(repoRoot, "game_server", "Matches", "MatchCombatService.cs");
        Assert.Contains("QueueMonsterHitNotification(", swarmSource);
        // 봇 플레이어 ID도 음수라 플레이어 맵 우선 해석이 계약이다 (#219 봇전 연출 증발 수리)
        Assert.Contains(
            "if (packet.TargetPlayerId < 0 && !_playerMap.ContainsKey(packet.TargetPlayerId))",
            mapSource);
        Assert.Contains("PlayObservedGuardianProjectileAtMonster(attacker, monster, packet.WeaponItemId);", mapSource);
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
