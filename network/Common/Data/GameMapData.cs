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
        
        private static readonly Dictionary<MapId, MapId> MapConversions = new()
        {
            { MapId.TutorialLibrary, MapId.Library },
            { MapId.TutorialSchool1, MapId.School1 },
            { MapId.TutorialSchool2, MapId.School2 },
            { MapId.TutorialClassroom, MapId.Classroom },
            { MapId.TutorialAdminoffice, MapId.Adminoffice },
            { MapId.TutorialGym, MapId.Gym },
            { MapId.TutorialGymstorage, MapId.Gymstorage },
            { MapId.TutorialSchoolground, MapId.Schoolground }
        };

        public static void Initialize(List<CsvRow> mapInfo, List<CsvRow> mapRegion)
        {
            InitializeMapInfo(mapInfo);
            InitializeInitCellFromMapRegion(mapRegion);
            InitializeMapRegions(mapRegion);
        }

        private static void InitializeMapInfo(List<CsvRow> data)
        {
            foreach (var row in data)
            {
                var mapInfo = new MapInfo
                {
                    Id = int.Parse(row["id"]),
                    SceneName = row["scene_name"],
                    IsCommon = int.Parse(row["is_common"]) == 1,
                    InitCells = new Dictionary<MapId, InitCellData>()
                };

                _mapInfos[MapId.Parse<MapId>(mapInfo.Id.ToString())] = mapInfo;
            }
        }

        private static void InitializeInitCellFromMapRegion(List<CsvRow> data)
        {
            var groundRegions =
                data.Where(row => row["region_type"].Equals("ground", StringComparison.OrdinalIgnoreCase));

            foreach (var row in groundRegions)
            {
                var mapId = MapId.Parse<MapId>(int.Parse(row["map_id"]).ToString());
                var initCellX = int.Parse(row["init_cell_x"]);
                var initCellY = int.Parse(row["init_cell_y"]);
                var fromMap = MapId.Parse<MapId>(int.Parse(row["from_map"]).ToString());
                var isFlip = int.Parse(row["is_flip"]) == 1;

                if (initCellX != 0 || initCellY != 0 || fromMap != MapId.None)
                {
                    if (_mapInfos.TryGetValue(mapId, out var mapInfo))
                    {
                        mapInfo.InitCells[fromMap] = new InitCellData(
                            new Vector3Int(initCellX, initCellY, 0),
                            fromMap,
                            isFlip
                        );
                    }
                }
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
            return GetMapInfo(mapId)?.IsCommon ?? false;
        }

        public static bool IsMoveablePosition(MapId mapId, Cell position)
        {
            mapId = ConvertMap(mapId);
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
        
        public static MapId ConvertMap(MapId mapId, bool toTutorial = false)
        {
            if (toTutorial)
            {
                if (mapId.ToString().StartsWith("Tutorial"))
                {
                    return mapId;
                }
           
                return MapConversions.FirstOrDefault(x => x.Value == mapId).Key;
            }

            if (!mapId.ToString().StartsWith("Tutorial"))
            {
                return mapId; 
            }
       
            return MapConversions.TryGetValue(mapId, out var convertedMap) ? convertedMap : mapId;
        }

        public static (MapId mapId, Cell spawnPosition, bool isFlip)? GetPortalOrNull(GameObjectInfo objectInfo, bool isTutorial)
        {
            var currentCell = objectInfo.TargetCell;
            var currentMap = ConvertMap(objectInfo.MapId);
            var regions = GetMapRegions(currentMap);
            var portalRegions = regions.Where(r => r.IsPortal);

            foreach (var portal in portalRegions)
            {
                if (currentCell.X >= portal.Start.X && currentCell.X <= portal.End.X &&
                    currentCell.Y >= portal.Start.Y && currentCell.Y <= portal.End.Y)
                {
                    var targetMapInfo = GetMapInfo(portal.WarpTo);

                    var convertCurrentMap = (portal.WarpTo == MapId.Camp) ? MapId.None : currentMap;
                    var (spawnPosition, isFlip) = targetMapInfo.GetInitialPosition(convertCurrentMap);
                    var warpMap = ConvertMap(portal.WarpTo, isTutorial);
                    
                    return (warpMap, spawnPosition, isFlip);
                }
            }

            return null;
        }
        
        public static ChairDirection GetChairDirection(MapId mapId, Cell position)
        {
            mapId = ConvertMap(mapId);
            if (!_chairInfos.ContainsKey(mapId))
                return ChairDirection.NONE;

            var chairInfo = _chairInfos[mapId].FirstOrDefault(c => 
                c.Position.X == position.X && c.Position.Y == position.Y);

            if (chairInfo == null)
                return ChairDirection.NONE;

            return chairInfo.IsFlip ? ChairDirection.RIGHT : ChairDirection.LEFT;
        }

        public static void Validate(LogManager logManager)
        {
            if (_mapInfos.Count == 0)
            {
                throw new Exception("No map information loaded");
            }

            foreach (var mapInfo in _mapInfos.Values)
            {
                if (string.IsNullOrEmpty(mapInfo.SceneName))
                {
                    throw new Exception($"Map ID {mapInfo.Id} has no scene name");
                }

                foreach (var (fromMap, initCell) in mapInfo.InitCells)
                {
                    var position = new Cell(initCell.position.x, initCell.position.y);
                    if (!IsMoveablePosition((MapId)mapInfo.Id, position))
                    {
                        throw new Exception(
                            $"Map ID {mapInfo.Id} has invalid initial position from {fromMap}: {position}");
                    }
                }
            }

            foreach (var (mapId, regions) in _mapRegions)
            {
                if (!_mapInfos.ContainsKey(mapId))
                {
                    throw new Exception($"Map region references non-existent map ID: {mapId}");
                }

                if (!regions.Any(r => r.RegionType.Equals("ground", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new Exception($"Map ID {mapId} has no ground regions");
                }

                foreach (var region in regions)
                {
                    if (region.Start.X > region.End.X || region.Start.Y > region.End.Y)
                    {
                        throw new Exception(
                            $"Invalid region coordinates in map {mapId}: Start({region.Start}) -> End({region.End})");
                    }

                    if (mapId == MapId.Camp)
                    {
                        continue;
                    }

                    if (region.IsPortal)
                    {
                        if (!_mapInfos.ContainsKey(region.WarpTo))
                        {
                            throw new Exception($"Portal in map {mapId} warps to non-existent map {region.WarpTo}");
                        }

                        var targetMapInfo = GetMapInfo(region.WarpTo);
                        if (!targetMapInfo.InitCells.TryGetValue(mapId, out var initData))
                        {
                            throw new Exception(
                                $"Portal in map {mapId} has no spawn position in target map {region.WarpTo}");
                        }

                        var spawnPosition = new Cell(initData.position.x, initData.position.y);
                        if (!IsMoveablePosition(region.WarpTo, spawnPosition))
                        {
                            throw new Exception(
                                $"Portal in map {mapId} has invalid spawn position in target map {region.WarpTo}: {spawnPosition}");
                        }
                    }
                }
            }

            logManager.WriteDebugLog("Map data validation completed successfully!");
        }

        public class MapInfo
        {
            public int Id { get; set; }
            public string SceneName { get; set; }
            public bool IsCommon { get; set; }
            public Dictionary<MapId, InitCellData> InitCells { get; set; }

            public (Cell position, bool isFlip) GetInitialPosition(MapId fromMap)
            {
                if (InitCells.TryGetValue(fromMap, out var initCell))
                {
                    return (new Cell(initCell.position.x, initCell.position.y), initCell.isFlip);
                }

                var defaultCell = InitCells.First().Value;
                return (new Cell(defaultCell.position.x, defaultCell.position.y), defaultCell.isFlip);
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
    }
}