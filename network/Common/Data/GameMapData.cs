// ReSharper disable All
#pragma warning disable CS8603 // 가능한 null 참조 반환입니다.
#pragma warning disable CS8618 // 생성자를 종료할 때 null을 허용하지 않는 필드에 null이 아닌 값을 포함해야 합니다. null 허용으로 선언해 보세요.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using network.common.data.helpers;
using network.common.data.models;
using network.managers;
using UnityEngine;

namespace network.common.data
{
    public struct InitCellData
    {
        public Vector3Int position;
        public MapId from;
        public bool isFlip;

        public InitCellData(Vector3Int position, MapId from, bool isFlip)
        {
            this.position = position;
            this.from = from;
            this.isFlip = isFlip;
        }
    }

    public enum ChairDirection
    {
        NONE,
        LEFT,
        RIGHT
    }

    public static class GameMapData
    {
        private static readonly Dictionary<MapId, MapInfo> _mapInfos = new();
        private static readonly Dictionary<MapId, List<MapRegion>> _mapRegions = new();
        private static readonly Dictionary<MapId, List<ChairInfo>> _chairInfos = new();
        private static readonly Dictionary<MapId, List<AreaRegion>> _areaRegions = new();

        // 런타임 오버라이드: 에디터에서 추가한 장애물 위치
        private static readonly Dictionary<MapId, HashSet<long>> _runtimeObstacles = new();
        private static readonly Dictionary<MapId, HashSet<long>> _staticWalkableCells = new();
        private static readonly Dictionary<MapId, HashSet<long>> _staticObstacleCells = new();
        private static readonly HashSet<MapId> _mapsWithExplicitWalkableCells = new();

        public static void Initialize(List<CsvRow> mapInfo, List<CsvRow> mapRegion)
        {
            _mapInfos.Clear();
            _mapRegions.Clear();
            _chairInfos.Clear();
            _areaRegions.Clear();
            _runtimeObstacles.Clear();
            _staticWalkableCells.Clear();
            _staticObstacleCells.Clear();
            _mapsWithExplicitWalkableCells.Clear();

            InitializeMapInfo(mapInfo);
            InitializeMapRegions(mapRegion);
            BuildMoveablePositionCaches();
        }

        private static void InitializeMapInfo(List<CsvRow> data)
        {
            foreach (var row in data)
            {
                var initCellX = int.Parse(row["init_cell_x"]);
                var initCellY = int.Parse(row["init_cell_y"]);
                var isFlip = int.Parse(row["is_flip"]) == 1;
                float worldOriginX = row.ContainsKey("world_origin_x")
                    ? float.Parse(row["world_origin_x"], CultureInfo.InvariantCulture)
                    : 0f;
                float worldOriginY = row.ContainsKey("world_origin_y")
                    ? float.Parse(row["world_origin_y"], CultureInfo.InvariantCulture)
                    : 0f;

                var mapInfo = new MapInfo
                {
                    Id = int.Parse(row["id"]),
                    SceneName = row["scene_name"],
                    IsCommon = int.Parse(row["is_common"]) == 1,
                    WorldOriginX = worldOriginX,
                    WorldOriginY = worldOriginY,
                    InitCell = new InitCellData(
                        new Vector3Int(initCellX, initCellY, 0),
                        MapId.None,
                        isFlip
                    )
                };

                _mapInfos[CsvHelper.ParseDefinedEnum<MapId>(row["id"], "map_info.id")] = mapInfo;
            }
        }

