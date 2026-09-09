using game_server.matches.combat;
using game_server.matches;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using game_server;
using game_server.services;
using game_server.sessions;
using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

public class ProximityAutoCombatDataTests
{
    [Fact]
    public void ChalkWeaponVariantsHaveDistinctDamage()
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
            repoRoot, "game_server", "Matches", "Combat", "MatchCombatService.cs");
        string sessionSource = ReadNormalizedSource(
            repoRoot, "game_server", "Matches", "Combat", "MatchCombatDamageService.cs");
        string mapSource = ReadMapManagerSources(repoRoot);
        string playerSource = ReadNormalizedSource(
            repoRoot, "client", "Assets", "Scripts", "Components", "Player", "Player.cs");

        Assert.Contains("combatDamage.ApplyProximityAutoCombatHit(targetSession,", gameServerSource);
        Assert.Contains("attack.WeaponItemId,", gameServerSource);
        Assert.Contains("WeaponItemId = weaponItemId", sessionSource);
        Assert.Contains("Damage = damage", sessionSource);
        // 명중 전용 연출로 리팩터링되어 단일 호출 형태를 검사한다.
        Assert.Contains(
            "PlayGuardianHitOnly(packet.AttackerId, localPlayerIsAttacker: false, packet.Damage);",
            mapSource);
        Assert.Contains(
            "int damageValue = authoritativeDamageValue > 0 ? authoritativeDamageValue : 0;",
            mapSource);
        // 수치 팝업은 MonsterDamageLabel/DamageComboDisplay 경로가 맡는다 (Player.ShowGuardianHitDisplay는 호출자 0으로 #252에서 삭제).
        Assert.DoesNotContain("오염 +", playerSource);
    }

    [Fact]
    public void RecoveryOrbRecordsHumanRecoveryOnlyThroughSessionStats()
    {
        string source = ReadNormalizedSource(
            FindRepositoryRoot(), "game_server", "Services", "OrbRecoveryService.cs");

        Assert.Contains(
            "if (session == null)\n" +
            "            {\n" +
            "                eventLogs.RecordRecovery(\n" +
            "                    matchingId, playerId, effectiveRecovery);\n" +
            "            }",
            source);
    }

    [Fact]
    public void OrbVisualPublicationCapture_DeepCopiesItemIds()
    {
        MethodInfo capture = Assert.IsType<MethodInfo>(typeof(OrbVisualStatePublisher).GetMethod(
            "CaptureSwarmOrbVisualItemIds",
            BindingFlags.NonPublic | BindingFlags.Static), exactMatch: false);
        var source = new List<int> { 107000010, 107000020, 107000030 };

        var snapshot = Assert.IsType<ImmutableArray<int>>(capture.Invoke(null, [source]));
        source[0] = 999;
        source.Add(998);

        Assert.Equal([107000010, 107000020, 107000030], snapshot.ToArray());
    }

    [Fact]
    public void OrbVisualPublication_UsesOneCommitAndSendStepPerCandidate()
    {
        string source = ReadNormalizedSource(
            FindRepositoryRoot(), "game_server", "Services", "OrbVisualStatePublisher.cs");
        string append = ReadMethodSlice(
            source,
            "private void DispatchOrbVisualStatePublications(",
            "private void CommitAndDispatchOrbVisualStatePublication(");
        string commitAndDispatch = ReadMethodSlice(
            source,
            "private void CommitAndDispatchOrbVisualStatePublication(",
            "private static ImmutableArray<int> CaptureSwarmOrbVisualItemIds(");

        AssertInOrder(
            append,
            "foreach (SwarmOrbVisualPublication publication in publications)",
            "CommitAndDispatchOrbVisualStatePublication(publication);");
        AssertInOrder(
            commitAndDispatch,
            "visualStates[key] = state;",
            "visualStates.TryRemove(key, out _);",
            "Packet.Create((int)Protocol.G_TO_C_ORB_EFFECT_STATE)",
            "publication.Recipient!.TrySend(packet);");
        Assert.DoesNotContain("catch", append);
        Assert.DoesNotContain("catch", commitAndDispatch);
    }

    [Fact]
    public void OrbVisualPublication_PreparesImmutablePlanBeforeInLockDispatch()
    {
        string root = FindRepositoryRoot();
        string proximity = ReadNormalizedSource(
            root, "game_server", "Services", "OrbVisualStatePublisher.cs");
        string combat = ReadNormalizedSource(root, "game_server", "Matches", "Combat", "MatchCombatService.cs");
        int prepareStart = proximity.IndexOf(
            "private ImmutableArray<SwarmOrbVisualPublication> PrepareOrbVisualStatePublications(",
            StringComparison.Ordinal);
        int appendStart = proximity.IndexOf(
            "private void DispatchOrbVisualStatePublications(",
            StringComparison.Ordinal);
        int captureStart = proximity.IndexOf(
            "private static ImmutableArray<int> CaptureSwarmOrbVisualItemIds(",
            StringComparison.Ordinal);
        Assert.True(prepareStart >= 0 && appendStart > prepareStart && captureStart > appendStart);
        string prepare = proximity[prepareStart..appendStart];
        string dispatch = proximity[appendStart..captureStart];

        Assert.Contains("orbVisuals.Publish(matchingId, actors, sessions);", combat);
        Assert.Contains(
            "DispatchOrbVisualStatePublications(\n" +
            "            PrepareOrbVisualStatePublications(matchingId, actors, matchingSessions));",
            proximity);
        AssertInOrder(
            prepare,
            "GameClientSession[] recipientSnapshot = matchingSessions.ToArray();",
            "foreach (var observer in recipientSnapshot)",
            "foreach (var visualActor in visualActors)",
            "if (observer.CurrentArea != actor.Area)",
            "publications.Add(SwarmOrbVisualPublication.Remove(",
            "visualStates.TryGetValue(key, out var previousState)",
            "publications.Add(SwarmOrbVisualPublication.Publish(",
            "CaptureSwarmOrbVisualItemIds(visualActor.OrbItemIds)");
        Assert.DoesNotContain("Packet.Create", prepare);
        Assert.DoesNotContain(".TrySend(", prepare);
        Assert.DoesNotContain("visualStates[key] = state;", prepare);
        Assert.DoesNotContain("visualStates.TryRemove(key, out _);", prepare);

        AssertInOrder(
            dispatch,
            "CommitAndDispatchOrbVisualStatePublication(publication)",
            "visualStates[key] = state;",
            "visualStates.TryRemove(key, out _);",
            "Packet.Create((int)Protocol.G_TO_C_ORB_EFFECT_STATE)",
            "OrbItemIds = publication.OrbItemIds.ToList()",
            "publication.Recipient!.TrySend(packet);");
        Assert.DoesNotContain("catch", dispatch);
    }

    [Fact]
    public void NeutralAfterimageMonstersDisableTheirOrbGlyphWhenReusedFromThePool()
    {
        string source = ReadNormalizedSource(
            FindRepositoryRoot(), "client", "Assets", "Scripts", "Components", "MapObject",
            "SwarmAfterimageOrbTheme.cs");

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
        Assert.Contains("_orbEffectStates.Remove(playerId);", source);
    }

    [Fact]
    public void ObserversSeeBotOrbProjectilesWhenTheTargetIsAnAfterimageMonster()
    {
        string repoRoot = FindRepositoryRoot();
        string mapSource = ReadMapManagerSources(repoRoot);

        // #238: 레거시 잔상 공격 파이프라인 퇴역 — 현행 스웜의 몬스터 공격 피드백 계약을 검사한다.
        string swarmSource = ReadNormalizedSource(repoRoot, "game_server", "Matches", "Combat", "MatchCombatService.cs");
        Assert.Contains("SendMonsterHitNotification(", swarmSource);
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

    private static string ReadMethodSlice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{startMarker}'.");
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find '{endMarker}' after '{startMarker}'.");
        return source[start..end];
    }

    private static void AssertInOrder(string source, params string[] markers)
    {
        int previousIndex = -1;
        foreach (string marker in markers)
        {
            int currentIndex = source.IndexOf(marker, previousIndex + 1, StringComparison.Ordinal);
            Assert.True(currentIndex > previousIndex, $"Expected '{marker}' after index {previousIndex}.");
            previousIndex = currentIndex;
        }
    }

    // 티어 값어치 = 발당 피해 성장. 공속은 단축 금지 (#268, 2026-08-24: 티어 주기 단축 퇴역 —
    // 현행 오브는 전 티어 0.8 고정, 레거시 가디언 라인만 옛 단축 값을 유지한다).
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
            Assert.True(tierTwo.AttackIntervalSeconds <= tierOne.AttackIntervalSeconds);
        });
        Assert.All(tiers[3], tierThree =>
        {
            Assert.True(tierThree.Damage > tiers[2].Max(tierTwo => tierTwo.Damage));
            Assert.True(tierThree.AttackIntervalSeconds <= tiers[2].Min(tierTwo => tierTwo.AttackIntervalSeconds));
        });
    }
}
