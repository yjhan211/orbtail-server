using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using game_server.matches.monsters;
using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

/// <summary>
///     밸런스 원천 = CSV (#292·#296·#335). 코드 폴백 기본값은 CSV가 없을 때(테스트·부트스트랩 초기)만
///     쓰이는 안전망이라 CSV 값과 같아야 한다 — 어긋나면 "CSV를 고쳤는데 테스트는 옛값으로 도는" 조용한
///     드리프트가 생긴다. 여기서 스칼라 키 전부와 디렉터 페이즈 곡선을 잠근다.
/// </summary>
public class SwarmConfigFallbackTests
{
    private static readonly Regex ScalarCall = new(
        @"SwarmConfigData\.Get(?:Int|Float|Double)\(\s*""(?<key>[A-Z0-9_]+)""\s*,\s*(?<value>[0-9.]+[fd]?(?:\s*/\s*[0-9.]+[fd]?)?)\s*\)",
        RegexOptions.Compiled);

    [Fact]
    public void ScalarFallbacks_MatchSwarmConfigCsv()
    {
        string root = FindRepositoryRoot();
        var csv = ReadSwarmConfigCsv(root);

        var mismatches = new List<string>();
        int checkedCount = 0;
        foreach (string file in EnumerateSources(root, "game_server", "network", "user_server"))
        {
            string source = File.ReadAllText(file);
            foreach (Match match in ScalarCall.Matches(source))
            {
                string key = match.Groups["key"].Value;
                double fallback = ParseCodeNumber(match.Groups["value"].Value);
                if (!csv.TryGetValue(key, out string? csvText))
                {
                    mismatches.Add($"{key}: swarm_config.csv에 행이 없다 ({Path.GetFileName(file)})");
                    continue;
                }

                double csvValue = double.Parse(csvText, CultureInfo.InvariantCulture);
                checkedCount++;
                if (Math.Abs(csvValue - fallback) > 1e-6 * Math.Max(1d, Math.Abs(csvValue)))
                    mismatches.Add($"{key}: csv={csvText} code={match.Groups["value"].Value} ({Path.GetFileName(file)})");
            }
        }

        Assert.True(checkedCount >= 50, $"SwarmConfigData 스칼라 호출을 {checkedCount}건만 찾았다 — 정규식이 코드 형태를 놓쳤다");
        Assert.True(mismatches.Count == 0,
            "코드 폴백 기본값이 swarm_config.csv와 다르다 (원천은 CSV — 둘을 같게 맞출 것):\n" +
            string.Join("\n", mismatches));
    }

    [Fact]
    public void SupplyPhaseCsv_MatchesDirectorFallbackCurve()
    {
        string root = FindRepositoryRoot();
        var rows = CsvHelper.LoadCsv(Path.Combine(root, "network", "Common", "csv", "swarm_supply_phase.csv"));
        var fromCsv = rows.Select(SwarmSupplyPhaseDefinition.CreateFromData)
            .OrderBy(phase => phase.PhaseIndex)
            .ToList();

        var field = typeof(SwarmMonsterDirector).GetField(
            "DefaultSupplyPhases", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var fallback = (SwarmSupplyPhaseDefinition[])field!.GetValue(null)!;

        Assert.Equal(fallback.Length, fromCsv.Count);
        for (int index = 0; index < fallback.Length; index++)
        {
            var expected = fallback[index];
            var actual = fromCsv[index];
            Assert.Equal(expected.PhaseIndex, actual.PhaseIndex);
            Assert.Equal(expected.UntilSeconds, actual.UntilSeconds);
            Assert.Equal(expected.PerPlayerTarget, actual.PerPlayerTarget);
            Assert.Equal(expected.NormalHp, actual.NormalHp);
            Assert.Equal(expected.ContactDamage, actual.ContactDamage);
            Assert.Equal(expected.CoreHp, actual.CoreHp);
            Assert.Equal(expected.StoneBudget, actual.StoneBudget);
        }

        // 마지막 페이즈만 무한 — 선형 탐색(GetSupplyPhaseIndex)의 전제.
        Assert.True(fromCsv[^1].IsFinal);
        Assert.All(fromCsv.SkipLast(1), phase => Assert.False(phase.IsFinal));
    }

    private static double ParseCodeNumber(string text)
    {
        string[] parts = text.Split('/');
        double value = ParseLiteral(parts[0]);
        for (int index = 1; index < parts.Length; index++)
            value /= ParseLiteral(parts[index]);
        return value;
    }

    private static double ParseLiteral(string text) =>
        double.Parse(text.Trim().TrimEnd('f', 'd'), CultureInfo.InvariantCulture);

    private static Dictionary<string, string> ReadSwarmConfigCsv(string root)
    {
        var values = new Dictionary<string, string>();
        foreach (string line in File.ReadLines(Path.Combine(root, "network", "Common", "csv", "swarm_config.csv")).Skip(1))
        {
            string[] parts = line.Split(',');
            if (parts.Length >= 2 && parts[0].Length > 0)
                values[parts[0]] = parts[1].Trim();
        }

        return values;
    }

    private static IEnumerable<string> EnumerateSources(string root, params string[] projects)
    {
        foreach (string project in projects)
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                yield return file;
            }
        }
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
