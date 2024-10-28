// ReSharper disable All
#pragma warning disable CS8618 // 생성자를 종료할 때 null을 허용하지 않는 필드에 null이 아닌 값을 포함해야 합니다. null 허용으로 선언해 보세요.
#pragma warning disable CS8625 // Null 리터럴을 null을 허용하지 않는 참조 형식으로 변환할 수 없습니다.
#pragma warning disable CS8603 // 가능한 null 참조 반환입니다.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics.CodeAnalysis;
using Newtonsoft.Json;
using network.common.data.helpers;
using network.common.data.models;
using network.managers;

namespace network.common.data
{
    // 현재 공통맵은 동일한 맵 크기, 동일한 파티셔닝 구조로 감. 추후 달라진다면 MapHelper 분리 필요
    public static class CommonMapData
    {
        private static int _totalServerNum;
        private static readonly Dictionary<string, int> PartByPositionKey = new Dictionary<string, int>();
        private static readonly Dictionary<MapId, Dictionary<int, List<string>>> PositionListByMapPart =
            new Dictionary<MapId, Dictionary<int, List<string>>>();

        private static readonly Dictionary<MapId, List<int>> GenExploreIdList = new Dictionary<MapId, List<int>>();

        private static readonly List<Cell> PartPivotList =
            new()
            {
                new Cell(25, 85), new Cell(31, 79), new Cell(37, 73), new Cell(43, 67), new Cell(49, 61),
                new Cell(55, 55), new Cell(61, 49), new Cell(67, 43), new Cell(73, 37), new Cell(79, 31),
                new Cell(45, 105), new Cell(51, 99), new Cell(57, 93), new Cell(63, 87), new Cell(69, 81),
                new Cell(75, 75), new Cell(81, 69), new Cell(87, 63), new Cell(93, 57), new Cell(99, 51),
                new Cell(65, 125), new Cell(71, 119), new Cell(77, 113), new Cell(83, 107), new Cell(89, 101),
                new Cell(95, 95), new Cell(101, 89), new Cell(107, 83), new Cell(113, 77), new Cell(119, 71),
                new Cell(85, 145), new Cell(91, 139), new Cell(97, 133), new Cell(103, 127), new Cell(109, 121),
                new Cell(115, 115), new Cell(121, 109), new Cell(127, 103), new Cell(133, 97), new Cell(139, 91)
            };

        private static readonly int[][] MapPartition =
            new int[][]
            {
                new[] { 1, 2 }, new[] { 11, 12 }, new[] { 3, 4 }, new[] { 13, 14 },
                new[] { 5, 6 }, new[] { 15, 16 }, new[] { 7, 8 }, new[] { 17, 18 },
                new[] { 9, 10 }, new[] { 19, 20 }, new[] { 21, 22 }, new[] { 31, 32 },
                new[] { 23, 24 }, new[] { 33, 34 }, new[] { 25, 26 }, new[] { 35, 36 },
                new[] { 27, 28 }, new[] { 37, 38 }, new[] { 29, 30 }, new[] { 39, 40 }
            };

        // ReSharper disable once CollectionNeverUpdated.Local
        private static readonly Dictionary<string, (MapId, Cell, bool)> PortalInfo = new Dictionary<string, (MapId, Cell, bool)>
        {
            // { GetPortalKey(MapID.CAMPUS_1, new(117, 110)), (MapID.FACTORY_1, new(39, 85), true) },
            // { GetPortalKey(MapID.CAMPUS_1, new(118, 110)), (MapID.FACTORY_1, new(39, 85), true) },
            // { GetPortalKey(MapID.FACTORY_1, new(39, 90)), (MapID.CAMPUS_1, new(114, 108), true) },
            // { GetPortalKey(MapID.FACTORY_1, new(40, 90)), (MapID.CAMPUS_1, new(114, 108), true) },
            // { GetPortalKey(MapID.CAMPUS_1, new(76, 29)), (MapID.WETLAND_1, new(113, 91), false) },
            // { GetPortalKey(MapID.CAMPUS_1, new(76, 30)), (MapID.WETLAND_1, new(113, 91), false) },
            // { GetPortalKey(MapID.WETLAND_1, new(122, 94)), (MapID.CAMPUS_1, new(69, 29), true) },
            // { GetPortalKey(MapID.WETLAND_1, new(122, 95)), (MapID.CAMPUS_1, new(69, 29), true) },
            // { GetPortalKey(MapID.CAMPUS_1, new(114, 92)), (MapID.LAB_1, new(90, 96), true) },
        };

        public static List<string> GetPositionListByMapByPart(MapId mapId, int part)
        {
            return PositionListByMapPart[mapId][part];
        }

