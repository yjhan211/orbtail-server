using network.common.data;

namespace game_server.services;

internal static class MissionPartSelection
{
    private const int TornAttendancePagePartId = 308;
    private const int BlackedOutPaperPartId = 309;

    public static MissionPartData? SelectNextCollectablePart(
        IReadOnlyList<MissionPartData> matchingParts,
        PlayerPartState? state)
    {
        if (matchingParts == null || matchingParts.Count == 0)
            return null;

        return matchingParts
            .Where(part => state == null || !state.CollectedParts.Contains(part.PartId))
            .OrderBy(GetCollectPriority)
            .ThenBy(part => part.PartId)
            .FirstOrDefault();
    }

    private static int GetCollectPriority(MissionPartData part)
    {
        // 책상은 여러 시작 Storylet 재료가 겹친다. 출석부 루트의 단서가 먼저 보이도록 우선한다.
        if (part.PartId == TornAttendancePagePartId) return -100;
        if (part.PartId == BlackedOutPaperPartId) return -90;

        return 0;
    }
}
