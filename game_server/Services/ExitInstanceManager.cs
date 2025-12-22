using System.Collections.Concurrent;
using network.common;
using network.common.data;

namespace game_server.services
{
    /// <summary>
    /// 탈출 절차 슬롯 바인딩 정보
    /// </summary>
    public class ExitSlotBinding
    {
        public int SpotId { get; set; }
        public int ConditionId { get; set; }
    }

    /// <summary>
    /// 단일 매칭 인스턴스의 탈출 절차 상태
    /// </summary>
    public class MatchingExitState
    {
        public int TemplateId { get; private set; }
        public ExitSlotBinding SlotBinding { get; private set; }
        public int CurrentStepOrder { get; private set; }
        public List<ExitStepData> Steps { get; private set; }
        public bool IsCompleted { get; private set; }
        public long LastAdvancedBy { get; private set; } // 마지막으로 진행한 플레이어 UID

        private readonly Random _random;
        private readonly object _stepLock = new(); // 동시 진행 방지 락

        public MatchingExitState(long matchingId)
        {
            _random = new Random((int)(matchingId % int.MaxValue));
            GenerateExitProcedure();
        }

        private void GenerateExitProcedure()
        {
            // 1. 템플릿 랜덤 선택 (싱글 매칭이므로 템플릿 2 제외)
            var templates = GameExitData.GetAllTemplates()
                .Where(t => t.Id != 2)  // TODO: 멀티 매칭 구현 시 제거
                .ToList();
            TemplateId = templates[_random.Next(templates.Count)].Id;

            // 2. 제약 조건 가져오기
            var constraints = GameExitData.GetConstraintsByTemplate(TemplateId);

            // 3. 슬롯 바인딩 생성 (제약 조건 적용)
            SlotBinding = GenerateSlotBinding(constraints);

            // 4. 단계 로드
            Steps = GameExitData.GetStepsByTemplate(TemplateId);
            CurrentStepOrder = 1;
            IsCompleted = false;
        }

        private ExitSlotBinding GenerateSlotBinding(List<ExitConstraintData> constraints)
        {
            var binding = new ExitSlotBinding();

            // 가용 목록 초기화
            var availableSpots = GameExitData.GetAllSpots().Select(s => s.Id).ToList();
            var availableConditions = GameExitData.GetAllConditions().Select(c => c.Id).ToList();

            // POOL 제약 적용 (허용 목록)
            foreach (var constraint in constraints.Where(c => c.ConstraintType == (int)ExitConstraintType.POOL))
            {
                switch ((ExitSlotType)constraint.SlotType)
                {
                    case ExitSlotType.SPOT:
                        availableSpots = availableSpots.Intersect(constraint.Values).ToList();
                        break;
                    case ExitSlotType.CONDITION:
                        availableConditions = availableConditions.Intersect(constraint.Values).ToList();
                        break;
                }
            }

            // 장소 선택
            binding.SpotId = availableSpots[_random.Next(availableSpots.Count)];

            // 조건 선택
            if (availableConditions.Count > 0)
            {
                binding.ConditionId = availableConditions[_random.Next(availableConditions.Count)];
            }
            else
            {
                // 폴백: 첫 번째 조건 사용
                binding.ConditionId = GameExitData.GetAllConditions().First().Id;
            }

            return binding;
        }

        /// <summary>
        /// 현재 단계 정보 반환
        /// </summary>
        public ExitStepData? GetCurrentStep()
        {
            if (IsCompleted) return null;
            return Steps.FirstOrDefault(s => s.StepOrder == CurrentStepOrder);
        }

        /// <summary>
        /// 현재 단계 텍스트 생성 (슬롯 치환)
        /// </summary>
        public string GetCurrentStepText()
        {
            var step = GetCurrentStep();
            if (step == null) return string.Empty;
            return ApplySlotSubstitution(step.TextTemplate);
        }

        /// <summary>
        /// 전체 단계 목록 반환 (텍스트 치환 완료)
        /// </summary>
        public List<(int stepOrder, string text, int actionType, string targetInteractableId)> GetAllStepsWithText()
        {
            var result = new List<(int, string, int, string)>();
            foreach (var step in Steps.OrderBy(s => s.StepOrder))
            {
                var text = ApplySlotSubstitution(step.TextTemplate);
                var targetId = ResolveTargetInteractableId(step.TargetInteractableId);
                result.Add((step.StepOrder, text, step.ActionType, targetId));
            }
            return result;
        }

        /// <summary>
        /// 슬롯 치환 적용
        /// </summary>
        private string ApplySlotSubstitution(string text)
        {
            var spot = GameExitData.GetSpot(SlotBinding.SpotId);
            var condition = GameExitData.GetCondition(SlotBinding.ConditionId);

            if (spot != null)
            {
                var interactable = GameInteractableData.Get(spot.InteractableId);
                text = text.Replace("{Spot.Name}", interactable?.ShortName ?? "???");
            }

            if (condition != null)
            {
                text = text.Replace("{Condition.Text}", condition.Text);
            }

            return text;
        }