        public static void Initialize(int totalServerNum)
        {
            _totalServerNum = totalServerNum;
            // TODO 공통맵 생기면 추가
            // ReSharper disable once RedundantEmptyObjectOrCollectionInitializer
            foreach (var mapId in new List<MapId> { })
            {
                PositionListByMapPart[mapId] = new Dictionary<int, List<string>>();

                var partNumber = 1;
                var boundCellLists = PartPivotList.Select(partPivotCell => partPivotCell.GetBoundCellList());
                foreach (var cellList in boundCellLists)
                {
                    PositionListByMapPart[mapId][partNumber] = new();

                    var uniquePositionKeys = cellList.Select(cell => CreatePartKey(mapId, cell))
                        .Where(positionKey => !PartByPositionKey.TryGetValue(positionKey, out _));

                    foreach (var positionKey in uniquePositionKeys)
                    {
                        PartByPositionKey.Add(positionKey, partNumber);
                        PositionListByMapPart[mapId][partNumber].Add(positionKey);
                    }

                    partNumber++;
                }
            }

            // TODO 공통맵 생기면 추가
            // GenExploreIdList.Add(MapId.CAMPUS_1, [100001]);
        }

        public static bool IsCommonMap(MapId mapId)
        {
            return mapId switch
            {
                MapId.LIBRARY => false,
                _ => true
            };
        }

        public static string CreatePartKey(MapId mapId, Cell cell)
        {
            return $"{mapId}|{cell.X},{cell.Y}";
        }

        private static string GetPortalKey(MapId mapId, Cell cell)
        {
            return $"{mapId}|{cell.X},{cell.Y}";
        }

        public static (MapId mapId, Cell spawnPosition, bool isFlip)? GetPortalOrNull(GameObjectInfo objectInfo)
        {
            var portalKey = GetPortalKey(objectInfo.MapId, objectInfo.TargetCell);
            if (PortalInfo.TryGetValue(portalKey, out var portalInfo)) return portalInfo;

            return null;
        }

        public static int GetManageServerId(string positionKey)
        {
            if (!PartByPositionKey.TryGetValue(positionKey, out var partNumber)) return 0;

            var serverId = _totalServerNum switch
            {
                40 => partNumber,
                20 => Array.FindIndex(MapPartition, parts => parts.Contains(partNumber)) + 1,
                10 => (Array.FindIndex(MapPartition, parts => parts.Contains(partNumber)) + 1 + 1) / 2,
                5 => (Array.FindIndex(MapPartition, parts => parts.Contains(partNumber)) + 1 + 3) / 4,
                2 => Array.FindIndex(MapPartition, parts => parts.Contains(partNumber)) < 10 ? 1 : 2,
                _ => throw new Exception("Invalid TotalServerNum")
            };

            return serverId;
        }

        public static Cell CreateCell(string partKey)
        {
            var split = partKey.Split("|");
            var position = split[1].Split(",");

            return new Cell(int.Parse(position[0]), int.Parse(position[1]));
        }

        public static List<int> GetGenExploreIdList(MapId mapId)
        {
            return GenExploreIdList.GetValueOrDefault(mapId);
        }

        public static List<int> GetBoundServerList(MapId mapId, Cell cell)
        {
            return cell.GetBoundCellList().Select(boundCell => CreatePartKey(mapId, boundCell))
                .Select(GetManageServerId)
                .Where(serverId => serverId != 0)
                .Distinct()
                .ToList();
        }

        public static int GetManagePartByPositionKey(string positionKey)
        {
            if (!PartByPositionKey.TryGetValue(positionKey, out var partNumber))
                throw new Exception($"Can't find part_number for position_key: {positionKey}");

            var serverId = GetManageServerId(positionKey);
            var managePart = _totalServerNum switch
            {
                40 => partNumber,
                20 => MapPartition[serverId - 1][0],
                10 => MapPartition[(serverId - 1) * 2][0],
                5 => MapPartition[(serverId - 1) * 4][0],
                2 => serverId == 1 ? MapPartition[0][0] : MapPartition[10][0],
                _ => throw new Exception("Invalid TotalServerNum")
            };

            return managePart;
        }

        public static List<int> GetManagePartList(int gameServerId)
        {
            var result = new List<int>();

            switch (_totalServerNum)
            {
                case 40:
                    result.Add(gameServerId);
                    break;

                case 20:
                    result.AddRange(MapPartition[gameServerId - 1]);
                    break;

                case 10:
                    for (var i = 2 * gameServerId - 2; i < 2 * gameServerId; i++) result.AddRange(MapPartition[i]);
                    break;

                case 5:
                    for (var i = 4 * (gameServerId - 1); i < 4 * gameServerId; i++) result.AddRange(MapPartition[i]);
                    break;

                case 2:
                    var startIndex = gameServerId == 1 ? 0 : 10;
                    for (var i = startIndex; i < startIndex + 10; i++) result.AddRange(MapPartition[i]);
                    break;

                default:
                    throw new Exception("Invalid TotalServerNum");
            }

            return result;
        }

        public static Cell GetRandomCell()
        {
            var random = new Random();
            return PartPivotList[random.Next(0, PartPivotList.Count)];
        }
    }
}