        private static void InitializeMapRegions(List<CsvRow> data)
        {
            foreach (var row in data)
            {
                var mapId = CsvHelper.ParseDefinedEnum<MapId>(row["map_id"], "map_region.map_id");

                if (row["region_type"].Equals("chair", StringComparison.OrdinalIgnoreCase))
                {
                    // 의자 정보 처리
                    var chairInfo = new ChairInfo(
                        new Cell(
                            int.Parse(row["start_x"]),
                            int.Parse(row["start_y"])
                        ),
                        int.Parse(row["is_flip"]) == 1
                    );

                    if (!_chairInfos.ContainsKey(mapId))
                    {
                        _chairInfos[mapId] = new List<ChairInfo>();
                    }
                    _chairInfos[mapId].Add(chairInfo);
                    continue;
                }

                if (row["region_type"].Equals("area", StringComparison.OrdinalIgnoreCase))
                {
                    // Area 정보 처리
                    var areaType = CsvHelper.ParseDefinedEnum<AreaType>(row["area"], $"map_region(map {mapId}).area");

                    var areaRegion = new AreaRegion
                    {
                        AreaType = areaType,
                        Start = new Cell(int.Parse(row["start_x"]), int.Parse(row["start_y"])),
                        End = new Cell(int.Parse(row["end_x"]), int.Parse(row["end_y"]))
                    };

                    if (!_areaRegions.ContainsKey(mapId))
                    {
                        _areaRegions[mapId] = new List<AreaRegion>();
                    }
                    _areaRegions[mapId].Add(areaRegion);
                    continue;
                }

                // 기존 region 처리
                var region = new MapRegion
                {
                    RegionType = row["region_type"],
                    Start = new Cell(
                        int.Parse(row["start_x"]),
                        int.Parse(row["start_y"])
                    ),
                    End = new Cell(
                        int.Parse(row["end_x"]),
                        int.Parse(row["end_y"])
                    ),
                    WarpTo = CsvHelper.ParseDefinedEnum<MapId>(row["area"], $"map_region(map {mapId}).area(warp)")
                };

                if (!_mapRegions.ContainsKey(mapId))
                {
                    _mapRegions[mapId] = new List<MapRegion>();
                }
                _mapRegions[mapId].Add(region);
            }
        }

