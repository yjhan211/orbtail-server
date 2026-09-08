using game_server.matches;
using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.services;

/// <summary>
///     매치의 오브 꼬리 경로에서 좌표를 계산하고 지정 순번 이후의 오브를 삭제한다.
///     전투와 구역 폐쇄가 같은 인벤토리 순서·좌표 규칙을 사용한다.
///     경로와 인벤토리는 매치가 소유하며 호출자는 매치 잠금을 보유한다.
/// </summary>
internal sealed class OrbTrailService(MatchRuntimeStore matchRuntimes)
{
    public int CountSwarmSquadOrbs(long matchingId, long playerId)
    {
        return matchRuntimes.GetRequired(matchingId).Inventory.GetPlayerInventory(playerId)
            .GetAllItems()
            .Where(item => item.Count > 0 && GetSquadOrbTier(item.ItemId) > 0)
            .Sum(item => item.Count);
    }

    internal const float CutFlashRadius = 0.7f;
    internal const int CutVfxKind = 1;
    public List<int> GetSwarmOrbTiersInOrder(long matchingId, long playerId)
    {
        return matchRuntimes.GetRequired(matchingId).Inventory.GetPlayerInventory(playerId)
            .GetAllItems()
            .Where(item => item.Count > 0 && GetSquadOrbTier(item.ItemId) > 0)
            .OrderBy(item => item.ItemUid)
            .Select(item => GetSquadOrbTier(item.ItemId))
            .ToList();
    }

    public Vector3f GetSwarmOrbTrailPosition(
        long matchingId, long playerId, int ordinal, Vector3f anchor,
        IReadOnlyList<int>? orderedTiers = null)
    {
        // 호출부가 목록을 들고 있으면 그걸 쓴다 — 순번마다 인벤토리를 다시 훑지 않게.
        float targetDistance = OrbData.GetSwarmTrailDistance(
            orderedTiers ?? GetSwarmOrbTiersInOrder(matchingId, playerId), ordinal);
        return GetSwarmTrailPositionAtDistance(matchingId, playerId, targetDistance, anchor);
    }

    /// <summary>경로를 지정 거리만큼 거슬러 올라간 지점 — 오브 열 좌표와 소용돌이 스폰이 공용.</summary>
    public Vector3f GetSwarmTrailPositionAtDistance(
        long matchingId, long playerId, float targetDistance, Vector3f anchor)
    {
        if (!matchRuntimes.GetRequired(matchingId).Swarm.TrailCombat.OrbTrails.TryGetValue((matchingId, playerId), out var points) || points.Count == 0)
            return new Vector3f(anchor.X, anchor.Y - targetDistance * 0.2f, 0f);

        Vector3f previous = anchor;
        float accumulated = 0f;
        // 호출자가 매치 잠금을 보유하므로 경로 갱신과 겹치지 않는다.
        for (int index = 0; index < points.Count; index++)
        {
            var point = points[index];
            float segment = Vector3f.Distance(previous, point);
            if (segment > 0.0001f && accumulated + segment >= targetDistance)
            {
                float t = (targetDistance - accumulated) / segment;
                return new Vector3f(
                    previous.X + (point.X - previous.X) * t,
                    previous.Y + (point.Y - previous.Y) * t,
                    0f);
            }

            accumulated += segment;
            previous = point;
        }

        Vector3f tailDirection = new(0f, -0.5f, 0f);
        if (points.Count >= 2)
        {
            var last = points[^1];
            var beforeLast = points[^2];
            float dx = last.X - beforeLast.X;
            float dy = last.Y - beforeLast.Y;
            float length = MathF.Sqrt(dx * dx + dy * dy);
            if (length > 0.0001f) tailDirection = new Vector3f(dx / length, dy / length, 0f);
        }

        float remaining = targetDistance - accumulated;
        return new Vector3f(
            previous.X + tailDirection.X * remaining,
            previous.Y + tailDirection.Y * remaining,
            0f);
    }

    public List<InGameItemInfo> DestroySwarmOrbsFromOrdinal(long matchingId, long playerId, int fromOrdinal)
    {
        var destroyed = new List<InGameItemInfo>();
        var inventory = matchRuntimes.GetRequired(matchingId).Inventory.GetPlayerInventory(playerId);
        var orbs = inventory.GetAllItems()
            .Where(item => item.Count > 0 && GetSquadOrbTier(item.ItemId) > 0)
            .OrderBy(item => item.ItemUid)
            .ToList();
        if (fromOrdinal < 0 || fromOrdinal >= orbs.Count)
            return destroyed;

        for (int ordinal = fromOrdinal; ordinal < orbs.Count; ordinal++)
        {
            if (inventory.TryRemoveItem(orbs[ordinal].ItemUid, 1, out var destroyedItem) &&
                destroyedItem != null)
                destroyed.Add(destroyedItem);
        }
        return destroyed;
    }

    private static int GetSquadOrbTier(int itemId)
    {
        if (OrbData.TryGetColorAndTier(itemId, out _, out int tier))
            return tier;
        return OrbData.TryGetRecoveryTier(itemId, out int recoveryTier) ? recoveryTier : 0;
    }

}
