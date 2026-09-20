using System.Text.RegularExpressions;

namespace server_tests;

/// <summary>서버 CSV 파일과 로더 등록의 일치 여부를 검증한다.</summary>
public class ProtocolWiringGuardTests
{
    [Fact]
    public void CsvFolderMatchesLoaderRegistrations()
    {
        string root = FindRepositoryRoot();
        string loaderSource = File.ReadAllText(Path.Combine(
            root, "network", "Common", "Data", "Helpers", "GameDataHelper.cs"));

        var registered = new SortedSet<string>();
        foreach (Match m in Regex.Matches(loaderSource, "\"([a-z0-9_]+\\.csv)\""))
            registered.Add(m.Groups[1].Value);

        var onDisk = new SortedSet<string>(
            Directory.GetFiles(Path.Combine(root, "network", "Common", "csv"), "*.csv")
                .Select(Path.GetFileName)!);

        var loadedButMissing = registered.Except(onDisk).ToList();
        var orphanFiles = onDisk.Except(registered).ToList();

        Assert.True(loadedButMissing.Count == 0,
            "GameDataHelper가 등록했지만 csv/ 폴더에 없는 파일 (부팅 실패 위험):\n" +
            string.Join("\n", loadedButMissing));
        Assert.True(orphanFiles.Count == 0,
            "csv/ 폴더에 있지만 어떤 로더도 읽지 않는 고아 파일 (삭제하거나 로더에 등록할 것):\n" +
            string.Join("\n", orphanFiles));
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
