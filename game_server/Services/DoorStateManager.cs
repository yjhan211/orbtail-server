using System.Collections.Generic;
using System.Linq;
using network.common.data;

namespace game_server.services
{
    /// <summary>
    /// 매칭별 문 상태를 관리하는 서비스
    /// 게임 세션 동안 문 상태 유지 (한번 열리면 계속 Open)
    /// </summary>
    public class DoorStateManager
    {
        // matchingId -> 열린 문 ID 목록
        private readonly Dictionary<long, HashSet<int>> _openDoors = new();
        private readonly object _lock = new();

        /// <summary>
        /// 매칭 시작 시 초기 열린 문 등록
        /// </summary>
        public void InitializeMatching(long matchingId)
        {
            lock (_lock)
            {
                // 이미 초기화되어 있으면 스킵
                if (_openDoors.ContainsKey(matchingId))
                {
                    return;
                }

                _openDoors[matchingId] = new HashSet<int>();

                // 초기 열림 상태인 문 등록
                foreach (var door in GameDoorData.GetAll())
                {
                    if (door.IsInitiallyOpen)
                    {
                        _openDoors[matchingId].Add(door.DoorId);
                    }
                }
            }
        }

        /// <summary>
        /// 문 열기
        /// </summary>
        /// <returns>true: 새로 열림, false: 이미 열려있었음</returns>
        public bool OpenDoor(long matchingId, int doorId)
        {
            lock (_lock)
            {
                if (!_openDoors.ContainsKey(matchingId))
                {
                    _openDoors[matchingId] = new HashSet<int>();
                }

                return _openDoors[matchingId].Add(doorId);
            }
        }

        /// <summary>
        /// 문이 열려있는지 확인
        /// </summary>
        public bool IsDoorOpen(long matchingId, int doorId)
        {
            lock (_lock)
            {
                return _openDoors.TryGetValue(matchingId, out var doors) && doors.Contains(doorId);
            }
        }

        /// <summary>
        /// 열린 문 ID 목록 가져오기
        /// </summary>
        public List<int> GetOpenDoors(long matchingId)
        {
            lock (_lock)
            {
                if (_openDoors.TryGetValue(matchingId, out var doors))
                {
                    return doors.ToList();
                }
                return new List<int>();
            }
        }

        /// <summary>
        /// 매칭 종료 시 상태 정리
        /// </summary>
        public void ClearMatching(long matchingId)
        {
            lock (_lock)
            {
                _openDoors.Remove(matchingId);
            }
        }
    }
}
