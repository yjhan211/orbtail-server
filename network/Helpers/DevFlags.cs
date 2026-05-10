namespace network.helpers;

/// <summary>
///     개발/디버그용 환경 변수 플래그. 빌드 일시 차단/조정용.
/// </summary>
public static class DevFlags
{
    /// <summary>
    ///     모든 게임 종료 트리거 차단 (race 완주, 봇 race 완주, 시간 만료, 인스턴스 정리).
    ///     RNG 채집 시스템 시연/테스트 동안 수동 종료만 허용.
    ///     활성화: 환경 변수 <c>DISABLE_GAME_END=1</c>
    /// </summary>
    public static bool DisableGameEnd => Environment.GetEnvironmentVariable("DISABLE_GAME_END") == "1";

    /// <summary>
    ///     2인 테스트 매칭 활성화. 첫 번째 실제 플레이어 + 두 번째 실제 플레이어 + 봇 3명으로 체인을 고정한다.
    ///     활성화: 환경 변수 <c>TEST_TWO_PLAYER_MATCH=1</c>
    /// </summary>
    public static bool TestTwoPlayerMatch => Environment.GetEnvironmentVariable("TEST_TWO_PLAYER_MATCH") == "1";
}
