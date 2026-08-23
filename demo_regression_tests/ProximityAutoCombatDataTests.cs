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
            repoRoot, "game_server", "GameServer.SwarmArena.cs");
        string sessionSource = ReadNormalizedSource(
            repoRoot, "game_server", "Network", "GameClientSession.ProximityAutoCombat.cs");
        string mapSource = ReadMapManagerSources(repoRoot);
        string playerSource = ReadNormalizedSource(
            repoRoot, "client", "Assets", "Scripts", "Components", "Player", "Player.cs");

        Assert.Contains("targetSession.ApplyProximityAutoCombatHit(", gameServerSource);
        Assert.Contains("attack.WeaponItemId,", gameServerSource);
        Assert.Contains("weaponItemId,\n            damage);", sessionSource);
        // 명중 전용 연출로 리팩터링되어 단일 호출 형태를 검사한다.
        Assert.Contains(
            "PlayGuardianHitOnly(packet.PlayerId, localPlayerIsAttacker: false, packet.DamageValue);",
            mapSource);
        Assert.Contains(
            "int damageValue = authoritativeDamageValue > 0\n" +
            "                ? authoritativeDamageValue",
            mapSource);
        // 수치 팝업은 MonsterDamageLabel/DamageComboDisplay 경로가 맡는다 (Player.ShowGuardianHitDisplay는 호출자 0으로 #252에서 삭제).
        Assert.DoesNotContain("오염 +", playerSource);
    }

    [Fact]
    public void RecoveryOrbRecordsHumanRecoveryOnlyThroughSessionStats()
    {
        string source = ReadNormalizedSource(
            FindRepositoryRoot(), "game_server", "GameServer.ProximityAutoCombat.cs");

        Assert.Contains(
            "if (session == null)\n" +
            "            {\n" +
            "                _gameEventLogManager.RecordSurvivorRecovery(\n" +
            "                    matchingId, playerId, effectiveRecovery);\n" +
            "            }",
            source);
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
    public void PlayerAffinityEncounterRequiresAVisibleTargetInTheCurrentArea()
    {
        string source = ReadMapManagerSources(FindRepositoryRoot());

        Assert.Contains("!IsPlayerAffinityEncounterVisible(targetPlayerId, out _)", source);
        Assert.Contains("targetPlayer.IsEncounterVisualVisible", source);
        Assert.Contains("return IsRemotePlayerInCurrentArea(targetPlayer);", source);
        Assert.Contains("PruneOutOfAreaPlayerAffinityEncounterStates();", source);
        Assert.Contains("_survivorOrbEffectStates.Remove(playerId);", source);
    }

    [Fact]
    public void ObserversSeeBotOrbProjectilesWhenTheTargetIsAnAfterimageMonster()
    {
        string repoRoot = FindRepositoryRoot();
        string mapSource = ReadMapManagerSources(repoRoot);

        // #238: 레거시 잔상 공격 파이프라인 퇴역 — 현행 스웜의 몬스터 공격 피드백 계약을 검사한다.
        string swarmSource = ReadNormalizedSource(repoRoot, "game_server", "GameServer.SwarmArena.cs");
        Assert.Contains("SendEmotionAfterimageMonsterAttackFeedback(", swarmSource);
        // 봇 플레이어 ID도 음수라 플레이어 맵 우선 해석이 계약이다 (#219 봇전 연출 증발 수리)
        Assert.Contains(
            "if (packet.TargetPlayerId < 0 && !_playerMap.ContainsKey(packet.TargetPlayerId))",
            mapSource);
        Assert.Contains("PlayObservedGuardianProjectileAtMonster(attacker, monster, packet.WeaponItemId);", mapSource);
    }
    // #229: 화면 구석 누적 표시(DamageComboDisplay)를 퇴역하고 숫자를 사건이 난 자리에 띄운다.
    // 세 갈래가 색·부호로 갈려야 "누가 누구를"이 읽힌다 — 하나로 합치면 원래 문제로 돌아간다.
    [Fact]
    public void FloatingValuePopup_SplitsDealtTakenAndRecovery()
    {
        string root = FindRepositoryRoot();
        // 클래스·파일명은 MonsterDamageLabel로 바뀌었다 (CI의 ~Popup 네이밍 금지).
        // 프리팹 에셋 경로는 그대로 두었으므로 아래 PrefabPath 단언은 유지한다.
        string source = ReadNormalizedSource(
            root, "client", "Assets", "Scripts", "Components", "MapObject", "MonsterDamageLabel.cs");

        Assert.Contains("public static void ShowDamageDealt(", source);
        Assert.Contains("public static void ShowDamageTaken(", source);
        Assert.Contains("public static void ShowRecovery(", source);
        // 부호는 색맹 대비 축이다 — 색만으로 방향을 읽게 두지 않는다.
        Assert.Contains("TakenColor, \"-\"", source);
        Assert.Contains("RecoveryColor, \"+\"", source);
        // 생김새는 프리팹이 소유한다 — 런타임 조립으로 되돌아가면 인스펙터 조절이 사라진다.
        Assert.Contains("PrefabPath = \"Prefabs/MonsterDamagePopup\"", source);

        Assert.False(
            File.Exists(Path.Combine(
                root, "client", "Assets", "Scripts", "UserInterfaces", "InGame", "Display",
                "DamageComboDisplay.cs")),
            "DamageComboDisplay가 되살아났다 — 피해 숫자는 사건이 난 자리에만 뜬다 (#229).");
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
