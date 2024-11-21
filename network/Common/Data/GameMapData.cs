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
    public static class GameMapData
    {
        private static readonly Dictionary<MapId, MapInfo> _mapInfos = new();
        private static readonly Dictionary<MapId, List<MapRegion>> _mapRegions = new();

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
                    InitCells = new Dictionary<MapId, Cell>()
                };

                _mapInfos[MapId.Parse<MapId>(mapInfo.Id.ToString())] = mapInfo;
            }
        }

        private static void InitializeInitCellFromMapRegion(List<CsvRow> data)
        {
            var groundRegions = data.Where(row => row["region_type"].Equals("ground", StringComparison.OrdinalIgnoreCase));
    
            foreach (var row in groundRegions)
            {
                var mapId = MapId.Parse<MapId>(int.Parse(row["map_id"]).ToString());
                var initCellX = int.Parse(row["init_cell_x"]);
                var initCellY = int.Parse(row["init_cell_y"]);
                var fromMap = MapId.Parse<MapId>(int.Parse(row["from_map"]).ToString());

                if (initCellX != 0 || initCellY != 0 || fromMap != MapId.NONE)
                {
                    if (_mapInfos.TryGetValue(mapId, out var mapInfo))
                    {
                        mapInfo.InitCells[fromMap] = new Cell(initCellX, initCellY);
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

            var obstacleRegions = regions.Where(r => r.RegionType.Equals("obstacle", StringComparison.OrdinalIgnoreCase));
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

        public static (MapId mapId, Cell spawnPosition, bool isFlip)? GetPortalOrNull(GameObjectInfo objectInfo)
        {
            var currentCell = objectInfo.TargetCell;
            var regions = GetMapRegions(objectInfo.MapId);
            var portalRegions = regions.Where(r => r.IsPortal);

            foreach (var portal in portalRegions)
            {
                if (currentCell.X >= portal.Start.X && currentCell.X <= portal.End.X && 
                    currentCell.Y >= portal.Start.Y && currentCell.Y <= portal.End.Y)
                {
                    var targetMapInfo = GetMapInfo(portal.WarpTo);
                    return (portal.WarpTo, targetMapInfo.GetInitialPosition(objectInfo.MapId), true);
                }
            }
    
            return null;
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

                foreach (var (fromMap, position) in mapInfo.InitCells)
                {
                    if (!IsMoveablePosition((MapId)mapInfo.Id, position))
                    {
                        throw new Exception($"Map ID {mapInfo.Id} has invalid initial position from {fromMap}: {position}");
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
                        throw new Exception($"Invalid region coordinates in map {mapId}: Start({region.Start}) -> End({region.End})");
                    }

                    if (region.IsPortal)
                    {
                        if (!_mapInfos.ContainsKey(region.WarpTo))
                        {
                            throw new Exception($"Portal in map {mapId} warps to non-existent map {region.WarpTo}");
                        }
                            
                        var targetMapInfo = GetMapInfo(region.WarpTo);
                        if (!targetMapInfo.InitCells.TryGetValue(mapId, out var spawnPosition))
                        {
                            throw new Exception($"Portal in map {mapId} has no spawn position in target map {region.WarpTo}");
                        }
                        
                        if (!IsMoveablePosition(region.WarpTo, spawnPosition))
                        {
                            throw new Exception($"Portal in map {mapId} has invalid spawn position in target map {region.WarpTo}: {spawnPosition}");
                        }
                    }
                }
            }

            logManager.WriteDebugLog("Map data validation completed successfully!");
        }
    }

    public class MapInfo
    {
        public int Id { get; set; }
        public string SceneName { get; set; }
        public bool IsCommon { get; set; }
        public Dictionary<MapId, Cell> InitCells { get; set; }

        public Cell GetInitialPosition(MapId fromMap)
        {
            return InitCells.TryGetValue(fromMap, out var cell) ? cell : InitCells.First().Value;
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
}