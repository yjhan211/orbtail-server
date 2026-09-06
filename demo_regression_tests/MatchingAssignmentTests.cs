using user_server.sessions;

namespace demo_regression_tests;

/// <summary>세션 매칭 상태 기계의 전이 규칙. 연결·Redis 없이 순수 상태만 잠근다.</summary>
public sealed class MatchingAssignmentTests
{
    [Fact]
    public void Begin_Assign_Take_RoundTrip()
    {
        var state = new MatchingAssignment();

        Assert.True(state.TryBegin(out string? requestId, out long blocking));
        Assert.NotNull(requestId);
        Assert.Equal(0, blocking);
        Assert.False(state.TryBegin(out _, out blocking)); // 요청 중
        Assert.Equal(0, blocking);

        Assert.True(state.TryAssign(42, requestId!));
        Assert.False(state.TryBegin(out _, out blocking)); // 배정됨
        Assert.Equal(42, blocking);

        Assert.Equal(42, state.TakeAndClear());
        Assert.Null(state.ActiveRequestId);
        Assert.True(state.TryBegin(out _, out _));
    }

    [Fact]
    public void Assign_RejectsWrongRequestOrOtherMatch()
    {
        var state = new MatchingAssignment();
        state.TryBegin(out string? requestId, out _);

        Assert.False(state.TryAssign(42, "not-the-request"));
        Assert.False(state.TryAssign(0, requestId!));
        Assert.True(state.TryAssign(42, requestId!));
        Assert.False(state.TryAssign(43, requestId!)); // 다른 매치로 덮어쓰기 금지
        Assert.True(state.TryAssign(42, requestId!));  // 같은 매치 재확인은 허용
    }

    [Fact]
    public void ReplaceStale_OnlyWhenNothingChangedMeanwhile()
    {
        var state = new MatchingAssignment();
        state.TryBegin(out string? first, out _);
        state.TryAssign(42, first!);

        // 그사이 다른 경로가 배정을 지우고 새 요청을 열었다 — 낡은 복구는 지지 않는다
        state.Clear(42);
        state.TryBegin(out string? concurrent, out _);
        Assert.Null(state.TryRestart(42));
        Assert.Equal(concurrent, state.ActiveRequestId);

        // 정상 경로: 배정 그대로면 교체된다
        var fresh = new MatchingAssignment();
        fresh.TryBegin(out string? old, out _);
        fresh.TryAssign(7, old!);
        string? replaced = fresh.TryRestart(7);
        Assert.NotNull(replaced);
        Assert.NotEqual(old, replaced);
    }

    [Fact]
    public void FailRequest_And_FailEntry_ClearOnlyMatchingState()
    {
        var state = new MatchingAssignment();
        state.TryBegin(out string? requestId, out _);
        state.TryAssign(42, requestId!);

        Assert.False(state.FailEntry(99));
        Assert.True(state.FailEntry(42));
        Assert.Null(state.ActiveRequestId);

        state.TryBegin(out requestId, out _);
        Assert.False(state.FailRequest("other", 0));
        Assert.True(state.FailRequest(requestId!, 0));
        Assert.Null(state.ActiveRequestId);
    }
}
