using network.common.data.models;

namespace demo_regression_tests;

public sealed class MovementCalculationTests
{
    [Theory]
    [InlineData(2.5f, 1.5f, 2f)]
    [InlineData(5f, 3f, 4f)]
    [InlineData(10f, 3f, 4f)]
    [InlineData(0f, 0f, 0f)]
    public void MovesInXYWithoutOvershooting(float budget, float expectedX, float expectedY)
    {
        var start = new Vector3f(0, 0, 0);
        var target = new Vector3f(3, 4, 0);
        var result = Vector3f.MoveTowardsXY(start, target, budget);
        Assert.Equal(expectedX, result.X);
        Assert.Equal(expectedY, result.Y);
        Assert.Equal(0f, start.X);
        Assert.NotSame(start, result);
        Assert.NotSame(target, result);
    }

    [Fact]
    public void SamePositionDoesNotDivideByZero()
    {
        var position = new Vector3f(3, 4, 0);
        Assert.Equal(position, Vector3f.MoveTowardsXY(position, position, 5));
    }
}
