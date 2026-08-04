using System;
using System.Collections.Generic;
using System.Linq;
using network.common.data.models;

namespace network.common.data
{

    /// <summary>
    /// Fixed Survivor Royale opening anchors.  A match seed permutes anchors, never players,
    /// so every roster member receives one unique corridor position regardless of join order.
    /// </summary>
    public static class SurvivorRoyaleSpawnData
    {
        private static readonly Cell[] CorridorAnchors =
        {
        new(140, 70),
        new(174, 108),
        new(112, 108),
        new(145, 108),
        new(178, 73),
        new(109, 85),
        new(139, 93),
        new(165, 85)
    };

        private static readonly AreaType[] PhaseRoomCandidates =
        {
            AreaType.ExamRoom,
            AreaType.BroadcastRoom,
            AreaType.Classroom2,
            AreaType.Classroom3,
            AreaType.Classroom4,
            AreaType.Library,
            AreaType.AdminOffice,
            AreaType.StaffRoom
        };

        public static IReadOnlyList<AreaType> GetPhaseRoomCandidates() =>
            PhaseRoomCandidates.ToArray();

        public static IReadOnlyList<Cell> GetCorridorAnchors() =>
            CorridorAnchors.Select(Cell.Clone).ToList();

        public static IReadOnlyDictionary<long, Cell> CreateAssignments(long matchingId, IEnumerable<long> playerIds)
        {
            var orderedPlayerIds = playerIds.Distinct().OrderBy(playerId => playerId).ToList();
            if (orderedPlayerIds.Count > CorridorAnchors.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(playerIds), orderedPlayerIds.Count,
                    $"Survivor Royale supports at most {CorridorAnchors.Length} players per match.");
            }

            var shuffledAnchors = Enumerable.Range(0, CorridorAnchors.Length).ToList();
            var rng = new Random(GetDeterministicSeed(matchingId));
            for (var index = shuffledAnchors.Count - 1; index > 0; index--)
            {
                var swapIndex = rng.Next(index + 1);
                (shuffledAnchors[index], shuffledAnchors[swapIndex]) =
                    (shuffledAnchors[swapIndex], shuffledAnchors[index]);
            }

            return orderedPlayerIds
                .Select((playerId, index) => new KeyValuePair<long, Cell>(
                    playerId, Cell.Clone(CorridorAnchors[shuffledAnchors[index]])))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
        }

        public static IReadOnlyDictionary<long, Cell> CreatePhaseRoomAssignments(
            long matchingId,
            IEnumerable<long> playerIds)
        {
            var orderedPlayerIds = playerIds.Distinct().OrderBy(playerId => playerId).ToList();
            if (orderedPlayerIds.Count > 8)
            {
                throw new ArgumentOutOfRangeException(nameof(playerIds), orderedPlayerIds.Count,
                    "Survivor Royale supports at most 8 players per match.");
            }

            var shuffledRooms = PhaseRoomCandidates.ToList();
            var rng = new Random(GetDeterministicSeed(matchingId));
            for (int index = shuffledRooms.Count - 1; index > 0; index--)
            {
                int swapIndex = rng.Next(index + 1);
                (shuffledRooms[index], shuffledRooms[swapIndex]) =
                    (shuffledRooms[swapIndex], shuffledRooms[index]);
            }

            return orderedPlayerIds
                .Select((playerId, index) => new KeyValuePair<long, Cell>(
                    playerId,
                    Cell.Clone(GameMapData.GetAreaSpawnCell(MapId.School, shuffledRooms[index]))))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
        }

        public static int GetDeterministicSeed(long matchingId) =>
            unchecked((int)(matchingId ^ (matchingId >> 32) ^ 0x51A7_195));

        public static int GetAnchorIndex(Cell cell)
        {
            int index = Array.FindIndex(CorridorAnchors, anchor => anchor.X == cell.X && anchor.Y == cell.Y);
            return index < 0 ? 0 : index + 1;
        }
    }
}
