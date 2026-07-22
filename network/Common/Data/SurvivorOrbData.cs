using System;
using System.Collections.Generic;
using System.Linq;

namespace network.common.data
{

    /// <summary>
    /// Shared color and tier rules for the three Survivor Royale orb lines added by #198.
    /// Legacy guardian orbs (107000003/004/006) intentionally remain outside this board rule.
    /// </summary>
    public enum SurvivorOrbColor
    {
        None = 0,
        Red = 1,
        Green = 2,
        Blue = 3
    }

    public static class SurvivorOrbData
    {
        public static bool TryGetColorAndTier(int itemId, out SurvivorOrbColor color, out int tier)
        {
            switch (itemId)
            {
                case 107000010: color = SurvivorOrbColor.Red; tier = 1; return true;
                case 107000011: color = SurvivorOrbColor.Red; tier = 2; return true;
                case 107000012: color = SurvivorOrbColor.Red; tier = 3; return true;
                case 107000020: color = SurvivorOrbColor.Green; tier = 1; return true;
                case 107000021: color = SurvivorOrbColor.Green; tier = 2; return true;
                case 107000022: color = SurvivorOrbColor.Green; tier = 3; return true;
                case 107000030: color = SurvivorOrbColor.Blue; tier = 1; return true;
                case 107000031: color = SurvivorOrbColor.Blue; tier = 2; return true;
                case 107000032: color = SurvivorOrbColor.Blue; tier = 3; return true;
                default: color = SurvivorOrbColor.None; tier = 0; return false;
            }
        }

        public static bool IsSurvivorOrb(int itemId) => TryGetColorAndTier(itemId, out _, out _);

        public static bool TryGetActivePair(
            int equippedItemId,
            IEnumerable<int> boardItemIds,
            out SurvivorOrbColor color,
            out int pairTier)
        {
            if (boardItemIds == null)
                throw new ArgumentNullException(nameof(boardItemIds));

            pairTier = 0;
            if (!TryGetColorAndTier(equippedItemId, out color, out _))
                return false;

            var tierCounts = new Dictionary<int, int>();
            foreach (int itemId in boardItemIds)
            {
                if (!TryGetColorAndTier(itemId, out var candidateColor, out int candidateTier) ||
                    candidateColor != color)
                {
                    continue;
                }

                tierCounts.TryGetValue(candidateTier, out int currentCount);
                tierCounts[candidateTier] = currentCount + 1;
            }

            pairTier = tierCounts
                .Where(entry => entry.Value >= 2)
                .Select(entry => entry.Key)
                .DefaultIfEmpty(0)
                .Max();
            return pairTier > 0;
        }
    }
}