        private static void BuildMoveablePositionCaches()
        {
            var mapIds = new HashSet<MapId>(_mapInfos.Keys);
            mapIds.UnionWith(_mapRegions.Keys);
            mapIds.UnionWith(_areaRegions.Keys);

            foreach (var mapId in mapIds)
            {
                var regions = _mapRegions.TryGetValue(mapId, out var mapRegions)
                    ? mapRegions
                    : new List<MapRegion>();
                var areas = _areaRegions.TryGetValue(mapId, out var areaRegions)
                    ? areaRegions
                    : new List<AreaRegion>();
                var obstacleCells = new HashSet<long>();

                foreach (var region in regions)
                {
                    if (region.RegionType.Equals("obstacle", StringComparison.OrdinalIgnoreCase))
                    {
                        AddRectangleCells(obstacleCells, region.Start, region.End);
                    }
                }

                _staticObstacleCells[mapId] = obstacleCells;

                var groundRegions = regions
                    .Where(region => region.RegionType.Equals("ground", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (groundRegions.Count == 0 && areas.Count == 0)
                {
                    continue;
                }

                var walkableCells = new HashSet<long>();
                foreach (var region in groundRegions)
                {
                    AddRectangleCells(walkableCells, region.Start, region.End);
                }

                foreach (var area in areas)
                {
                    AddRectangleCells(walkableCells, area.Start, area.End);
                }

                walkableCells.ExceptWith(obstacleCells);
                _staticWalkableCells[mapId] = walkableCells;
                _mapsWithExplicitWalkableCells.Add(mapId);
            }
        }

        private static void AddRectangleCells(HashSet<long> cells, Cell start, Cell end)
        {
            for (var x = start.X; x <= end.X; x++)
            {
                for (var y = start.Y; y <= end.Y; y++)
                {
                    cells.Add(MakeCellKey(x, y));
                }
            }
        }

        public static MapInfo GetMapInfo(MapId id)
        {
            return _mapInfos.TryGetValue(id, out var info) ? info : null;
        }

        public static List<MapRegion> GetMapRegions(MapId mapId)
        {
            return _mapRegions.TryGetValue(mapId, out var regions) ? regions : new List<MapRegion>();
        }

        private static long MakeCellKey(Cell cell) => MakeCellKey(cell.X, cell.Y);

        private static long MakeCellKey(int x, int y) => ((long)x << 32) | (uint)y;

        public static bool IsMoveablePosition(MapId mapId, Cell position)
        {
            if (position == null)
            {
                return false;
            }

            var cellKey = MakeCellKey(position);
            if (_runtimeObstacles.TryGetValue(mapId, out var obstacles) &&
                obstacles.Contains(cellKey))
            {
                return false;
            }

            if (_mapsWithExplicitWalkableCells.Contains(mapId))
            {
                return _staticWalkableCells.TryGetValue(mapId, out var walkableCells) &&
                       walkableCells.Contains(cellKey);
            }

            return !_staticObstacleCells.TryGetValue(mapId, out var staticObstacles) ||
                   !staticObstacles.Contains(cellKey);
        }

        // Runtime obstacle overrides are applied before the cached static map cells.
        public static void SetRuntimeObstacles(MapId mapId, IEnumerable<Vector3Int> obstaclePositions)
        {
            if (!_runtimeObstacles.ContainsKey(mapId))
            {
                _runtimeObstacles[mapId] = new HashSet<long>();
            }

            _runtimeObstacles[mapId].Clear();

            foreach (var pos in obstaclePositions)
            {
                _runtimeObstacles[mapId].Add(MakeCellKey(pos.x, pos.y));
            }
        }

        public static void ClearRuntimeObstacles(MapId mapId)
        {
            if (_runtimeObstacles.ContainsKey(mapId))
            {
                _runtimeObstacles[mapId].Clear();
            }
        }

        // 현재 위치의 Area 가져오기
        public static AreaType GetCurrentArea(MapId mapId, Cell position)
        {
            if (!_areaRegions.TryGetValue(mapId, out var areas))
            {
                return AreaType.None;
            }

            foreach (var area in areas)
            {
                if (area.Contains(position))
                {
                    return area.AreaType;
                }
            }

            return AreaType.None;
        }

        /// <summary>
        /// Keeps coordinate-driven movement from repeatedly changing areas while a player
        /// is standing on a shared one-cell boundary. Explicit area transitions should use
        /// GetCurrentArea so their destination is applied immediately.
        /// </summary>
        public static AreaType GetStableCurrentArea(MapId mapId, Cell position, AreaType currentArea)
        {
            var resolvedArea = GetCurrentArea(mapId, position);
            if (currentArea == AreaType.None ||
                resolvedArea == AreaType.None ||
                resolvedArea == currentArea)
            {
                return resolvedArea;
            }

            if (GetCurrentArea(mapId, new Cell(position.X - 1, position.Y)) == currentArea ||
                GetCurrentArea(mapId, new Cell(position.X + 1, position.Y)) == currentArea ||
                GetCurrentArea(mapId, new Cell(position.X, position.Y - 1)) == currentArea ||
                GetCurrentArea(mapId, new Cell(position.X, position.Y + 1)) == currentArea)
            {
                return currentArea;
            }

            return resolvedArea;
        }

        // 특정 맵의 모든 Area 가져오기
        public static List<AreaRegion> GetAreas(MapId mapId)
        {
            return _areaRegions.TryGetValue(mapId, out var areas) ? areas : new List<AreaRegion>();
        }

        /// <summary>
        ///     특정 Area의 스폰 셀 반환 (영역 중심, 장애물/미걷기 구역이면 영역 내 다른 walkable 셀 검색)
        ///     GDD v0.0.8: 구역 이동 시 목적지 스폰 포인트로 사용
        /// </summary>
        public static Cell GetAreaSpawnCell(MapId mapId, AreaType targetArea)
        {
            if (!_areaRegions.TryGetValue(mapId, out var areas))
                return new Cell(0, 0);

            foreach (var area in areas)
            {
                if (area.AreaType != targetArea) continue;

                // 1순위: 영역 중심 셀
                int centerX = (area.Start.X + area.End.X) / 2;
                int centerY = (area.Start.Y + area.End.Y) / 2;
                var center = new Cell(centerX, centerY);
                if (IsMoveablePosition(mapId, center))
                    return center;

                // 2순위: 영역 내 walkable 셀 스캔 (중심부터 나선형)
                for (int x = area.Start.X; x <= area.End.X; x++)
                {
                    for (int y = area.Start.Y; y <= area.End.Y; y++)
                    {
                        var candidate = new Cell(x, y);
                        if (IsMoveablePosition(mapId, candidate))
                            return candidate;
                    }
                }

                // 3순위: 그냥 중심 반환 (walkable 아니더라도)
                return center;
            }

            return new Cell(0, 0);
        }

        public static (MapId mapId, Cell spawnPosition, bool isFlip)? GetPortalOrNull(GameObjectInfo objectInfo, bool isTutorial)
        {
            var currentCell = objectInfo.Cell;
            var currentMap = objectInfo.MapId;
            var regions = GetMapRegions(currentMap);
            var portalRegions = regions.Where(r => r.IsPortal);

            foreach (var portal in portalRegions)
            {
                if (currentCell.X >= portal.Start.X && currentCell.X <= portal.End.X &&
                    currentCell.Y >= portal.Start.Y && currentCell.Y <= portal.End.Y)
                {
                    var targetMapInfo = GetMapInfo(portal.WarpTo);
                    var (spawnPosition, isFlip) = targetMapInfo.GetInitialPosition();

                    return (portal.WarpTo, spawnPosition, isFlip);
                }
            }

            return null;
        }

        /// <summary>
        /// 주어진 셀이 현재 맵의 어떤 포탈 영역에 포함되는지 확인하고, 해당 포탈 정보를 반환합니다.
        /// </summary>
        /// <param name="currentMapId">현재 맵 ID</param>
        /// <param name="cellToCheck">확인할 셀 좌표</param>
        /// <returns>포탈 정보 (목표 맵, 시작점, 끝점). 포탈 영역에 없으면 null</returns>
        public static (MapId targetMap, Cell start, Cell end)? GetPortalInfoAtCell(MapId currentMapId, Cell cellToCheck)
        {
            var regions = GetMapRegions(currentMapId);
            var portalRegions = regions.Where(r => r.IsPortal);

            foreach (var portal in portalRegions)
            {
                if (cellToCheck.X >= portal.Start.X && cellToCheck.X <= portal.End.X &&
                    cellToCheck.Y >= portal.Start.Y && cellToCheck.Y <= portal.End.Y)
                {
                    return (portal.WarpTo, portal.Start, portal.End);
                }
            }

            return null;
        }


        public static ChairDirection GetChairDirection(MapId mapId, Cell position)
        {
            if (!_chairInfos.ContainsKey(mapId))
                return ChairDirection.NONE;

            var chairInfo = _chairInfos[mapId].FirstOrDefault(c =>
                c.Position.X == position.X && c.Position.Y == position.Y);

            if (chairInfo == null)
                return ChairDirection.NONE;

            return chairInfo.IsFlip ? ChairDirection.RIGHT : ChairDirection.LEFT;
        }

        /// <summary>
        ///     매치 맵(Config.SWARM_MATCH_MAP) 저작 검증 (#335 복원 — 이전 본문은 전부 주석이었다).
        ///     스폰 앵커·문·시작 셀·구역 연결이 걸을 수 있고 제 구역 안에 있는지를 부팅 시 잠근다.
        ///     실패는 참조 무결성 검증과 같은 InvalidDataException으로 부팅을 막는다.
        /// </summary>
        public static void Validate()
        {
            var errors = new List<string>();
            MapId matchMap = Config.SWARM_MATCH_MAP;

            if (_mapInfos.Count == 0)
                errors.Add("map_info: 맵 정보가 없다");

            foreach (var pair in _mapRegions)
            {
                foreach (var region in pair.Value)
                {
                    if (region.Start.X > region.End.X || region.Start.Y > region.End.Y)
                        errors.Add($"map_region(map {pair.Key}) {region.RegionType}: 좌표 역전 {region.Start} → {region.End}");
                }
            }

            foreach (var pair in _areaRegions)
            {
                foreach (var area in pair.Value)
                {
                    if (area.Start.X > area.End.X || area.Start.Y > area.End.Y)
                        errors.Add($"map_region(map {pair.Key}) area {area.AreaType}: 좌표 역전 {area.Start} → {area.End}");
                }
            }

            ValidateMatchMap(matchMap, errors);

            if (errors.Count == 0)
                return;

            throw new InvalidDataException($"맵 데이터 검증 실패: {errors.Count}건\n{string.Join("\n", errors)}");
        }

        private static void ValidateMatchMap(MapId matchMap, List<string> errors)
        {
            var mapInfo = GetMapInfo(matchMap);
            if (mapInfo == null)
            {
                errors.Add($"map_info: 매치 맵 {matchMap}의 행이 없다");
                return;
            }

            if (string.IsNullOrEmpty(mapInfo.SceneName))
                errors.Add($"map_info({matchMap}): scene_name이 비어 있다");

            var initCell = new Cell(mapInfo.InitCell.position.x, mapInfo.InitCell.position.y);
            if (!IsMoveablePosition(matchMap, initCell))
                errors.Add($"map_info({matchMap}): 초기 셀 {initCell}이 걸을 수 없는 자리다");

            var areas = GetAreas(matchMap);
            if (areas.Count == 0)
            {
                errors.Add($"map_region({matchMap}): area 행이 없다");
                return;
            }

            var definedAreas = new HashSet<AreaType>(areas.Select(area => area.AreaType));
            if (!definedAreas.Contains(Config.SWARM_MATCH_GROUND_AREA))
                errors.Add($"map_region({matchMap}): 공용 구역 {Config.SWARM_MATCH_GROUND_AREA}의 area 행이 없다");

            ValidateSpawnAnchors(matchMap, definedAreas, errors);
            ValidateDoors(matchMap, definedAreas, errors);
            ValidateAreaConnections(matchMap, definedAreas, errors);
        }

        /// <summary>시작방 스폰 셀(실배정)과 텔레메트리 앵커 배열이 걸을 수 있고 제 방 안에 있어야 한다.</summary>
        private static void ValidateSpawnAnchors(MapId matchMap, HashSet<AreaType> definedAreas, List<string> errors)
        {
            var rooms = MatchSpawnData.GetPhaseRoomCandidates();
            var anchors = MatchSpawnData.GetCorridorAnchors();
            if (rooms.Count != anchors.Count)
                errors.Add($"MatchSpawnData: 시작방 {rooms.Count}곳과 앵커 {anchors.Count}개의 수가 다르다");

            for (int index = 0; index < rooms.Count; index++)
            {
                AreaType room = rooms[index];
                if (!definedAreas.Contains(room))
                {
                    errors.Add($"MatchSpawnData: 시작방 {room}의 area 행이 map_region({matchMap})에 없다");
                    continue;
                }

                var spawnCell = GetAreaSpawnCell(matchMap, room);
                if (!IsMoveablePosition(matchMap, spawnCell))
                    errors.Add($"MatchSpawnData: {room} 스폰 셀 {spawnCell}이 걸을 수 없는 자리다");
                else if (GetCurrentArea(matchMap, spawnCell) != room)
                    errors.Add($"MatchSpawnData: {room} 스폰 셀 {spawnCell}이 {GetCurrentArea(matchMap, spawnCell)} 구역에 있다");

                if (index >= anchors.Count)
                    continue;

                var anchor = anchors[index];
                if (!IsMoveablePosition(matchMap, anchor))
                    errors.Add($"MatchSpawnData: 앵커 {index + 1} {anchor}이 걸을 수 없는 자리다");
                else if (GetCurrentArea(matchMap, anchor) != room)
                    errors.Add($"MatchSpawnData: 앵커 {index + 1} {anchor}이 {room}이 아니라 {GetCurrentArea(matchMap, anchor)}에 있다");
            }
        }

        /// <summary>문은 두 구역의 간선이다 — 위치 셀은 양쪽 rect 중 하나에, 차단 폴백 셀은 걸을 수 있는 자기 구역 안에.</summary>
        private static void ValidateDoors(MapId matchMap, HashSet<AreaType> definedAreas, List<string> errors)
        {
            foreach (var door in GameDoorData.GetAll())
            {
                string context = $"door_info[{door.DoorId}]";
                if (door.AreaType == AreaType.None || !definedAreas.Contains(door.AreaType))
                {
                    errors.Add($"{context}: area_type {door.AreaType}의 area 행이 map_region({matchMap})에 없다");
                    continue;
                }

                if (door.AreaTypeB != AreaType.None && !definedAreas.Contains(door.AreaTypeB))
                {
                    errors.Add($"{context}: area_type_b {door.AreaTypeB}의 area 행이 map_region({matchMap})에 없다");
                    continue;
                }

                var doorCell = new Cell((int)door.PositionX, (int)door.PositionY);
                if (!IsInsideArea(matchMap, doorCell, door.AreaType) &&
                    !IsInsideArea(matchMap, doorCell, door.AreaTypeB))
                    errors.Add($"{context}: 위치 {doorCell}이 {door.AreaType}·{door.AreaTypeB} 어느 rect에도 없다");

                var fallbackCell = new Cell(door.FallbackCellX, door.FallbackCellY);
                if (!IsMoveablePosition(matchMap, fallbackCell))
                    errors.Add($"{context}: 폴백 셀 {fallbackCell}이 걸을 수 없는 자리다");

                AreaType fallbackArea = GetCurrentArea(matchMap, fallbackCell);
                if (fallbackArea != door.AreaType && fallbackArea != door.AreaTypeB)
                    errors.Add($"{context}: 폴백 셀 {fallbackCell}이 {fallbackArea} 구역에 있다 (기대 {door.AreaType}·{door.AreaTypeB})");
            }
        }

        /// <summary>구역 연결의 양 끝은 area 행이 있어야 하고, 지정된 도착 스폰 셀은 걸을 수 있는 도착 구역 안이어야 한다.</summary>
        private static void ValidateAreaConnections(MapId matchMap, HashSet<AreaType> definedAreas, List<string> errors)
        {
            foreach (var area in GetAreas(matchMap))
            {
                foreach (var connection in GameAreaConnectionData.GetConnections(matchMap, area.AreaType))
                {
                    string context = $"map_connections({matchMap}) {connection.FromArea}→{connection.ToArea}";
                    if (!definedAreas.Contains(connection.ToArea))
                    {
                        errors.Add($"{context}: 도착 구역의 area 행이 없다");
                        continue;
                    }

                    var spawnCell = connection.SpawnCell;
                    if (spawnCell == null || (spawnCell.X == 0 && spawnCell.Y == 0))
                        continue;

                    if (!IsMoveablePosition(matchMap, spawnCell))
                        errors.Add($"{context}: 도착 스폰 셀 {spawnCell}이 걸을 수 없는 자리다");
                    else if (GetCurrentArea(matchMap, spawnCell) != connection.ToArea)
                        errors.Add($"{context}: 도착 스폰 셀 {spawnCell}이 {GetCurrentArea(matchMap, spawnCell)} 구역에 있다");
                }
            }
        }

        private static bool IsInsideArea(MapId mapId, Cell cell, AreaType areaType)
        {
            if (areaType == AreaType.None)
                return false;

            foreach (var area in GetAreas(mapId))
            {
                if (area.AreaType == areaType && area.Contains(cell))
                    return true;
            }

            return false;
        }

        public static (Cell start, Cell end)? GetPortalCoordinates(MapId currentMapId, MapId targetMapId)
        {
            var regions = GetMapRegions(currentMapId);
            var portalRegions = regions.Where(r => r.IsPortal);

            foreach (var portal in portalRegions)
            {
                if (portal.WarpTo == targetMapId)
                {
                    return (portal.Start, portal.End);
                }
            }

            return null;
        }

        public class MapInfo
        {
            public int Id { get; set; }
            public string SceneName { get; set; }
            public bool IsCommon { get; set; }
            public float WorldOriginX { get; set; }
            public float WorldOriginY { get; set; }
            public InitCellData InitCell { get; set; }

            public (Cell position, bool isFlip) GetInitialPosition()
            {
                return (new Cell(InitCell.position.x, InitCell.position.y), InitCell.isFlip);
            }
        }

        public class MapRegion
        {
            public string RegionType { get; set; }
            public Cell Start { get; set; }
            public Cell End { get; set; }
            public MapId WarpTo { get; set; }

            public bool IsPortal => RegionType.Equals("portal", StringComparison.OrdinalIgnoreCase);
        }

        public class ChairInfo
        {
            public Cell Position { get; set; }
            public bool IsFlip { get; set; }

            public ChairInfo(Cell position, bool isFlip)
            {
                Position = position;
                IsFlip = isFlip;
            }
        }

        public class AreaRegion
        {
            public AreaType AreaType { get; set; }
            public Cell Start { get; set; }
            public Cell End { get; set; }

            public bool Contains(Cell position)
            {
                return position.X >= Start.X && position.X <= End.X &&
                       position.Y >= Start.Y && position.Y <= End.Y;
            }
        }
    }
}
