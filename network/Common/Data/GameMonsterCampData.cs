using System.Collections.Generic;
using network.common.data.helpers;
using network.common.data.models;

namespace network.common.data
{
    /// <summary>
    ///     몬스터 캠프 커스텀 앵커 (#219). 구역·캠프 인덱스별 셀을 지정하면 서버가
    ///     절차 배치(중심 반경 6·120°) 대신 이 셀에 캠프를 세운다. 행이 없는 캠프는
    ///     절차 배치로 폴백한다. 씬 저작은 Tools/Monster Camps 에디터 툴로 한다.
    /// </summary>
    public static class GameMonsterCampData
    {
        private static readonly Dictionary<(int Area, int CampIndex), Cell> _anchors = new();

        public static void Initialize(List<CsvRow> rows)
        {
            _anchors.Clear();
            if (rows == null) return;

            foreach (var row in rows)
            {
                int area = int.Parse(row["area_type"]);
                int campIndex = int.Parse(row["camp_index"]);
                int cellX = int.Parse(row["cell_x"]);
                int cellY = int.Parse(row["cell_y"]);
                _anchors[(area, campIndex)] = new Cell(cellX, cellY);
            }
        }

        /// <summary>커스텀 앵커 셀. 지정이 없으면 null — 호출부는 절차 배치로 폴백한다.</summary>
        public static Cell GetAnchor(AreaType area, int campIndex) =>
            _anchors.TryGetValue(((int)area, campIndex), out var cell) ? cell : null;

        public static int Count => _anchors.Count;
    }
}
