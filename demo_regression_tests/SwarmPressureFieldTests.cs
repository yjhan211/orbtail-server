using game_server.services;
using network.common;
using network.common.data;
using network.common.data.helpers;

namespace demo_regression_tests;

// #217 자기장: 보행 거리 필드가 깔때기 순서(시작방 > 쌍 구역 > 운동장)를 재현하는지 고정한다.
public class SwarmPressureFieldTests
{
    public SwarmPressureFieldTests()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();
    }

    [Fact]
    public void Ground_IsZeroAndMaxDistanceIsSane()
    {
        Assert.Equal(0, SwarmPressureField.GetAreaMinDistance(AreaType.Ground));
        Assert.InRange(SwarmPressureField.MaxDistance, 10, 1000);
    }

    [Fact(Skip = "#219 클론 맵 전환: 옛 학교 지형 전제 — 클론 데이터 스택(벽·연결·문) 완성 후 재작성")]
    public void FunnelOrder_StartRoomsFartherThanTheirPairZones()
    {
        AssertFarther(AreaType.ExamRoom, AreaType.Library);
        AssertFarther(AreaType.Storage, AreaType.Library);
        AssertFarther(AreaType.Classroom2, AreaType.Gym);
        AssertFarther(AreaType.Storage2, AreaType.Gym);
        AssertFarther(AreaType.AdminOffice, AreaType.Corridor);
        AssertFarther(AreaType.StaffRoom, AreaType.Corridor);
    }

    [Fact(Skip = "#219 클론 맵 전환: 옛 학교 지형 전제 — 클론 데이터 스택(벽·연결·문) 완성 후 재작성")]
    public void PairZones_FartherThanCorridorTier()
    {
        // 쌍 구역은 복도보다 바깥 — 수축이 시작방 → 쌍 구역 → 복도 순서로 스친다.
        AssertFarther(AreaType.Library, AreaType.Corridor);
        AssertFarther(AreaType.Gym, AreaType.Corridor);
    }

    [Fact(Skip = "#219 클론 맵 전환: 옛 학교 지형 전제 — 클론 데이터 스택(벽·연결·문) 완성 후 재작성")]
    public void LockedBackDoors_DoNotShortenDistances()
    {
        // 서쪽창고는 운동장 직행 문(112)이 잠겨 있어 도서관 경유가 강제된다.
        // 문이 벽 취급되지 않으면 창고가 도서관보다 가까워져 깔때기가 깨진다.
        Assert.True(
            SwarmPressureField.GetAreaMinDistance(AreaType.Storage) >
            SwarmPressureField.GetAreaMinDistance(AreaType.Library));
    }

    private static void AssertFarther(AreaType outer, AreaType inner)
    {
        int outerDistance = SwarmPressureField.GetAreaMinDistance(outer);
        int innerDistance = SwarmPressureField.GetAreaMinDistance(inner);
        Assert.True(outerDistance > innerDistance,
            $"{outer}({outerDistance}) 가 {inner}({innerDistance}) 보다 멀어야 한다");
        Assert.True(outerDistance < int.MaxValue, $"{outer} 도달 불가");
    }

    private static string FindNetworkBasePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "network", "Common", "csv");
            if (Directory.Exists(candidate))
                return Path.Combine(directory.FullName, "network");
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate network/Common/csv.");
    }
}
