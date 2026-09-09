using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches.states;

/// <summary>
///     계열별 오브 강화 구매 횟수. 비용 계산이 읽는 매치 로컬 누적값만 소유하고, 소환석 소비와
///     인벤토리 교체는 GameServer의 기존 흐름에 남긴다.
/// </summary>
public sealed class OrbUpgradeState
{
    private readonly Dictionary<(long PlayerId, OrbColor Color), int> _familyUpgradeCounts = new();

    public int GetFamilyUpgradeCount(long playerId, OrbColor color) =>
        _familyUpgradeCounts.TryGetValue((playerId, color), out int count) ? count : 0;

    public int IncrementFamilyUpgradeCount(long playerId, OrbColor color)
    {
        int count = GetFamilyUpgradeCount(playerId, color) + 1;
        _familyUpgradeCounts[(playerId, color)] = count;
        return count;
    }
}
