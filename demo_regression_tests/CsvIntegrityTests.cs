using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

/// <summary>
///     CSV 간 참조 무결성(게임 내장 검증기가 안 잡는 부분): 상태이상 행이 가리키는 텍스트 ID가 실제 문구로 이어지는지 잠근다.
/// </summary>
public class CsvIntegrityTests
{
    private static void Init()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();
    }

    [Fact]
    public void FullBagStatusEffectRowIsValid()
    {
        Init();

        var effect = GameStatusEffectData.Get(1013);
        Assert.Equal(0, effect.BuffId);
        Assert.Equal("debuff", effect.Kind);
        Assert.Equal(12176, effect.NameTextId);
        Assert.Equal(12177, effect.DescTextId);
        Assert.Equal("가득 찬 오브", GameSystemTextData.GetText(12176, "kr"));
        Assert.Equal("더 이상 오브를 소환할 수 없습니다.", GameSystemTextData.GetText(12177, "kr"));
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
