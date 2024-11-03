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
    public static class InstanceMapData
    {
        private static int _totalServerNum;

        private static readonly Dictionary<string, (MapId, Cell, bool)> PortalInfo = new()
        {
            // { GetPortalKey(MapID.LAB_1, new(91, 100)), (MapID.CAMPUS_1, new(110, 91), false) }
        };

        public static void Initialize(int totalServerNum)
        {
            _totalServerNum = totalServerNum;
        }

        public static string CreatePartKey(MapId mapId, long mapSubId)
        {
            return $"map_{mapId}|{mapSubId}";
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

        public static int GetManageServerId(long mapSubId)
        {
            return (int)((mapSubId - 1) % _totalServerNum) + 1;
        }
    }
}