using System.Reflection;
using System.Text.RegularExpressions;
using game_server;

namespace demo_regression_tests;

// #227 M2 피해 경로 고정: 두 공격이 서로 다른 것을 깎아야 "죽이러 갈지 / 무장 해제하러 갈지"가
// 선택이 된다. 원거리(미사일·물폭탄·잔상·폐쇄)는 본체 HP만, 전투 오브 내구는 몸으로 가로지르는
// 절단만 깎는다. 이 경계는 코드 몇 줄로 조용히 무너질 수 있어 여기서 잠근다.
public class SwarmDamagePathTests
{
    /// <summary>
    ///     오브 HP 경로(몹 접촉이 앞줄 오브를 깎던 구형 모델)는 꺼진 채로 유지한다.
    ///     이 플래그가 켜지면 원거리·접촉 피해가 다시 오브를 깎아 두 경로가 한 축으로 합쳐진다.
    /// </summary>
    [Fact]
    public void SwarmOrbHealth_StaysDisabled()
    {
        var field = typeof(GameServer).GetField(
            "SwarmOrbHealthEnabled", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);

        var value = field!.GetValue(null);
        Assert.False(
            (bool)value!,
            "SwarmOrbHealthEnabled가 켜졌다 — 원거리 피해가 전투 오브를 깎으면 " +
            "본체 HP와 절단 내구의 역할 분리가 무너진다 (#227 M2).");
    }

    /// <summary>
    ///     절단 내구(_swarmOrbCutCracks)에 값을 쓰는 곳은 절단 판정 하나뿐이어야 한다.
    ///     제거(Remove)는 파괴·정리 경로라 대상이 아니고, 증가 대입만 센다.
    /// </summary>
    [Fact]
    public void OrbCutDurability_IsWrittenOnlyByTrailCut()
    {
        string source = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "game_server", "GameServer.SwarmArena.cs"));

        var writes = Regex.Matches(source, @"_swarmOrbCutCracks\[[^\]]+\]\s*=");
        Assert.True(
            writes.Count == 1,
            $"절단 내구 대입 지점이 {writes.Count}곳이다 — 절단 외의 경로가 오브 내구를 깎으면 " +
            "원거리 공격으로 크랙이 생겨 두 피해 경로가 뒤섞인다 (#227 M2).");

        // 그 한 곳이 실제로 절단 판정 안인지 확인한다.
        int cutMethodStart = source.IndexOf("private void TryPerformSwarmTrailCut(", StringComparison.Ordinal);
        Assert.True(cutMethodStart >= 0, "TryPerformSwarmTrailCut를 찾지 못했다");
        Assert.True(
            writes[0].Index > cutMethodStart,
            "절단 내구 대입이 절단 판정 밖에 있다 (#227 M2).");
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "game_server")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("repository root not found");
    }
}
