#pragma warning disable CS8618
// ReSharper disable All
using System;
using System.Collections.Generic;
using MessagePack;
using network.common.data.helpers;

namespace network.common.data.models
{
    [MessagePackObject]
    public class U_TO_C_MAIL_LIST : IMessagePackObject
    {
        [Key("mailDict")] public Dictionary<long, MailInfo> MailDict { get; set; }
        [Key("isEnd")] public bool IsEnd { get; set; }
    }

    [MessagePackObject]
    public class C_TO_U_MAIL_RECEIVE : IMessagePackObject
    {
        [Key("mailUid")] public long MailUid { get; set; }
    }

    [MessagePackObject]
    public class U_TO_C_MAIL_RECEIVE : IMessagePackObject
    {
        [Key("mailUid")] public long MailUid { get; set; }
        [Key("errorCode")] public ErrorCode ErrorCode { get; set; }
    }
}
