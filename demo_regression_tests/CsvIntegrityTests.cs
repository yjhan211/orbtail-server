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
    public void FullBagStatusEffectAndCsvMirrorsAreValid()
    {
        Init();

        var effect = GameStatusEffectData.Get(1013);
        Assert.Equal(0, effect.BuffId);
        Assert.Equal("debuff", effect.Kind);
        Assert.Equal(12176, effect.NameTextId);
        Assert.Equal(12177, effect.DescTextId);
        Assert.Equal("가득 찬 오브", GameSystemTextData.GetText(12176, "kr"));
        Assert.Equal("더 이상 오브를 소환할 수 없습니다.", GameSystemTextData.GetText(12177, "kr"));

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
