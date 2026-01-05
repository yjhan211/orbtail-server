// ReSharper disable All
#pragma warning disable CS8618 // 생성자를 종료할 때 null을 허용하지 않는 필드에 null이 아닌 값을 포함해야 합니다. null 허용으로 선언해 보세요.
#pragma warning disable CS8625 // Null 리터럴을 null을 허용하지 않는 참조 형식으로 변환할 수 없습니다.
#pragma warning disable CS8603 // 가능한 null 참조 반환입니다.

using System.Collections.Generic;
using network.common.data.helpers;

namespace network.common.data
{
    public static class GameDoorData
    {
        // door_id -> DoorInfoData
        private static readonly Dictionary<int, DoorInfoData> Doors = new();

        public static void Initialize(List<CsvRow> csvData)
        {
            Doors.Clear();

            foreach (var row in csvData)
            {
                var door = DoorInfoData.CreateFromData(row);
                Doors[door.DoorId] = door;
            }
        }

        /// <summary>
        /// 특정 문 정보 가져오기
        /// </summary>
        public static DoorInfoData Get(int doorId)
        {
            return Doors.TryGetValue(doorId, out var door) ? door : null;
        }

        /// <summary>
        /// 모든 문 정보 가져오기
        /// </summary>
        public static IEnumerable<DoorInfoData> GetAll()
        {
            return Doors.Values;
        }
    }

    public class DoorInfoData
    {
        public int DoorId { get; private set; }
        public int RequiredItemId { get; private set; } // 문을 열기 위해 필요한 아이템 ID (0이면 열쇠 불필요)
        public float PositionX { get; private set; }
        public float PositionY { get; private set; }
        public float InteractDistance { get; private set; } // 상호작용 가능 거리
        public string LocationName { get; private set; } // 문이 위치한 장소 이름 (알럿 표시용)

        public static DoorInfoData CreateFromData(CsvRow row)
        {
            return new DoorInfoData
            {
                DoorId = int.Parse(row["door_id"]),
                RequiredItemId = row.ContainsKey("required_item_id") ? int.Parse(row["required_item_id"]) : 0,
                PositionX = row.ContainsKey("position_x") ? float.Parse(row["position_x"]) : 0,
                PositionY = row.ContainsKey("position_y") ? float.Parse(row["position_y"]) : 0,
                InteractDistance = row.ContainsKey("interact_distance") ? float.Parse(row["interact_distance"]) : 3f,
                LocationName = row.ContainsKey("location_name") ? row["location_name"] : "알 수 없는 장소"
            };
        }
    }
}
