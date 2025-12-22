// ReSharper disable All
#pragma warning disable CS8618

using System;
using System.Collections.Generic;
using System.Linq;

namespace network.common.data.models
{
    /// <summary>
    /// 세션별 포탈 상태 관리
    /// 조건부 포탈의 해제 상태를 추적
    /// </summary>
    public class PortalStateManager
    {
        private readonly HashSet<AreaType> _unlockedPortals = new();
        private readonly Dictionary<AreaType, DateTime> _unlockTimes = new();

        /// <summary>
        /// 특정 구역 포탈이 해제되었는지 확인
        /// </summary>
        public bool IsPortalUnlocked(AreaType areaType)
        {
            // 조건부 포탈이 아니면 항상 열림
            if (!GamePortalConditionData.IsConditionalPortal(areaType))
                return true;

            return _unlockedPortals.Contains(areaType);
        }

        /// <summary>
        /// 포탈 해제
        /// </summary>
        public bool UnlockPortal(AreaType areaType)
        {
            if (_unlockedPortals.Contains(areaType))
                return false; // 이미 해제됨

            _unlockedPortals.Add(areaType);
            _unlockTimes[areaType] = DateTime.UtcNow;
            return true;
        }

        /// <summary>
        /// 아이템 획득 시 포탈 해제 여부 확인 및 처리
        /// </summary>
        /// <returns>해제된 포탈의 조건 데이터, 해제할 포탈이 없으면 null</returns>
        public PortalConditionData TryUnlockPortalByItem(int itemId)
        {
            var condition = GamePortalConditionData.GetByItemId(itemId);
            if (condition == null)
                return null;

            if (UnlockPortal(condition.TargetAreaType))
                return condition;

            return null;
        }

        /// <summary>
        /// 해제된 모든 포탈 목록
        /// </summary>
        public List<AreaType> GetUnlockedPortals()
        {
            return _unlockedPortals.ToList();
        }

        /// <summary>
        /// 특정 포탈의 해제 시간
        /// </summary>
        public DateTime? GetUnlockTime(AreaType areaType)
        {
            return _unlockTimes.TryGetValue(areaType, out var time) ? time : null;
        }

        /// <summary>
        /// 포탈 상태 초기화 (게임 시작 시)
        /// </summary>
        public void Reset()
        {
            _unlockedPortals.Clear();
            _unlockTimes.Clear();
        }

        /// <summary>
        /// 특정 포탈들을 해제된 상태로 초기화 (세이브 로드 시)
        /// </summary>
        public void InitializeWith(IEnumerable<AreaType> unlockedPortals)
        {
            Reset();
            foreach (var areaType in unlockedPortals)
            {
                _unlockedPortals.Add(areaType);
            }
        }
    }

    /// <summary>
    /// 포탈 해제 이벤트 정보
    /// </summary>
    public class PortalUnlockEventData
    {
        public AreaType TargetAreaType { get; set; }
        public int RequiredItemId { get; set; }
        public int MessageTextId { get; set; }
        public string Message { get; set; }

        public static PortalUnlockEventData FromCondition(PortalConditionData condition)
        {
            return new PortalUnlockEventData
            {
                TargetAreaType = condition.TargetAreaType,
                RequiredItemId = condition.RequiredItemId,
                MessageTextId = condition.MessageTextId,
                Message = condition.GetMessage()
            };
        }
    }
}
