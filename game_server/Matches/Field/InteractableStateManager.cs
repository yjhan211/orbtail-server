using network.common;
using network.common.data;
using network.common.data.models;

namespace game_server.matches.field;

/// <summary>
///     공용 상호작용 정의를 클라이언트에 보낼 구역별 목록으로 변환한다.
///     탐색 상태를 변경하는 기능은 없으므로 매치별 복사본이나 종료 시 정리가 필요하지 않다.
///     호출마다 새 DTO를 반환해 응답 수정이 다른 세션에 영향을 주지 않게 한다.
/// </summary>
public static class InteractableStateManager
{
    public static List<InteractableObjectState> GetAreaObjectStates(AreaType areaType) =>
        GameInteractableData.GetAll()
            .Where(interactable => interactable.ZoneId == (int)areaType &&
                                   interactable.Actions.Any(action => action.State == 0))
            .Select(interactable => new InteractableObjectState
            {
                InteractId = interactable.Id,
                Actions = interactable.Actions.Select(action => new InteractableActionState
                {
                    Order = action.ActionId,
                    IsExplored = false,
                    ExploredBy = 0,
                    State = action.State
                }).ToList()
            }).ToList();
}
