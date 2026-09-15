using MessagePack;

namespace network.common.data.models
{
    /// <summary>구역에서 상호작용할 수 있는 오브젝트 하나. 완료 여부는 문의 열림 상태에 대응한다.</summary>
    [MessagePackObject]
    public class InteractableInfo : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
        [Key("isCompleted")] public bool IsCompleted { get; set; }
    }
}
