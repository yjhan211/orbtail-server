// ReSharper disable All
#pragma warning disable CS8603 // 가능한 null 참조 반환입니다.
#pragma warning disable CS8618 // 생성자를 종료할 때 null을 허용하지 않는 필드에 null이 아닌 값을 포함해야 합니다. null 허용으로 선언해 보세요.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
        private static readonly Dictionary<string, (MapId, Cell, bool)> _portalInfo = new();

        public static void Initialize(List<CsvRow> mapInfo, List<CsvRow> mapRegion)
        {
            InitializeMapInfo(mapInfo);
            InitializeMapRegions(mapRegion);
        }

        private static void InitializeMapInfo(List<CsvRow> data)
        {
            foreach (var row in data)
            {
                var initCell = ParseInitCell(row["init_cell"].Trim('"'));
                var mapInfo = new MapInfo
                {
                    Id = int.Parse(row["id"]),
                    SceneName = row["scene_name"],
                    IsCommon = int.Parse(row["is_common"]) == 1,
                    InitialPosition = new Cell(initCell.x, initCell.y),
                    InitialIsGround = initCell.isGround,
                    Comment = row["comment"]
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

                if (region.IsPortal)
                {
                    var key = GetPortalKey(mapId, region.Start);
                    var targetMapInfo = GetMapInfo(region.WarpTo);
                    _portalInfo[key] = (region.WarpTo, targetMapInfo.InitialPosition, true);
                }
            }
        }

        private static (int x, int y, bool isGround) ParseInitCell(string initCell)
        {
            var trimmed = initCell.Trim('(', ')');
            var parts = trimmed.Split(',');
            return (
                int.Parse(parts[0].Trim()),
                int.Parse(parts[1].Trim()),
                bool.Parse(parts[2].Trim())
            );
        }

        private static string GetPortalKey(MapId mapId, Cell cell)
        {
            return $"{mapId}|{cell.X},{cell.Y}";
        }

        public static MapInfo GetMapInfo(MapId id)
        {
            return _mapInfos.TryGetValue(id, out var info) ? info : null;
        }

        public static List<MapRegion> GetMapRegions(MapId mapId)
        {
            return _mapRegions.TryGetValue(mapId, out var regions) ? regions : new List<MapRegion>();
        }

        public static bool IsCommonMap(MapId mapId)
        {
            return GetMapInfo(mapId)?.IsCommon ?? false;
        }

        public static bool IsOutOfMapPosition(MapId mapId, Cell position)
        {
            var regions = GetMapRegions(mapId);
            var groundRegions = regions.Where(r => r.RegionType.Equals("ground", StringComparison.OrdinalIgnoreCase));

            foreach (var region in groundRegions)
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
            var portalKey = GetPortalKey(objectInfo.MapId, objectInfo.TargetCell);
            return _portalInfo.TryGetValue(portalKey, out var portalInfo) ? portalInfo : null;
        }
        
        public static List<MapId> GetCommonMapList()
        {
            return _mapInfos.Where(x => x.Value.IsCommon).Select(x => x.Key).ToList();
        }
        
        public static void Validate(LogManager logManager)
        {
            try
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

                    // if (IsOutOfMapPosition(mapInfo.Id, mapInfo.InitialPosition))
                    // {
                    //     throw new Exception($"Map ID {mapInfo.Id} has invalid initial position: {mapInfo.InitialPosition}");
                    // }
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

                        if (region.IsPortal && !_mapInfos.ContainsKey(region.WarpTo))
                        {
                            throw new Exception($"Portal in map {mapId} warps to non-existent map {region.WarpTo}");
                        }
                    }
                }

                foreach (var (key, portalInfo) in _portalInfo)
                {
                    var (targetMapId, spawnPosition, _) = portalInfo;
                    if (!_mapInfos.ContainsKey(targetMapId))
                    {
                        throw new Exception($"Portal {key} targets non-existent map {targetMapId}");
                    }
                    // TODO 맵 전부 추가 후에 활성화
                    // if (IsOutOfMapPosition(targetMapId, spawnPosition))
                    // {
                    //     throw new Exception($"Portal {key} has invalid spawn position in target map {targetMapId}: {spawnPosition}");
                    // }
                }

                logManager.WriteDebugLog("Map data validation completed successfully!");
            }
            catch (Exception ex)
            {
                logManager.WriteDebugLog($"{ex.Message}");
            }
        }
    }

    public class MapInfo
    {
        public int Id { get; set; }
        public string SceneName { get; set; }
        public bool IsCommon { get; set; }
        public Cell InitialPosition { get; set; }
        public bool InitialIsGround { get; set; }
        public string Comment { get; set; }
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