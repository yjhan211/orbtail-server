using network.common.data.helpers;
using network.common.data.models;

namespace demo_regression_tests;

public class GridMovementTraversalTests
{
    [Fact]
    public void CrossingBlockedIntermediateCellIsRejected()
    {
        var canMove = GridMovementTraversal.IsTraversable(
            new Cell(0, 0), new Cell(2, 0), cell => cell.X != 1);

        Assert.False(canMove);
    }

    [Fact]
    public void DiagonalCornerCutIsRejectedWhenASideCellIsBlocked()
    {
        var canMove = GridMovementTraversal.IsTraversable(
            new Cell(0, 0), new Cell(1, 1), cell => !(cell.X == 1 && cell.Y == 0));

        Assert.False(canMove);
    }

    [Fact]
    public void LongTraversalAcceptsOpenCells()
    {
        var canMove = GridMovementTraversal.IsTraversable(
            new Cell(0, 0), new Cell(5, -2), _ => true);

        Assert.True(canMove);
    }

    [Fact]
    public void TransitionPolicyCanRejectAnOtherwiseOpenRoute()
    {
        var canMove = GridMovementTraversal.IsTraversable(
            new Cell(0, 0), new Cell(2, 0),
            _ => true,
            (from, to) => !(from.X == 0 && to.X == 1));

        Assert.False(canMove);
    }
}
