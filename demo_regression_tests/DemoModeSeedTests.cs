using network.common;
using network.helpers;

namespace demo_regression_tests;

/// <summary>
///     W10 — DemoMode 결정론 시드 회귀 테스트.
///     동일 시드(20260511)로 RNG / 동선 스크립트 / 체인 직책 배치 재현 가능성 검증.
///     영상 7대 모먼트(02:50/04:00/04:50/06:40/09:40/10:35/13:00) 시간 정합 확인.
/// </summary>
public class DemoModeSeedTests
{
    [Fact]
    public void Seed_Constant_Stable()
    {
        Assert.Equal(20260511, DemoMode.Seed);
    }

    [Fact]
    public void RandomFromSeed_Repeatable()
    {
        var rng1 = new Random(DemoMode.Seed);
        var rng2 = new Random(DemoMode.Seed);

        for (int i = 0; i < 100; i++)
            Assert.Equal(rng1.Next(1000), rng2.Next(1000));
    }

    [Fact]
    public void ChainJobOrder_PlayerSlot_Matches_DemoPlayerJob()
    {
        Assert.Equal(JobTitle.NONE, DemoMode.PlayerJob);
        Assert.Equal(DemoMode.PlayerJob, DemoMode.ChainJobOrder[DemoMode.PlayerChainIndex]);
    }

    [Fact]
    public void ChainJobOrder_BotSlots_BR_DC_SC_HE()
    {
        Assert.Equal(JobTitle.BROADCAST_MEMBER, DemoMode.ChainJobOrder[0]);
        Assert.Equal(JobTitle.DISCIPLINE_MEMBER, DemoMode.ChainJobOrder[2]);
        Assert.Equal(JobTitle.SCIENCE_MEMBER, DemoMode.ChainJobOrder[3]);
        Assert.Equal(JobTitle.HEALTH_MEMBER, DemoMode.ChainJobOrder[4]);
    }

    [Fact]
    public void ChainJobOrder_Length_Matches_MatchPlayerCount()
    {
        Assert.Equal(DemoMode.MatchPlayerCount, DemoMode.ChainJobOrder.Length);
    }

    [Theory]
    [InlineData(JobTitle.BROADCAST_MEMBER, 0, AreaType.BroadcastRoom)]
    [InlineData(JobTitle.BROADCAST_MEMBER, 360, AreaType.Library)]
    [InlineData(JobTitle.BROADCAST_MEMBER, 570, AreaType.Library)]
    [InlineData(JobTitle.DISCIPLINE_MEMBER, 0, AreaType.Corridor1F)]
    [InlineData(JobTitle.DISCIPLINE_MEMBER, 270, AreaType.Library)]
    [InlineData(JobTitle.SCIENCE_MEMBER, 0, AreaType.ExamRoom)]
    [InlineData(JobTitle.SCIENCE_MEMBER, 170, AreaType.Library)]
    [InlineData(JobTitle.SCIENCE_MEMBER, 300, AreaType.ExamRoom)]
    // HEALTH_MEMBER는 BotMovementScript에서 폐기 — 직책 큐 자연 walking으로 전환됨 (#134).
    public void BotMovementScript_AreaAtTime(JobTitle job, int elapsedSec, AreaType expected)
    {
        var script = DemoMode.BotMovementScript[job];
        var area = script[0].area;
        foreach (var (sec, a) in script)
        {
            if (sec > elapsedSec) break;
            area = a;
        }
        Assert.Equal(expected, area);
    }

    [Fact]
    public void BotMovementScript_WaypointsSorted_Ascending()
    {
        foreach (var (job, script) in DemoMode.BotMovementScript)
        {
            for (int i = 1; i < script.Count; i++)
                Assert.True(script[i].elapsedSec > script[i - 1].elapsedSec,
                    $"{job} script must be ascending in elapsedSec; mismatch at index {i}");
        }
    }

    [Fact]
    public void ScDetectionAttempt_AlignsWith_06_40_VideoBeat()
    {
        // 영상 7대 비트 4번째: 06:40 SC 색출 시도
        Assert.Equal(400, DemoMode.ScDetectionAttemptSeconds);
    }

    [Fact]
    public void HeForcedElimination_AlignsWith_12_00_NarrativeBeat()
    {
        // 영상 narrative: 12:00 HE 시한부 강제 탈락
        Assert.Equal(720, DemoMode.HeForcedEliminationSeconds);
    }

    [Fact]
    public void ProtectedAreas_Library_And_Classroom2()
    {
        Assert.Contains(AreaType.Library, DemoMode.ProtectedAreas);
        Assert.Contains(AreaType.Classroom2, DemoMode.ProtectedAreas);
    }

    [Fact]
    public void ForcedClosureSequence_DoesNotIntersect_ProtectedAreas()
    {
        // ProtectedAreas는 시퀀스에 명시적으로 포함되어도 AreaClosureManager에서 필터되지만
        // 정의 단계에서 충돌 안 하는 것이 의도. 회귀 방지.
        foreach (var area in DemoMode.ForcedClosureSequence)
            Assert.DoesNotContain(area, DemoMode.ProtectedAreas);
    }

    [Fact]
    public void BotRaceMin_AlignsWith_07_00_PaceCap()
    {
        // H4: 봇 race 페이스 캡 (시연자 race 보장)
        Assert.Equal(420, DemoMode.BotRaceMinSeconds);
    }

    [Fact]
    public void IsActive_Reflects_DEMO_MODE_LB_EnvVar()
    {
        var original = Environment.GetEnvironmentVariable("DEMO_MODE");
        try
        {
            Environment.SetEnvironmentVariable("DEMO_MODE", "LB");
            Assert.True(DemoMode.IsActive);

            Environment.SetEnvironmentVariable("DEMO_MODE", "");
            Assert.False(DemoMode.IsActive);

            Environment.SetEnvironmentVariable("DEMO_MODE", "OTHER");
            Assert.False(DemoMode.IsActive);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEMO_MODE", original);
        }
    }
}
