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
    }

    [MessagePackObject]
    public class InteractableObjectState : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
        [Key("actions")] public List<InteractableActionState> Actions { get; set; }
    }
}
