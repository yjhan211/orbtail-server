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
}
