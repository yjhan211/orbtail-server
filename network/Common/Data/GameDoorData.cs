// ReSharper disable All

#pragma warning disable CS8618 // 생성자를 종료할 때 null을 허용하지 않는 필드에 null이 아닌 값을 포함해야 합니다. null 허용으로 선언해 보세요.
using System.Collections.Generic;
using network.common.data.helpers;
using network.common.data.models;

namespace network.common.data
{
    public static class GameDoorData
    {
        private const float PassageRadiusPadding = 1f;

        // door_id -> DoorInfoData
        private static readonly Dictionary<int, DoorInfoData> _doors = new();

        public static void Initialize(List<CsvRow> csvData)
        {
            _doors.Clear();

            foreach (var row in csvData)
            {
                var door = DoorInfoData.CreateFromData(row);
                _doors[door.DoorId] = door;
            }
        }

        /// <summary>
        ///     특정 문 정보 가져오기
        /// </summary>
        public static DoorInfoData? Get(int doorId)
        {
            return _doors.TryGetValue(doorId, out var door) ? door : null;
        }

        /// <summary>
        ///     모든 문 정보 가져오기
        /// </summary>
        public static IEnumerable<DoorInfoData> GetAll()
        {
            return _doors.Values;
        }

        /// <summary>
        ///     특정 영역으로 연결된 문들 가져오기 — 양쪽 모두 본다 (#229).
        ///     폐쇄 잠금이 이 함수를 쓰는데 소유 구역 한쪽만 비교해, 상대편이 먼저 닫히는 문
        ///     다섯 개(16·17·19·21·22)가 폐쇄 뒤에도 열려 있었다.
        ///     통행 판정(GetDoorForTransition)은 원래부터 양쪽을 인정하므로 그 비대칭을 없앤다.
        /// </summary>
        public static IEnumerable<DoorInfoData> GetByAreaType(AreaType areaType)
        {
            foreach (var door in _doors.Values)
            {
                if (door.AreaType == areaType || door.AreaTypeB == areaType)
                {
                    yield return door;
                }
            }
        }

        /// <summary>
        /// Finds the door that permits a direct movement transition between two areas.
        /// A transition is valid only near a door assigned to either side of the boundary.
        /// </summary>
        public static DoorInfoData? GetDoorForTransition(
            AreaType currentArea,
            AreaType nextArea,
            Cell currentCell,
            Cell nextCell)
        {
            if (currentArea == nextArea || currentArea == AreaType.None || nextArea == AreaType.None)
                return null;

            DoorInfoData? nearestDoor = null;
            float nearestDistanceSquared = float.MaxValue;

            foreach (var door in _doors.Values)
            {
                if (door.AreaType != currentArea && door.AreaType != nextArea)
                    continue;

                float currentDistanceSquared = GetDistanceSquared(door, currentCell);
                float nextDistanceSquared = GetDistanceSquared(door, nextCell);
                float distanceSquared = currentDistanceSquared < nextDistanceSquared
                    ? currentDistanceSquared
                    : nextDistanceSquared;
                float transitionDistance = GetPassageRadius(door);

                if (distanceSquared > transitionDistance * transitionDistance ||
                    distanceSquared >= nearestDistanceSquared)
                    continue;

                nearestDistanceSquared = distanceSquared;
                nearestDoor = door;
            }

            return nearestDoor;
        }

        private static float GetPassageRadius(DoorInfoData door)
        {
            // 좁은 통과 반경 예외는 구 School 교실·창고 전용이었다 (#310에서 구역과 함께 제거).
            float interactionRadius = door.InteractDistance > 0f ? door.InteractDistance : 1f;
            return interactionRadius + PassageRadiusPadding;
        }

        private static float GetDistanceSquared(DoorInfoData door, Cell cell)
        {
            float dx = cell.X - door.PositionX;
            float dy = cell.Y - door.PositionY;
            return dx * dx + dy * dy;
        }
    }

    public class DoorInfoData
    {
        public int DoorId { get; private set; }
        public int RequiredItemId { get; private set; } // 문을 열기 위해 필요한 아이템 ID (0이면 열쇠 불필요)
        public float PositionX { get; private set; }
        public float PositionY { get; private set; }
        public float InteractDistance { get; private set; } // 상호작용 가능 거리
        public AreaType AreaType { get; private set; } // 문이 위치한 영역 타입

        // 문이 잇는 반대편 영역 (#229): 문은 두 구역의 간선인데 area_type 하나만 있어서
        // 폐쇄 잠금이 한쪽만 보고 있었다 — 교실1이 닫혀도 고사실 소속인 door 21은 안 잠겼다.
        // 통행 판정(GetDoorForTransition)은 원래부터 양쪽을 인정하고 있었다.
        public AreaType AreaTypeB { get; private set; }
        public bool IsInitiallyOpen { get; private set; } // 초기 열림 상태
        public int FallbackCellX { get; private set; } // 차단 시 텔레포트할 셀 X
        public int FallbackCellY { get; private set; } // 차단 시 텔레포트할 셀 Y

        // #272 문 등급 게이지(초). 0 = 기본값(Config.SWARM_DOOR_GAUGE_SECONDS) 사용.
        public float GaugeSeconds { get; private set; }

        // area_name.csv에서 이름 가져오기
        public string LocationName => GameAreaNameData.Get(AreaType);

        public static DoorInfoData CreateFromData(CsvRow row)
        {
            var posX = row.ContainsKey("position_x") ? float.Parse(row["position_x"]) : 0;
            var posY = row.ContainsKey("position_y") ? float.Parse(row["position_y"]) : 0;

            return new DoorInfoData
            {
                DoorId = int.Parse(row["door_id"]),
                RequiredItemId = row.ContainsKey("required_item_id") ? int.Parse(row["required_item_id"]) : 0,
                PositionX = posX,
                PositionY = posY,
                InteractDistance =
                    row.ContainsKey("interact_distance") ? float.Parse(row["interact_distance"]) : 3f,
                AreaType = row.ContainsKey("area_type")
                    ? CsvHelper.ParseDefinedEnum<AreaType>(row["area_type"], "door_info.area_type")
                    : AreaType.None,
                AreaTypeB = row.ContainsKey("area_type_b")
                    ? CsvHelper.ParseDefinedEnum<AreaType>(row["area_type_b"], "door_info.area_type_b")
                    : AreaType.None,
                IsInitiallyOpen = row.ContainsKey("is_initially_open") && row["is_initially_open"] == "1",
                FallbackCellX = row.ContainsKey("fallback_cell_x") ? int.Parse(row["fallback_cell_x"]) : (int)posX,
                FallbackCellY = row.ContainsKey("fallback_cell_y") ? int.Parse(row["fallback_cell_y"]) : (int)posY,
                GaugeSeconds = row.ContainsKey("gauge_seconds") ? float.Parse(row["gauge_seconds"]) : 0f
            };
        }
    }
}
