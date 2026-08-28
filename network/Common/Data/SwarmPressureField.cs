using System;
using System.Collections.Generic;
using System.Linq;
using network.common.data.models;

namespace network.common.data
{
    /// <summary>
    ///     #272 자기장: 운동장 중심 기준 원형 수축 필드. 안전 거리 밖의 참가자에게 초과 거리에
    ///     비례한 오염을 준다 — 밀리는 방향이 곧 걸어야 하는 방향.
    ///     거리는 셀 등방 유클리드다 (2026-08-26 유저 결정: 배그식 원). 셀 공간의 원은 바닥면
    ///     (아이소, dy×2 정규화)의 원과 같아 기존 접촉·물폭탄 판정 타원 문법과 같은 결로 읽힌다.
    ///     보행 거리(BFS) 세대는 벽 위상은 정확했지만 경계가 원으로 읽히지 않아 퇴역 —
    ///     문 밖이 먼저 잠기는 방은 오염 경사를 물며 통과하는 배그식 블루존 규칙으로 흡수한다.
    ///     거리 맵은 프로세스 수명 동안 불변이라 시작 시 한 번 계산한다.
    ///     서버 판정과 클라 경계 렌더가 같은 필드를 쓴다 — 표시 = 판정.
    /// </summary>
    public static class SwarmPressureField
    {
        private static readonly object InitLock = new object();
        private static Dictionary<(int X, int Y), int> _distanceByCell;
        private static Dictionary<AreaType, int> _minDistanceByArea = new Dictionary<AreaType, int>();
        private static int _maxDistance;
        private static float _centerX;
        private static float _centerY;

        /// <summary>맵에서 중심까지 가장 먼 거리 — 안전 거리 수축의 시작값.</summary>
        public static int MaxDistance
        {
            get
            {
                EnsureInitialized();
                return _maxDistance;
            }
        }

        /// <summary>원 중심 (셀 좌표, 운동장 사각 중심) — 클라 경계 렌더가 같은 원을 그린다.</summary>
        public static (float X, float Y) CenterCell
        {
            get
            {
                EnsureInitialized();
                return (_centerX, _centerY);
            }
        }

        /// <summary>중심까지 거리 (셀, 반올림). 도달 불가 셀은 최대 거리로 취급한다.</summary>
        public static int GetDistance(Cell cell)
        {
            EnsureInitialized();
            return _distanceByCell.GetValueOrDefault((cell.X, cell.Y), _maxDistance);
        }

        /// <summary>
        ///     구역에서 중심에 가장 가까운 셀의 거리.
        ///     이 값이 안전 거리를 넘으면 구역 전체가 경계 밖이다.
        /// </summary>
        public static int GetAreaMinDistance(AreaType area)
        {
            EnsureInitialized();
            return _minDistanceByArea.GetValueOrDefault(area, int.MaxValue);
        }

        /// <summary>
        ///     수축 진행률(0~1) → 안전 반경. ease-in 곡선(#272): 초반 느리고 후반 빠르다 —
        ///     서버 오염 판정·클라 경계 렌더·파생 폐쇄 시간표가 전부 이 함수 하나를 쓴다.
        /// </summary>
        public static double GetSafeDistanceAtProgress(double progress)
        {
            double clamped = Math.Clamp(progress, 0d, 1d);
            return MaxDistance * (1d - Math.Pow(clamped, Config.SWARM_FIELD_SHRINK_EXPONENT));
        }

        /// <summary>역함수: 이 거리가 경계에 먹히는 수축 진행률(0~1) — 파생 폐쇄 시각 계산용.</summary>
        public static double GetProgressAtSafeDistance(double distance)
        {
            double ratio = Math.Clamp(1d - distance / MaxDistance, 0d, 1d);
            return Math.Pow(ratio, 1d / Config.SWARM_FIELD_SHRINK_EXPONENT);
        }

