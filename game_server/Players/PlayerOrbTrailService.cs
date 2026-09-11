using game_server.matches;
using network.common.data;
using network.common.data.models;

namespace game_server.players;

/// <summary>
///     매치의 오브 꼬리 경로에서 좌표를 계산하고 지정 순번 이후의 오브를 삭제한다.
///     전투와 구역 폐쇄가 같은 인벤토리 순서·좌표 규칙을 사용한다.
///     경로와 인벤토리는 매치가 소유하며 호출자는 매치 잠금을 보유한다.
/// </summary>
internal sealed class PlayerOrbTrailService
{
    internal const float CutFlashRadius = 0.7f;
    internal const int CutVfxKind = 1;

    public int CountOrbs(MatchRuntime runtime, Player player)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Orb trail operations require the match lock.");
        }

        var inventory = player.Orbs;
        int orbCount = 0;
        foreach (var item in inventory.GetAllItems())
        {
            if (item.Count <= 0 || PlayerOrbCollection.GetOrbTier(item.ItemId) <= 0)
            {
                continue;
            }

            orbCount += item.Count;
        }

        return orbCount;
    }

    public List<int> GetOrbTiersInOrder(MatchRuntime runtime, Player player)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Orb trail operations require the match lock.");
        }

        var items = player.Orbs.GetAllItems().ToList();
        items.Sort((left, right) => left.ItemUid.CompareTo(right.ItemUid));

        var tiers = new List<int>();
        foreach (var item in items)
        {
            if (item.Count <= 0)
            {
                continue;
            }

            int tier = PlayerOrbCollection.GetOrbTier(item.ItemId);
            if (tier > 0)
            {
                tiers.Add(tier);
            }
        }

        return tiers;
    }

    public Vector3f GetOrbPosition(MatchRuntime runtime, Player player, int ordinal, Vector3f anchor, IReadOnlyList<int>? orderedTiers = null)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Orb trail operations require the match lock.");
        }

        float targetDistance = OrbData.GetSwarmTrailDistance(orderedTiers ?? GetOrbTiersInOrder(runtime, player), ordinal);
        return GetPositionAtDistance(runtime, player, targetDistance, anchor);
    }

    public Vector3f GetPositionAtDistance(MatchRuntime runtime, Player player, float targetDistance, Vector3f anchor)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Orb trail operations require the match lock.");
        }

        if (!runtime.TrailCombat.OrbTrails.TryGetValue((runtime.MatchingId, player.PlayerId), out var points) || points.Count == 0)
        {
            return new Vector3f(anchor.X, anchor.Y - targetDistance * 0.2f, 0f);
        }

        var previous = anchor;
        float accumulated = 0f;
        for (int index = 0; index < points.Count; index++)
        {
            var point = points[index];
            float segment = Vector3f.Distance(previous, point);
            if (segment > 0.0001f && accumulated + segment >= targetDistance)
            {
                float t = (targetDistance - accumulated) / segment;
                return new Vector3f(previous.X + (point.X - previous.X) * t, previous.Y + (point.Y - previous.Y) * t, 0f);
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
        return new Vector3f(previous.X + tailDirection.X * remaining, previous.Y + tailDirection.Y * remaining, 0f);
    }

    public List<InGameItemInfo> DestroyOrbsFromOrdinal(MatchRuntime runtime, Player player, int fromOrdinal)
    {
        if (!Monitor.IsEntered(runtime.MatchLock))
        {
            throw new InvalidOperationException("Orb trail operations require the match lock.");
        }

        var destroyed = new List<InGameItemInfo>();
        var inventory = player.Orbs;
        var orbs = inventory.GetAllItems().Where(item => item.Count > 0 && PlayerOrbCollection.GetOrbTier(item.ItemId) > 0).OrderBy(item => item.ItemUid).ToList();
        if (fromOrdinal < 0 || fromOrdinal >= orbs.Count)
        {
            return destroyed;
        }

        for (int ordinal = fromOrdinal; ordinal < orbs.Count; ordinal++)
        {
            if (inventory.TryRemoveItem(orbs[ordinal].ItemUid, 1, out var destroyedItem) && destroyedItem != null)
            {
                player.ForgetOrbTimers(destroyedItem.ItemUid);
                destroyed.Add(destroyedItem);
            }
        }
        return destroyed;
    }
}
