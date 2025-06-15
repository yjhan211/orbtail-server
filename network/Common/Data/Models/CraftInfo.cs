// ReSharper disable All

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using MessagePack;
using network.common.data;

namespace network.common.data.models
{
    [MessagePackObject]
    public partial class CraftInfo : IMessagePackObject
    {
        [IgnoreMember] public const string HashKey = "CraftInfo";

        public CraftInfo()
        {
            PlayerId = 0;
            Manuals = new List<int>();
            Slots = new List<int>();
        }
        
        public CraftInfo(long playerId)
        {
            PlayerId = playerId;
            Manuals = new List<int>();
            Slots = new List<int>();
            for (var i = 1; i <= 42; i++)
            {
                Slots.Add(0);
            }
        }
        
        [Key("playerId")] public long PlayerId { get; set; }
        [Key("manuals")] public List<int> Manuals { get; set; }
        [Key("slots")] public List<int> Slots { get; set; }
    }
}