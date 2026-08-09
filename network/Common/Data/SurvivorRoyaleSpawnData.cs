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
        new(160, 108),
        new(112, 108),
        new(145, 108),
        new(178, 73),
        new(109, 85),
        new(139, 93),
        new(165, 85)
    };

        // #219 SB 클론 맵 — 중앙 광장(Ground)을 포드 10개가 직접 포위한다.
        // 스폰 후보 = 포드 10곳 (기존 방 enum 재사용). Ground(광장)와 상하 회랑 밴드
        // (Junkyard=1, Corridor=7)는 스폰 금지. 6인 매치는 이 중 6곳을 뽑는다.
        private static readonly AreaType[] PhaseRoomCandidates =
        {
            AreaType.Storage2,
            AreaType.AdminOffice,
            AreaType.StaffRoom,
            AreaType.Gym,
            AreaType.Classroom2,
            AreaType.Library,
            AreaType.Classroom3,
            AreaType.ExamRoom,
            AreaType.Classroom4,
            AreaType.BroadcastRoom
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

        public static IReadOnlyList<AreaType> GetSpotArenaCandidates() =>
            SpotArenaCandidates.ToArray();

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

        // #219 8인 전환: 스폰 풀 = 포드 10곳 중 도서관·강당 제외 8곳 — 쌍 구역(만남 지점)은
        // 스폰이 아니라 동선의 목적지다. 8인 매치 = 8포드 전원 유니크 스폰.
        private static readonly AreaType[] SwarmSpawnPodCandidates = PhaseRoomCandidates
            .Where(area => area != AreaType.Library && area != AreaType.Gym)
            .ToArray();

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
                    Cell.Clone(GameMapData.GetAreaSpawnCell(MapId.School, shuffledRooms[index]))))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
        }

        public static IReadOnlyDictionary<long, Cell> CreateSpotArenaAssignments(
            long matchingId,
            IEnumerable<long> playerIds)
        {
            var orderedPlayerIds = playerIds.Distinct().OrderBy(playerId => playerId).ToList();
            if (orderedPlayerIds.Count > SpotArenaCandidates.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(playerIds), orderedPlayerIds.Count,
                    "Spot Arena supports at most four players per match.");
            }

            var shuffledRooms = SpotArenaCandidates.ToList();
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
