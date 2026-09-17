namespace server_tests;

internal static class TestTime
{
    // 테스트가 밀리초 숫자로 쓰던 시각을 서버가 받는 DateTime으로 바꾼다. 기준점은 의미가 없고 간격만 쓴다.
    public static DateTime Ms(long milliseconds) => DateTime.UnixEpoch.AddMilliseconds(milliseconds);
}
