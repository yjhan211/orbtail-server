#pragma warning disable CS8618
// ReSharper disable All
using System;
using System.Collections.Generic;
using MessagePack;
using network.common.data.helpers;

namespace network.common.data.models
{
    // InteractId는 문 양쪽의 상호작용 위치를 구분한다. DoorId는 서버 데이터에서 찾는다.
    [MessagePackObject]
    public class C_TO_G_INTERACTION_START : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
    }

    [MessagePackObject]
    public class C_TO_G_INTERACTION_FINISH : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
    }

    [MessagePackObject]
    public class G_TO_C_INTERACTION_ACK : IMessagePackObject
    {
        [Key("interactId")] public int InteractId { get; set; }
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
        [Key("completed")] public bool Completed { get; set; }
    }

}
