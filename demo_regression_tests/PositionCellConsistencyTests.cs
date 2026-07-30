using System.Reflection;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class PositionCellConsistencyTests
{
    public PositionCellConsistencyTests()
    {
        GameDataHelper.SetBasePath(FindNetworkBasePath());
        GameDataHelper.Initialize();
    }

    [Theory]
    [InlineData(139, 93)]
    [InlineData(144, 92)]
    [InlineData(173, 90)]
    public void GameObjectInfo_UpdateCellFromPosition_RoundTripsSchoolNewGrid(int cellX, int cellY)
    {
        var info = new GameObjectInfo
        {
            MapId = MapId.School,
            Position = MapCoordinateConverter.CellToWorld(MapId.School, new Cell(cellX, cellY))
        };

        info.UpdateCellFromPosition();

        Assert.Equal(cellX, info.Cell.X);
        Assert.Equal(cellY, info.Cell.Y);
    }

    [Fact]
    public void PlayerInfo_CachedCellConversion_UsesSchoolNewGridOrigin()
    {
        var method = typeof(PlayerInfo).GetMethod(
            "CellToWorldPosition",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var world = Assert.IsType<Vector3f>(method!.Invoke(null, new object[] { MapId.School, new Cell(139, 93) }));

        Assert.Equal(23f, world.X, 3);
        Assert.Equal(58.25f, world.Y, 3);
    }

    [Fact]
    public void SchoolNewGridOrigin_UsesZeroOffset()
    {
        var cell = MapCoordinateConverter.WorldToCell(
            MapId.School,
            new Vector3f(57.89f, 68.61f, 0f));

        Assert.Equal(195, cell.X);
        Assert.Equal(79, cell.Y);
    }

    private static string FindNetworkBasePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, "network", "Common", "csv");
            if (Directory.Exists(candidate))
                return Path.Combine(directory.FullName, "network");
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate network/Common/csv.");
    }
}
