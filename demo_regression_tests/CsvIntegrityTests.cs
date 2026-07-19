using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

/// <summary>
///     CSV 간 참조 무결성(게임 내장 검증기가 안 잡는 부분) + 태그 그래프 도달성 린트.
///     구조 리팩터(node 통합·로컬라이징 분리 등) 시 조용한 실패를 잡는 안전망.
///     카운트가 아니라 "관계"만 검증하므로 콘텐츠 저작이 늘어도 깨지지 않는다.
/// </summary>
public class CsvIntegrityTests
{
    private static void Init()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();
    }

    [Fact]
    public void Node_InteractId_Resolves_To_Interactable()
    {
        Init();
        var failures = new List<string>();
        foreach (var node in GameMissionGraphData.GetAllNodes())
        {
            if (node.InteractId <= 0) continue;
            if (GameInteractableData.Get(node.InteractId) == null)
                failures.Add($"node {node.NodeId}({node.NodeKey}): interact_id {node.InteractId} 가 interactable_info에 없음");
        }

        Assert.True(failures.Count == 0, "끊어진 interact_id 참조:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void Node_ActionGroup_Resolves_To_ObjectActionGroup()
    {
        Init();
        var failures = new List<string>();
        foreach (var node in GameMissionGraphData.GetAllNodes())
        {
            if (string.IsNullOrWhiteSpace(node.ActionGroupKey)) continue;
            // 단서 액션 그룹이 object_action에 실제로 존재해야 함 (없으면 클릭 후 선택지가 안 뜸)
            if (GameInteractableData.GetActionGroup(node.ActionGroupKey).Count == 0)
                failures.Add($"node {node.NodeId}: action_group '{node.ActionGroupKey}' 가 object_action에 없음");
        }

        Assert.True(failures.Count == 0, "끊어진 action_group 참조:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void StoryletTags_AllRequired_AreReachable()
    {
        Init();
        var nodes = GameMissionGraphData.GetAllNodes();

        // 지급되는 태그: 시작점 자동 태그(record_case, start_{key}, stage_1) + 모든 노드의 ClueTags(grant) + FinalTags
        var granted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "record_case", "stage_1" };
        foreach (var start in GameMissionGraphData.GetStoryletStarts())
        {
            string key = (start.StartKey ?? "").Trim().ToLowerInvariant();
            if (key.Length > 0) granted.Add($"start_{key}");
        }
        foreach (var node in nodes)
        {
            foreach (var t in node.ClueTags) granted.Add(t);
            foreach (var t in node.FinalTags) granted.Add(t);
        }

        // 요구되는 태그
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            foreach (var t in node.RequiredAllTags) required.Add(t);
            foreach (var t in node.RequiredAnyTags) required.Add(t);
        }

        var orphans = required.Where(t => !granted.Contains(t)).OrderBy(t => t).ToList();
        Assert.True(orphans.Count == 0,
            "어디서도 지급되지 않는 필수 태그(도달 불가 단계):\n  " + string.Join(", ", orphans));
    }

    [Fact]
    public void NodeId_GloballyUnique_AcrossSources()
    {
        Init();
        // start/pool/graph_node(prep)는 별도 CSV지만 node_id 네임스페이스는 하나여야 한다.
        // (3파일 통합 대신, 충돌만 린트로 방지 — 관례상 3xxx/41xx/42xx 분리)
        var ids = GameMissionGraphData.GetStoryletStarts().Select(s => s.NodeId)
            .Concat(GameMissionGraphData.GetStoryletPoolItems().Select(p => p.NodeId))
            .Concat(GameMissionGraphData.GetAllNodes()
                .Where(n => n.NodeKind == MissionGraphNodeKind.CollectPart)
                .Select(n => n.NodeId))
            .ToList();

        var dups = ids.GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key).OrderBy(id => id).ToList();
        Assert.True(dups.Count == 0,
            "node_id 충돌(start/pool/prep 소스 간 중복): " + string.Join(", ", dups));
    }

    [Fact]
    public void FullBagStatusEffectAndCsvMirrorsAreValid()
    {
        Init();

        var effect = GameStatusEffectData.Get(1013);
        Assert.Equal(0, effect.BuffId);
        Assert.Equal("debuff", effect.Kind);
        Assert.Equal(12176, effect.NameTextId);
        Assert.Equal(12177, effect.DescTextId);
        Assert.Equal("가득찬 가방", GameSystemTextData.GetText(12176, "kr"));
        Assert.Equal("더 이상 아이템을 주울 수 없습니다.", GameSystemTextData.GetText(12177, "kr"));

        string networkRoot = FindNetworkBasePath();
        string repositoryRoot = Directory.GetParent(networkRoot)!.FullName;
        string networkCsvRoot = Path.Combine(networkRoot, "Common", "csv");
        foreach (string fileName in new[] { "status_effect_info.csv", "system_text.csv" })
        {
            byte[] canonical = File.ReadAllBytes(Path.Combine(networkCsvRoot, fileName));
            Assert.Equal(canonical, File.ReadAllBytes(Path.Combine(
                repositoryRoot, "client", "Assets", "Resources", "Common", "csv", fileName)));
            Assert.Equal(canonical, File.ReadAllBytes(Path.Combine(
                repositoryRoot, "client", "Assets", "StreamingAssets", "Common", "csv", fileName)));
        }
    }

    private static string FindNetworkBasePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "network", "Common", "csv");
            if (Directory.Exists(candidate))
                return Path.Combine(dir.FullName, "network");

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate network/Common/csv from test output path.");
    }
}
