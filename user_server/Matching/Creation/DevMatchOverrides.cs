using network.common;
using network.common.data.models;

namespace user_server.matching.creation;

/// <summary>
///     솔로 맵 검증 여부를 서버 시작 시 읽는다.
///     일반 매치는 설정된 정원을 사용하고, 솔로 검증은 봇 없는 1인 매치를 사용한다.
/// </summary>
internal sealed class DevMatchOverrides(bool soloMapValidation)
{
    internal const string SoloMapValidationVariable = "SOLO_MAP_VALIDATION";

    public bool IsSoloMapValidation { get; } = soloMapValidation;
    public MatchMode MatchMode => IsSoloMapValidation ? MatchMode.SoloMapValidation : MatchMode.Normal;
    public int GamePlayersPerMatch => IsSoloMapValidation ? 1 : Config.SWARM_PLAYERS_PER_MATCH;

    public static DevMatchOverrides FromEnvironment() =>
        new(Environment.GetEnvironmentVariable(SoloMapValidationVariable) == "1");
}
