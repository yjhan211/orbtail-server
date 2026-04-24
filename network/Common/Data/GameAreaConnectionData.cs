// ReSharper disable All

#pragma warning disable CS8618
#pragma warning disable CS8625
#pragma warning disable CS8603

using System.Collections.Generic;
using network.common.data.helpers;

namespace network.common.data
{
    public enum ConnectionType
    {
        None = 0,
        Door = 1,
        Stair = 2
    }

    public enum StairSide
    {
        None = 0,
        West = 1,
        East = 2
    }

    /// <summary>
    ///     두 구역 간의 연결 정보 (양방향 이동 가능)
    ///     GDD v0.0.8 기준: Door = 같은 층 인접, Stair = 엘리베이터식 층 간 이동
    /// </summary>
    public class AreaConnectionInfo
    {
        public MapId MapId { get; private set; }
        public AreaType FromArea { get; private set; }
        public AreaType ToArea { get; private set; }
        public ConnectionType Type { get; private set; }
        public StairSide StairSide { get; private set; }

        public static AreaConnectionInfo Create(MapId mapId, AreaType from, AreaType to,
            ConnectionType type, StairSide side)
        {
            return new AreaConnectionInfo
            {
                MapId = mapId,
                FromArea = from,
                ToArea = to,
                Type = type,
                StairSide = side
            };
        }

        public static AreaConnectionInfo CreateFromData(CsvRow row)
        {
            var typeStr = row.ContainsKey("connection_type") ? row["connection_type"] : "door";
            var type = typeStr.ToLowerInvariant() switch
            {
                "stair" => ConnectionType.Stair,
                "door" => ConnectionType.Door,
                _ => ConnectionType.None
            };

            var sideStr = row.ContainsKey("stair_side") ? row["stair_side"] : "none";
            var side = sideStr.ToLowerInvariant() switch
            {
                "west" => StairSide.West,
                "east" => StairSide.East,
                _ => StairSide.None
            };

            return new AreaConnectionInfo
            {
                MapId = (MapId)int.Parse(row["map_id"]),
                FromArea = (AreaType)int.Parse(row["from_area"]),
                ToArea = (AreaType)int.Parse(row["to_area"]),
                Type = type,
                StairSide = side
            };
        }
    }

    /// <summary>
    ///     구역 간 연결 그래프. CSV(map_connections.csv) 기반 정적 데이터.
    ///     양방향 연결로 저장되어 A→B/B→A 모두 조회 가능.
    /// </summary>
    public static class GameAreaConnectionData
    {
        // (MapId, FromArea) → List of connections
        private static readonly Dictionary<(MapId, AreaType), List<AreaConnectionInfo>> _adjacency = new();

        // 모든 연결 (중복 포함)
        private static readonly List<AreaConnectionInfo> _all = new();

        public static void Initialize(List<CsvRow> csvData)
        {
            _adjacency.Clear();
            _all.Clear();

            foreach (var row in csvData)
            {
                var conn = AreaConnectionInfo.CreateFromData(row);
                AddBidirectional(conn);
            }
        }

        private static void AddBidirectional(AreaConnectionInfo conn)
        {
            _all.Add(conn);

            // From → To
            AddOne(conn.MapId, conn.FromArea, conn);

            // To → From (역방향 조회용)
            var reverse = CreateReverse(conn);
            AddOne(conn.MapId, conn.ToArea, reverse);
        }

        private static void AddOne(MapId mapId, AreaType fromArea, AreaConnectionInfo conn)
        {
            var key = (mapId, fromArea);
            if (!_adjacency.TryGetValue(key, out var list))
            {
                list = new List<AreaConnectionInfo>();
                _adjacency[key] = list;
            }

            list.Add(conn);
        }

        private static AreaConnectionInfo CreateReverse(AreaConnectionInfo conn)
        {
            return AreaConnectionInfo.Create(conn.MapId, conn.ToArea, conn.FromArea, conn.Type, conn.StairSide);
        }

        /// <summary>
        ///     특정 구역에서 이동 가능한 모든 인접 구역 정보 반환 (양방향 포함)
        /// </summary>
        public static IReadOnlyList<AreaConnectionInfo> GetConnections(MapId mapId, AreaType fromArea)
        {
            return _adjacency.TryGetValue((mapId, fromArea), out var list)
                ? list
                : new List<AreaConnectionInfo>();
        }

        /// <summary>
        ///     두 구역이 직접 연결되어 있는지 확인
        /// </summary>
        public static bool IsAdjacent(MapId mapId, AreaType a, AreaType b)
        {
            if (a == b) return false;
            var connections = GetConnections(mapId, a);
            foreach (var conn in connections)
                if (conn.ToArea == b)
                    return true;
            return false;
        }

        /// <summary>
        ///     두 구역 간 연결 타입 조회 (인접 아니면 None)
        /// </summary>
        public static ConnectionType GetConnectionType(MapId mapId, AreaType a, AreaType b)
        {
            foreach (var conn in GetConnections(mapId, a))
                if (conn.ToArea == b)
                    return conn.Type;
            return ConnectionType.None;
        }

        /// <summary>
        ///     엘리베이터식 계단 사용 시 목적지 층의 같은 쪽(서/동) 복도 조회
        ///     예) 4층복도의 서쪽계단으로 2층 선택 → 2층복도
        /// </summary>
        public static AreaType GetStairTarget(MapId mapId, AreaType fromCorridor, StairSide side, int targetFloor)
        {
            foreach (var conn in GetConnections(mapId, fromCorridor))
            {
                if (conn.Type != ConnectionType.Stair) continue;
                if (conn.StairSide != side) continue;
                if (conn.ToArea.GetFloor() == targetFloor) return conn.ToArea;
            }

            return AreaType.None;
        }

        /// <summary>
        ///     모든 연결 반환 (디버그/검증용)
        /// </summary>
        public static IReadOnlyList<AreaConnectionInfo> GetAll()
        {
            return _all;
        }
    }
}
