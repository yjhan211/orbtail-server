using System.IO;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;

namespace demo_regression_tests;

/// <summary>
///     GameMapData.Validate (#335 복원)가 현행 CSV를 통과하고, 저작 실수(스폰 셀 위 장애물)는 부팅 실패로 막는지 잠근다.
/// </summary>
public sealed class GameMapDataValidationTests
{
    private static void Init()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();
    }

    [Fact]
    public void CanonicalCsvPassesMatchMapValidation()
    {
        Init();

        GameMapData.Validate();
    }

    [Fact]
    public void ObstacleOnSpawnCellFailsValidation()
    {
        Init();

        MapId map = Config.SWARM_MATCH_MAP;
        Cell spawnCell = GameMapData.GetAreaSpawnCell(map, MatchSpawnData.GetPhaseRoomCandidates()[0]);
        try
        {
            GameMapData.SetRuntimeObstacles(map, [new UnityEngine.Vector3Int(spawnCell.X, spawnCell.Y, 0)]);

            // 스폰 셀 조회(GetAreaSpawnCell)는 막힌 중심을 피해 다른 셀을 고르므로, 고정 앵커 검사가 잡는다.
            var error = Assert.Throws<InvalidDataException>(GameMapData.Validate);
            Assert.Contains("걸을 수 없는 자리", error.Message);
        }
        finally
        {
            GameMapData.ClearRuntimeObstacles(map);
        }
    }

    [Fact]
    public void UndefinedEnumValueIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => CsvHelper.ParseDefinedEnum<AreaType>("100", "test"));
        Assert.Throws<InvalidDataException>(() => CsvHelper.ParseDefinedEnum<MapId>("abc", "test"));
        Assert.Equal(AreaType.S2Classroom1, CsvHelper.ParseDefinedEnum<AreaType>("50", "test"));
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
