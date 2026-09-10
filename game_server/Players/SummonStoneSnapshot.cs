namespace game_server.players;

/// <summary>클라이언트에 보내는 소환석 상태. 다음 비용은 성공한 소환 횟수로 정한다.</summary>
public readonly record struct SummonStoneSnapshot(int StoneCount, int SuccessfulSummonCount, int NextCost)
{
    public static SummonStoneSnapshot Empty => new(0, 0, PlayerOrbGrowthService.GetSummonCost(0));
}
