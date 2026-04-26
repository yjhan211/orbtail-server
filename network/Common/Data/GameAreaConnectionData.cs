// ReSharper disable All

#pragma warning disable CS8618
#pragma warning disable CS8625
#pragma warning disable CS8603

using System.Collections.Generic;
using network.common.data.helpers;
using network.common.data.models;

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
    ///     SpawnCell: 이 방향 연결로 ToArea에 도착했을 때의 스폰 위치. (0,0)이면 미설정 — fallback 로직 사용.
    /// </summary>
    public class AreaConnectionInfo
    {
        public MapId MapId { get; private set; }
        public AreaType FromArea { get; private set; }
        public AreaType ToArea { get; private set; }
        public ConnectionType Type { get; private set; }
        public StairSide StairSide { get; private set; }
        public Cell SpawnCell { get; private set; }

        public static AreaConnectionInfo Create(MapId mapId, AreaType from, AreaType to,
            ConnectionType type, StairSide side, Cell spawnCell)
        {
            return new AreaConnectionInfo
            {
                MapId = mapId,
                FromArea = from,
                ToArea = to,
                Type = type,
                StairSide = side,
                SpawnCell = spawnCell
            };
        }

        /// <summary>
        ///     CSV 한 행에서 forward(from→to) 방향 연결 정보 생성.
        ///     역방향(to→from) 연결은 GameAreaConnectionData에서 reverse_spawn_x/y로 별도 생성.
        /// </summary>
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

            int forwardX = row.ContainsKey("forward_spawn_x") && int.TryParse(row["forward_spawn_x"], out var fx) ? fx : 0;
            int forwardY = row.ContainsKey("forward_spawn_y") && int.TryParse(row["forward_spawn_y"], out var fy) ? fy : 0;

            return new AreaConnectionInfo
            {
                MapId = (MapId)int.Parse(row["map_id"]),
                FromArea = (AreaType)int.Parse(row["from_area"]),
                ToArea = (AreaType)int.Parse(row["to_area"]),
                Type = type,
                StairSide = side,
                SpawnCell = new Cell(forwardX, forwardY)
            };
        }

        /// <summary>
        ///     CSV 한 행에서 reverse(to→from) 방향 연결 정보 생성.
        /// </summary>
        public static AreaConnectionInfo CreateReverseFromData(CsvRow row, AreaConnectionInfo forward)
        {
            int reverseX = row.ContainsKey("reverse_spawn_x") && int.TryParse(row["reverse_spawn_x"], out var rx) ? rx : 0;
            int reverseY = row.ContainsKey("reverse_spawn_y") && int.TryParse(row["reverse_spawn_y"], out var ry) ? ry : 0;

            return new AreaConnectionInfo
            {
                MapId = forward.MapId,
                FromArea = forward.ToArea,
                ToArea = forward.FromArea,
                Type = forward.Type,
                StairSide = forward.StairSide,
                SpawnCell = new Cell(reverseX, reverseY)
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
                var forward = AreaConnectionInfo.CreateFromData(row);
                var reverse = AreaConnectionInfo.CreateReverseFromData(row, forward);
                AddBidirectional(forward, reverse);
            }
        }

        private static void AddBidirectional(AreaConnectionInfo forward, AreaConnectionInfo reverse)
        {
            _all.Add(forward);
            AddOne(forward.MapId, forward.FromArea, forward);
            AddOne(reverse.MapId, reverse.FromArea, reverse);
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
        ///     모든 연결 반환 (디버그/검증용)
        /// </summary>
        public static IReadOnlyList<AreaConnectionInfo> GetAll()
        {
            return _all;
        }

        /// <summary>
        ///     fromArea → toArea 이동 시 toArea 안의 스폰 셀 조회. 미설정(0,0) 또는 연결 없으면 null.
        ///     같은 (from, to) 쌍이 서/동 두 개 존재할 수 있는 연결(층간 door 등)은 stairSide로 구분.
        ///     conn.StairSide=None이면 항상 매치, West/East면 요청 stairSide와 일치해야 매치.
        /// </summary>
        public static Cell GetSpawnCell(MapId mapId, AreaType fromArea, AreaType toArea,
            StairSide stairSide = StairSide.None)
        {
            foreach (var conn in GetConnections(mapId, fromArea))
            {
                if (conn.ToArea != toArea) continue;
                if (conn.StairSide != StairSide.None && conn.StairSide != stairSide) continue;
                var cell = conn.SpawnCell;
                if (cell.X == 0 && cell.Y == 0) return null;
                return cell;
            }

            return null;
        }
    }
}
