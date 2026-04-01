// ReSharper disable All

using System.Collections.Generic;
using MessagePack;

namespace network.common.data.models
{
    [MessagePackObject]
    public class InteractableActionState : IMessagePackObject
    {
        [Key("order")] public int Order { get; set; }
        [Key("isExplored")] public bool IsExplored { get; set; }
        [Key("exploredBy")] public long ExploredBy { get; set; }
        [Key("state")] public int State { get; set; }  // 액션이 활성화되는 Interactable state (0=기본, 1=사보타주 등)
    }

    [MessagePackObject]
    public class InteractableObjectState : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
        [Key("actions")] public List<InteractableActionState> Actions { get; set; } = new();
        [Key("missionActionText")] public string MissionActionText { get; set; } = "";
    }
}
