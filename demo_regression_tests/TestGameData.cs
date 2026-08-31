using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

/// <summary>
///     #292 이후 OrbData의 색/티어·전투 계수 조회는 battle_item_combat.csv 로드에 의존한다.
///     정적 로더 상태는 테스트 어셈블리 전체에서 공유되므로, OrbData를 쓰는 테스트 클래스는
///     실행 순서에 기대지 말고 생성자에서 이 헬퍼를 호출해 결정적으로 로드한다 (#304에서 순서 의존 발견).
/// </summary>
public static class TestGameData
{
    private static readonly object _lock = new();
    private static bool _battleItemCombatLoaded;

    public static void EnsureBattleItemCombatLoaded()
    {
        lock (_lock)
        {
            if (_battleItemCombatLoaded) return;

            string csvRoot = Path.Combine(FindRepositoryRoot(), "network", "Common", "csv");
            BattleItemCombatData.Initialize(
                CsvHelper.LoadCsv(Path.Combine(csvRoot, "battle_item_combat.csv")));
            _battleItemCombatLoaded = true;
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
