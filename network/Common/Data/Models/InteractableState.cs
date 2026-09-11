// ReSharper disable All

using System.Collections.Generic;
using MessagePack;

namespace network.common.data.models
{
    /// <summary>상호작용 오브젝트의 액션 하나. 클라이언트는 Order로 object_action.csv의 액션 정의를 찾는다.</summary>
    [MessagePackObject]
    public class InteractableActionState : IMessagePackObject
    {
        [Key("order")] public int Order { get; set; }
    }

    /// <summary>구역에서 상호작용할 수 있는 오브젝트 하나. 현재는 닫힌 문만 해당한다.</summary>
    [MessagePackObject]
    public class InteractableObjectState : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
        [Key("actions")] public List<InteractableActionState> Actions { get; set; } = new();
    }
}
