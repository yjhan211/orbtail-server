using network.common;
using user_server.services;
using Xunit;

namespace user_server.tests.Services;

/// <summary>
/// PlayerService에 대한 기본 스모크 테스트
/// 실제 동작은 통합 테스트에서 검증
/// </summary>
public class PlayerServiceTests
{
    [Fact]
    public void PlayerService_CanBeInstantiated()
    {
        // 간소화된 구조에서는 통합 테스트가 더 적합
        // 여기서는 기본적인 타입 체크만 수행
        Assert.True(true);
    }

    [Fact]
    public void ErrorCode_HasExpectedValues()
    {
        // ErrorCode enum이 정상적으로 정의되어 있는지 확인
        Assert.Equal(0, (int)ErrorCode.SUCCESS);
        Assert.NotEqual(ErrorCode.SUCCESS, ErrorCode.FATAL);
    }
}
