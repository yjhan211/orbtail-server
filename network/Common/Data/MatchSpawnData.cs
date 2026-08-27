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
    public static class MatchSpawnData
    {
        private static readonly Cell[] CorridorAnchors =
        {
        new(140, 70),
        new(160, 108),
        new(112, 108),
        new(145, 108),
        new(178, 73),
        new(109, 85),
        new(139, 93),
        new(165, 85)
    };

        // #272 School2 8인 전환 — 스폰 포드 = 1인 전용 시작방 8곳 (외곽 링).
        // (#219~#223 School 포드 10곳 세대는 이 배열 교체로 퇴역 — School 데이터는 CSV에 남는다.)
        private static readonly AreaType[] PhaseRoomCandidates =
        {
            AreaType.S2Classroom1,
            AreaType.S2Classroom2,
            AreaType.S2ExamRoom,
            AreaType.S2BroadcastRoom,
            AreaType.S2Storage,
            AreaType.S2NurseOffice,
            AreaType.S2AdminOffice1,
            AreaType.S2AdminOffice2
        };

        private static readonly AreaType[] SpotArenaCandidates =
        {
            AreaType.Classroom3,
            AreaType.Classroom4,
            AreaType.BroadcastRoom,
            AreaType.Classroom2,
        };

        public static IReadOnlyList<AreaType> GetPhaseRoomCandidates() =>
            PhaseRoomCandidates.ToArray();

        public static IReadOnlyList<Cell> GetCorridorAnchors() =>
            CorridorAnchors.Select(Cell.Clone).ToList();

        public static Cell GetCorridorAnchor(int srNumber)
        {
            if (srNumber < 1 || srNumber > CorridorAnchors.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(srNumber), srNumber,
                    $"Corridor anchor must be between SR1 and SR{CorridorAnchors.Length}.");
            }

            return Cell.Clone(CorridorAnchors[srNumber - 1]);
        }

        public static Cell GetCorridorSpawnCell(int srNumber)
        {
            Cell anchor = GetCorridorAnchor(srNumber);
            Cell? spawnCell = anchor.GetAdjacentCells()
                .FirstOrDefault(cell =>
                    GameMapData.GetCurrentArea(MapId.School, cell) == AreaType.Corridor &&
                    GameMapData.IsMoveablePosition(MapId.School, cell));
            return spawnCell is null ? anchor : Cell.Clone(spawnCell);
        }

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

        // #272 8인: 스폰 풀 = 시작방 8곳 전부 — 정원과 일치, 전원 유니크 스폰.
        private static readonly AreaType[] SwarmSpawnPodCandidates = PhaseRoomCandidates.ToArray();

        public static IReadOnlyDictionary<long, Cell> CreatePhaseRoomAssignments(
            long matchingId,
            IEnumerable<long> playerIds)
        {
            var orderedPlayerIds = playerIds.Distinct().OrderBy(playerId => playerId).ToList();
            if (orderedPlayerIds.Count > SwarmSpawnPodCandidates.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(playerIds), orderedPlayerIds.Count,
                    $"Survivor Royale supports at most {SwarmSpawnPodCandidates.Length} players per match.");
            }

            var shuffledRooms = SwarmSpawnPodCandidates.ToList();
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
                    Cell.Clone(GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, shuffledRooms[index]))))
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
