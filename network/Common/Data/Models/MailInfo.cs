// ReSharper disable All

using System.Collections.Generic;
using MessagePack;

namespace network.common.data.models
{
    [MessagePackObject]
    public partial class MailBox : IMessagePackObject
    {
        [IgnoreMember] public const string HashKey = "MailBox";
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("MailDict")] public Dictionary<long, MailInfo> MailDict { get; set; }

        public MailBox()
        {
            PlayerId = 0;
            MailDict = new();
        }

        public MailBox(long playerId)
        {
            PlayerId = playerId;
            MailDict = new();
        }
    }
    
    [MessagePackObject]
    public class MailInfo : IMessagePackObject
    {
        [IgnoreMember] public const string HashKey = "MailInfo";
        
        public MailInfo()
        {
            MailUid = 0;
            MailId = 0;
            State = MailState.NONE;
            Items = new();
        }
        
        public MailInfo(long mailUid, int mailId, List<(int, int)> items)
        {
            MailUid = mailUid;
            MailId = mailId;
            State = MailState.NONE;
            Items = items;
        }
        
        [Key("uid")] public long MailUid { get; set; }
        [Key("id")] public int MailId { get; set; }
        [Key("state")] public MailState State { get; set; }
        [Key("items")] public List<(int, int)> Items { get; set; }
    }
}
