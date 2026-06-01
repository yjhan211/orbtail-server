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
