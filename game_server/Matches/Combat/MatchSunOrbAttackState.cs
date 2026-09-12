using game_server.players.bots;

namespace game_server.matches.combat;

/// <summary>
///     매치 하나의 태양 교차사격 상태. 진행 중인 직선 모양 목록과 봇 회피용 스냅샷을 든다.
///     MatchRuntime이 소유하며 SunOrbAttackService·PlayerOrbService가 매치 잠금 안에서 갱신한다. 화상은 피해자 Player가 든다.
/// </summary>
public sealed class MatchSunOrbAttackState
{
    public List<SwarmCrossfireShape> Shapes { get; } = new();

    public IReadOnlyList<SwarmBotDodgePolicy.SwarmCrossfireDodgeThreat> DodgeSnapshot { get; internal set; } = [];
}
