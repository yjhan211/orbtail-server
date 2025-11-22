// ReSharper disable All
#pragma warning disable CS8603 // 가능한 null 참조 반환입니다.
#pragma warning disable CS8618 // 생성자를 종료할 때 null을 허용하지 않는 필드에 null이 아닌 값을 포함해야 합니다. null 허용으로 선언해 보세요.

using System;
using System.Collections.Generic;
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
        private static readonly Dictionary<MapId, HashSet<Cell>> _runtimeObstacles = new();

        public static void Initialize(List<CsvRow> mapInfo, List<CsvRow> mapRegion)
        {
            InitializeMapInfo(mapInfo);
            InitializeMapRegions(mapRegion);
        }

        private static void InitializeMapInfo(List<CsvRow> data)
        {
            foreach (var row in data)
            {
                var initCellX = int.Parse(row["init_cell_x"]);
                var initCellY = int.Parse(row["init_cell_y"]);
                var isFlip = int.Parse(row["is_flip"]) == 1;

                var mapInfo = new MapInfo
                {
                    Id = int.Parse(row["id"]),
                    SceneName = row["scene_name"],
                    IsCommon = int.Parse(row["is_common"]) == 1,
                    InitCell = new InitCellData(
                        new Vector3Int(initCellX, initCellY, 0),
                        MapId.None,
                        isFlip
                    )
                };

                _mapInfos[MapId.Parse<MapId>(mapInfo.Id.ToString())] = mapInfo;
            }
        }

        private static void InitializeMapRegions(List<CsvRow> data)
        {
            foreach (var row in data)
            {
                var rawMapId = int.Parse(row["map_id"]);
                var mapId = MapId.Parse<MapId>(rawMapId.ToString());

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
                    var areaTypeId = int.Parse(row["warp_to"]);
                    var areaType = AreaType.Parse<AreaType>(areaTypeId.ToString());

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
                    WarpTo = MapId.Parse<MapId>(int.Parse(row["warp_to"]).ToString())
                };

                if (!_mapRegions.ContainsKey(mapId))
                {
                    _mapRegions[mapId] = new List<MapRegion>();
                }
                _mapRegions[mapId].Add(region);
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

        public static List<MapId> GetCommonMapList()
        {
            return _mapInfos
                .Where(pair => pair.Value.IsCommon)
                .Select(pair => pair.Key)
                .ToList();
        }

        public static bool IsCommonMap(MapId mapId)
        {
            var mapInfo = GetMapInfo(mapId);
            return mapInfo.IsCommon;
        }

        public static bool IsMoveablePosition(MapId mapId, Cell position)
        {
            // 런타임 오버라이드 체크 (Inspector에서 추가한 장애물)
            if (_runtimeObstacles.TryGetValue(mapId, out var obstacles))
            {
                if (obstacles.Contains(position))
                {
                    return false;
                }
            }

            var regions = GetMapRegions(mapId);
            var groundRegions = regions.Where(r => r.RegionType.Equals("ground", StringComparison.OrdinalIgnoreCase));
            var isInGround = false;
            foreach (var region in groundRegions)
            {
                if (position.X >= region.Start.X && position.X <= region.End.X &&
                    position.Y >= region.Start.Y && position.Y <= region.End.Y)
                {
                    isInGround = true;
                    break;
                }
            }

            if (!isInGround)
            {
                return false;
            }

            var obstacleRegions =
                regions.Where(r => r.RegionType.Equals("obstacle", StringComparison.OrdinalIgnoreCase));
            foreach (var region in obstacleRegions)
            {
                if (position.X >= region.Start.X && position.X <= region.End.X &&
                    position.Y >= region.Start.Y && position.Y <= region.End.Y)
                {
                    return false;
                }
            }

            return true;
        }

        // 런타임 장애물 추가/제거 (에디터 전용)
        public static void SetRuntimeObstacles(MapId mapId, IEnumerable<Vector3Int> obstaclePositions)
        {
            if (!_runtimeObstacles.ContainsKey(mapId))
            {
                _runtimeObstacles[mapId] = new HashSet<Cell>();
            }

            _runtimeObstacles[mapId].Clear();

            foreach (var pos in obstaclePositions)
            {
                _runtimeObstacles[mapId].Add(new Cell(pos.x, pos.y));
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

        // 특정 맵의 모든 Area 가져오기
        public static List<AreaRegion> GetAreas(MapId mapId)
        {
            return _areaRegions.TryGetValue(mapId, out var areas) ? areas : new List<AreaRegion>();
        }

        public static (MapId mapId, Cell spawnPosition, bool isFlip)? GetPortalOrNull(GameObjectInfo objectInfo, bool isTutorial)
        {
            var currentCell = objectInfo.TargetCell;
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
        /// 주어진 셀이 특정 포탈 영역에 포함되는지 확인하고, 포함되지 않으면 포탈의 중심 좌표를 반환합니다.
        /// </summary>
        /// <param name="currentMapId">현재 맵 ID</param>
        /// <param name="targetMapId">목표 맵 ID</param>
        /// <param name="cellToCheck">확인할 셀 좌표</param>
        /// <returns>셀이 포탈 영역에 포함되면 null, 포함되지 않으면 포탈의 중심 좌표</returns>
        public static Cell? GetPortalCenterIfNotInPortal(MapId currentMapId, MapId targetMapId, Cell cellToCheck)
        {
            var portalCoords = GetPortalCoordinates(currentMapId, targetMapId);
            if (!portalCoords.HasValue)
            {
                return null; // 포탈이 존재하지 않음
            }

            var (start, end) = portalCoords.Value;

            // 셀이 포탈 영역에 포함되는지 확인
            bool isInPortal = cellToCheck.X >= start.X && cellToCheck.X <= end.X &&
                              cellToCheck.Y >= start.Y && cellToCheck.Y <= end.Y;

            if (isInPortal)
            {
                return null; // 이미 포탈 영역에 있음
            }

            // 포탈 중심 좌표 계산 및 반환
            var centerX = (start.X + end.X) / 2;
            var centerY = (start.Y + end.Y) / 2;
            return new Cell(centerX, centerY);
        }

        /// <summary>
        /// 주어진 셀이 포탈 영역에 포함되는지 확인합니다.
        /// </summary>
        /// <param name="currentMapId">현재 맵 ID</param>
        /// <param name="targetMapId">목표 맵 ID</param>
        /// <param name="cellToCheck">확인할 셀 좌표</param>
        /// <returns>셀이 포탈 영역에 포함되면 true, 그렇지 않으면 false</returns>
        public static bool IsInPortalArea(MapId currentMapId, MapId targetMapId, Cell cellToCheck)
        {
            var portalCoords = GetPortalCoordinates(currentMapId, targetMapId);
            if (!portalCoords.HasValue)
            {
                return false; // 포탈이 존재하지 않음
            }

            var (start, end) = portalCoords.Value;

            return cellToCheck.X >= start.X && cellToCheck.X <= end.X &&
                   cellToCheck.Y >= start.Y && cellToCheck.Y <= end.Y;
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

        public static void Validate()
        {
            // if (_mapInfos.Count == 0)
            // {
            //     throw new Exception("No map information loaded");
            // }
            //
            // foreach (var mapInfo in _mapInfos.Values)
            // {
            //     if (string.IsNullOrEmpty(mapInfo.SceneName))
            //     {
            //         throw new Exception($"Map ID {mapInfo.Id} has no scene name");
            //     }
            //
            //     foreach (var (fromMap, initCell) in mapInfo.InitCells)
            //     {
            //         var position = new Cell(initCell.position.x, initCell.position.y);
            //         if (!IsMoveablePosition((MapId)mapInfo.Id, position))
            //         {
            //             throw new Exception(
            //                 $"Map ID {mapInfo.Id} has invalid initial position from {fromMap}: {position}");
            //         }
            //     }
            // }
            //
            // foreach (var (mapId, regions) in _mapRegions)
            // {
            //     if (!_mapInfos.ContainsKey(mapId))
            //     {
            //         throw new Exception($"Map region references non-existent map ID: {mapId}");
            //     }
            //
            //     if (!regions.Any(r => r.RegionType.Equals("ground", StringComparison.OrdinalIgnoreCase)))
            //     {
            //         throw new Exception($"Map ID {mapId} has no ground regions");
            //     }
            //
            //     foreach (var region in regions)
            //     {
            //         if (region.Start.X > region.End.X || region.Start.Y > region.End.Y)
            //         {
            //             throw new Exception(
            //                 $"Invalid region coordinates in map {mapId}: Start({region.Start}) -> End({region.End})");
            //         }
            //
            //         if (mapId == MapId.Camp)
            //         {
            //             continue;
            //         }
            //
            //         if (region.IsPortal)
            //         {
            //             if (!_mapInfos.ContainsKey(region.WarpTo))
            //             {
            //                 throw new Exception($"Portal in map {mapId} warps to non-existent map {region.WarpTo}");
            //             }
            //
            //             var targetMapInfo = GetMapInfo(region.WarpTo);
            //             if (!targetMapInfo.InitCells.TryGetValue(mapId, out var initData))
            //             {
            //                 throw new Exception(
            //                     $"Portal in map {mapId} has no spawn position in target map {region.WarpTo}");
            //             }
            //
            //             var spawnPosition = new Cell(initData.position.x, initData.position.y);
            //             if (!IsMoveablePosition(region.WarpTo, spawnPosition))
            //             {
            //                 throw new Exception(
            //                     $"Portal in map {mapId} has invalid spawn position in target map {region.WarpTo}: {spawnPosition}");
            //             }
            //         }
            //     }
            // }
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

        public static Cell? GetPortalCenterCoordinates(MapId currentMapId, MapId targetMapId)
        {
            var portalCoords = GetPortalCoordinates(currentMapId, targetMapId);
            if (portalCoords.HasValue)
            {
                var (start, end) = portalCoords.Value;
                var centerX = (start.X + end.X) / 2;
                var centerY = (start.Y + end.Y) / 2;
                return new Cell(centerX, centerY);
            }

            return null;
        }

        public class MapInfo
        {
            public int Id { get; set; }
            public string SceneName { get; set; }
            public bool IsCommon { get; set; }
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