        public static IReadOnlyCollection<AreaType> GetKnownAreas()
        {
            EnsureInitialized();
            return _minDistanceByArea.Keys;
        }

        private static readonly Dictionary<(int X, int Y), int> EmptyDistances = new Dictionary<(int X, int Y), int>();

        /// <summary>셀별 거리 원본 — 클라 경계 렌더가 유효 마스크·미니맵 베이크로 굽는다.</summary>
        public static IReadOnlyDictionary<(int X, int Y), int> DistancesByCell
        {
            get
            {
                EnsureInitialized();
                // 맵 CSV가 아직 로드되기 전이면 초기화가 미뤄져 null이다 — 빈 표를 돌려
                // 호출자의 NRE를 막는다 (다음 접근에서 재시도된다).
                return _distanceByCell ?? EmptyDistances;
            }
        }

        private static void EnsureInitialized()
        {
            if (_distanceByCell != null) return;
            lock (InitLock)
            {
                if (_distanceByCell != null) return;

                var areas = GameMapData.GetAreas(Config.SWARM_MATCH_MAP);

                // 맵 CSV가 아직 로드되기 전이면 빈 거리장을 영구 캐시하게 된다(테스트 병렬 순서 플레이크)
                // — 캐시하지 않고 다음 호출에서 재시도한다.
                if (areas == null || areas.Count == 0) return;

                // 원 중심 = 운동장 사각(들)의 경계 상자 중심. 셀은 끝값 포함이라 중심은 (Start+End)/2.
                var groundRects = areas
                    .Where(region => region.AreaType == Config.SWARM_MATCH_GROUND_AREA).ToList();
                if (groundRects.Count > 0)
                {
                    int groundMinX = groundRects.Min(region => Math.Min(region.Start.X, region.End.X));
                    int groundMaxX = groundRects.Max(region => Math.Max(region.Start.X, region.End.X));
                    int groundMinY = groundRects.Min(region => Math.Min(region.Start.Y, region.End.Y));
                    int groundMaxY = groundRects.Max(region => Math.Max(region.Start.Y, region.End.Y));
                    _centerX = (groundMinX + groundMaxX) * 0.5f;
                    _centerY = (groundMinY + groundMaxY) * 0.5f;
                }

                // 전 구역 경계 상자 안의 이동 가능 셀 전부에 거리를 매긴다 — 문·벽 위상은 보지 않는다.
                int minX = int.MaxValue;
                int minY = int.MaxValue;
                int maxX = int.MinValue;
                int maxY = int.MinValue;
                foreach (var region in areas)
                {
                    minX = Math.Min(minX, Math.Min(region.Start.X, region.End.X));
                    maxX = Math.Max(maxX, Math.Max(region.Start.X, region.End.X));
                    minY = Math.Min(minY, Math.Min(region.Start.Y, region.End.Y));
                    maxY = Math.Max(maxY, Math.Max(region.Start.Y, region.End.Y));
                }

                var distances = new Dictionary<(int X, int Y), int>();
                var minByArea = new Dictionary<AreaType, int>();
                int maxDistance = 1;
                for (int y = minY; y <= maxY; y++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        var cell = new Cell(x, y);
                        if (!GameMapData.IsMoveablePosition(Config.SWARM_MATCH_MAP, cell)) continue;

                        float dx = x - _centerX;
                        float dy = y - _centerY;
                        int distance = (int)Math.Round(Math.Sqrt(dx * dx + dy * dy));
                        distances[(x, y)] = distance;
                        if (distance > maxDistance) maxDistance = distance;

                        var area = GameMapData.GetCurrentArea(Config.SWARM_MATCH_MAP, cell);
                        if (area == AreaType.None) continue;
                        if (!minByArea.TryGetValue(area, out int currentMin) || distance < currentMin)
                            minByArea[area] = distance;
                    }
                }

                _minDistanceByArea = minByArea;
                _maxDistance = maxDistance;
                _distanceByCell = distances;
            }
        }
    }
}
