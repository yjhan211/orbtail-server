using System.Reflection;
using network.common.data.models;

namespace demo_regression_tests;

public sealed class PositionCellConsistencyTests
{
    [Theory]
    [InlineData(139, 93)]
    [InlineData(144, 92)]
    [InlineData(173, 90)]
    public void GameObjectInfo_UpdateCellFromPosition_RoundTripsIsometricCellCenter(int cellX, int cellY)
    {
        var info = new GameObjectInfo
        {
            Position = CellCenterToWorld(cellX, cellY)
        };

        info.UpdateCellFromPosition();

        Assert.Equal(cellX, info.Cell.X);
        Assert.Equal(cellY, info.Cell.Y);
    }

    [Fact]
    public void PlayerInfo_CachedCellConversion_UsesSameCoordinatesAsGameServer()
    {
        var method = typeof(PlayerInfo).GetMethod(
            "CellToWorldPosition",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var world = Assert.IsType<Vector3f>(method!.Invoke(null, new object[] { new Cell(139, 93) }));

        Assert.Equal(23f, world.X, 3);
        Assert.Equal(58.25f, world.Y, 3);
    }

    private static Vector3f CellCenterToWorld(int cellX, int cellY)
    {
        return new Vector3f(
            (cellX - cellY) / 2f,
            (cellX + cellY) / 4f + 0.25f,
            0f);
    }
}
