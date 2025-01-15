// ReSharper disable All

using System.Collections.Generic;
using MessagePack;

namespace network.common.data.models
{
    [MessagePackObject]
    public class MailInfo : IMessagePackObject
    {
        [IgnoreMember] public const string HashKey = "MailInfo";
        
        public MailInfo()
        {
            
        }

        public MailInfo(long playerId, string comment, List<ItemInfo> items = null)
        {
            PlayerId = playerId;
            Comment = comment;
            Items = items;
        }
        
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("comment")] public string Comment { get; set; }
        [Key("items")] public List<ItemInfo> Items { get; set; }
    }
}