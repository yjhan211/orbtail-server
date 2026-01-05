// ReSharper disable All
#pragma warning disable CS8618 // 생성자를 종료할 때 null을 허용하지 않는 필드에 null이 아닌 값을 포함해야 합니다. null 허용으로 선언해 보세요.
#pragma warning disable CS8625 // Null 리터럴을 null을 허용하지 않는 참조 형식으로 변환할 수 없습니다.
#pragma warning disable CS8603 // 가능한 null 참조 반환입니다.

using System.Collections.Generic;
using network.common.data.helpers;

namespace network.common.data
{
    public static class GameAreaExitConditionData
    {
        // area_type -> AreaExitConditionInfoData
        private static readonly Dictionary<AreaType, AreaExitConditionInfoData> Conditions = new();

        public static void Initialize(List<CsvRow> csvData)
        {
            Conditions.Clear();

            foreach (var row in csvData)
            {
                var condition = AreaExitConditionInfoData.CreateFromData(row);
                Conditions[condition.AreaType] = condition;
            }
        }

        /// <summary>
        /// 특정 Area의 퇴장 조건 가져오기
        /// </summary>
        public static AreaExitConditionInfoData Get(AreaType areaType)
        {
            return Conditions.TryGetValue(areaType, out var condition) ? condition : null;
        }

        /// <summary>
        /// 특정 Area에서 나가는 데 필요한 최소 Step 확인
        /// </summary>
        public static int GetRequiredStep(AreaType areaType)
        {
            var condition = Get(areaType);
            return condition?.RequiredStep ?? 0; // 0이면 제한 없음
        }

        /// <summary>
        /// 특정 Area에서 나갈 수 있는지 확인
        /// required_step이 완료되어야 퇴장 가능 (currentStep > requiredStep)
        /// </summary>
        public static bool CanExitArea(AreaType areaType, int currentStep)
        {
            var requiredStep = GetRequiredStep(areaType);
            return requiredStep == 0 || currentStep > requiredStep;
        }

        /// <summary>
        /// 특정 Area의 Fallback 위치 가져오기 (퇴장 불가 시 텔레포트할 위치)
        /// </summary>
        public static (float x, float y)? GetFallbackPosition(AreaType areaType)
        {
            var condition = Get(areaType);
            if (condition == null || (condition.FallbackX == 0 && condition.FallbackY == 0))
                return null;
            return (condition.FallbackX, condition.FallbackY);
        }
    }

    public class AreaExitConditionInfoData
    {
        public AreaType AreaType { get; private set; }
        public int RequiredStep { get; private set; } // 이 Area에서 나가려면 완료해야 할 Exit Step
        public int MessageTextId { get; private set; } // 조건 불충족 시 표시할 메시지 ID
        public float FallbackX { get; private set; } // 조건 불충족 시 텔레포트할 X 좌표
        public float FallbackY { get; private set; } // 조건 불충족 시 텔레포트할 Y 좌표
        public int RequiredItemId { get; private set; } // 퇴장에 필요한 아이템 ID (0이면 체크 안 함)

        public static AreaExitConditionInfoData CreateFromData(CsvRow row)
        {
            return new AreaExitConditionInfoData
            {
                AreaType = (AreaType)int.Parse(row["area_type"]),
                RequiredStep = int.Parse(row["required_step"]),
                MessageTextId = row.ContainsKey("message_text_id") ? int.Parse(row["message_text_id"]) : 0,
                FallbackX = row.ContainsKey("fallback_x") ? float.Parse(row["fallback_x"]) : 0,
                FallbackY = row.ContainsKey("fallback_y") ? float.Parse(row["fallback_y"]) : 0,
                RequiredItemId = row.ContainsKey("required_item_id") ? int.Parse(row["required_item_id"]) : 0
            };
        }
    }
}
