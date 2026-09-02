using System;
using System.Collections.Generic;
using System.Linq;
using network.common.data.models;

namespace network.common.data
{

    /// <summary>
    /// Fixed Swarm match opening anchors.  A match seed permutes anchors, never players,
    /// so every roster member receives one unique position regardless of join order.
    /// </summary>
    public static class MatchSpawnData
    {
        // #272 School2 재지정 (2026-08-27): School 복도 앵커 좌표 → S2 시작방 8곳의 실스폰 셀
        // (GetAreaSpawnCell = rect 중심과 동일). 실전 배정은 CreatePhaseRoomAssignments지만,
        // 이 배열이 텔레메트리 앵커 인덱스(GetAnchorIndex)·씬 스폰 기즈모의 원천이라
        // 실스폰과 일치해야 한다. 순서 = PhaseRoomCandidates와 동일.
        private static readonly Cell[] CorridorAnchors =
        {
        new(105, 85),
        new(172, 85),
        new(77, 56),
        new(78, -10),
        new(105, -39),
        new(199, -10),
        new(199, 56),
        new(172, -39)
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
                    $"Swarm match supports at most {SwarmSpawnPodCandidates.Length} players per match.");
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