        /// <summary>
        /// TargetInteractableId 슬롯 치환
        /// </summary>
        private string ResolveTargetInteractableId(string? template)
        {
            if (string.IsNullOrEmpty(template)) return string.Empty;

            var result = template;

            if (result.Contains("{Spot.InteractObj}"))
            {
                var spot = GameExitData.GetSpot(SlotBinding.SpotId);
                result = result.Replace("{Spot.InteractObj}", spot?.InteractableId.ToString() ?? "0");
            }

            return result;
        }

        /// <summary>
        /// 다음 단계로 진행 (동시 진행 방지)
        /// </summary>
        public bool AdvanceStep(long playerId)
        {
            lock (_stepLock)
            {
                if (IsCompleted) return false;

                var nextOrder = CurrentStepOrder + 1;
                var nextStep = Steps.FirstOrDefault(s => s.StepOrder == nextOrder);

                LastAdvancedBy = playerId;

                if (nextStep == null)
                {
                    IsCompleted = true;
                    return true; // 탈출 완료
                }

                CurrentStepOrder = nextOrder;
                return true;
            }
        }

        /// <summary>
        /// 탈출 완료 여부
        /// </summary>
        public bool CheckEscape()
        {
            return IsCompleted;
        }
    }

    /// <summary>
    /// MatchingId별로 탈출 절차 상태를 관리하는 매니저
    /// </summary>
    public class ExitInstanceManager
    {
        private Action<string>? _logAction;
        private readonly ConcurrentDictionary<long, MatchingExitState> _matchingStates = new();

        public void Initialize(Action<string>? logAction = null)
        {
            _logAction = logAction;
            _matchingStates.Clear();
            _logAction?.Invoke("ExitInstanceManager: Initialized");
        }

        /// <summary>
        /// 매칭 인스턴스의 탈출 상태를 가져오거나 새로 생성
        /// </summary>
        public MatchingExitState GetOrCreateMatchingState(long matchingId)
        {
            return _matchingStates.GetOrAdd(matchingId, id =>
            {
                _logAction?.Invoke($"ExitInstanceManager: Creating new exit state for MatchingId={id}");
                var state = new MatchingExitState(id);
                _logAction?.Invoke($"ExitInstanceManager: Generated - Template={state.TemplateId}, Spot={state.SlotBinding.SpotId}, Condition={state.SlotBinding.ConditionId}");
                return state;
            });
        }

        /// <summary>
        /// 현재 단계 텍스트 가져오기
        /// </summary>
        public string GetCurrentStepText(long matchingId)
        {
            var state = GetOrCreateMatchingState(matchingId);
            return state.GetCurrentStepText();
        }

        /// <summary>
        /// 현재 단계 정보 가져오기
        /// </summary>
        public ExitStepData? GetCurrentStep(long matchingId)
        {
            var state = GetOrCreateMatchingState(matchingId);
            return state.GetCurrentStep();
        }

        /// <summary>
        /// 다음 단계로 진행
        /// </summary>
        public (bool success, bool escaped, string? nextStepText) AdvanceStep(long matchingId, long playerId)
        {
            var state = GetOrCreateMatchingState(matchingId);

            if (!state.AdvanceStep(playerId))
            {
                return (false, false, null);
            }

            if (state.IsCompleted)
            {
                _logAction?.Invoke($"ExitInstanceManager: MatchingId={matchingId} ESCAPED by Player {playerId}!");
                return (true, true, null);
            }

            var nextText = state.GetCurrentStepText();
            _logAction?.Invoke($"ExitInstanceManager: MatchingId={matchingId} advanced to step {state.CurrentStepOrder} by Player {playerId}");
            return (true, false, nextText);
        }

        /// <summary>
        /// 탈출 완료 여부 확인
        /// </summary>
        public bool IsEscaped(long matchingId)
        {
            if (!_matchingStates.TryGetValue(matchingId, out var state))
                return false;
            return state.IsCompleted;
        }

        /// <summary>
        /// 매칭 종료 시 상태 정리
        /// </summary>
        public void RemoveMatchingState(long matchingId)
        {
            if (_matchingStates.TryRemove(matchingId, out _))
            {
                _logAction?.Invoke($"ExitInstanceManager: Removed state for MatchingId={matchingId}");
            }
        }

        /// <summary>
        /// 전체 상태 초기화
        /// </summary>
        public void Reset()
        {
            _matchingStates.Clear();
            _logAction?.Invoke("ExitInstanceManager: All states cleared");
        }
    }
}
