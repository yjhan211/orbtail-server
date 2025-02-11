// ReSharper disable All

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using MessagePack;

namespace network.common.data.models
{
    [MessagePackObject]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [SuppressMessage("ReSharper", "AutoPropertyCanBeMadeGetOnly.Global")]
    [SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
    public partial class CampInfo : IMessagePackObject
    {
        [IgnoreMember] public const string HashKey = "CampInfo";

        public CampInfo()
        {
            ObjectInfo = new GameObjectInfo();
            PlayerId = new long();
            PlayerName = "";
            ItemInfo = new ItemInfo();
            CellDict = new Dictionary<long, (ItemInfo, int)>();
        }

        public CampInfo(long playerId, string playerName, GameObjectInfo playerObjectInfo, ItemInfo itemInfo, Cell cell)
        {
            ObjectInfo = new GameObjectInfo(
                ObjectType.CAMP,
                playerId,
                playerObjectInfo.MapId,
                playerObjectInfo.MapSubId,
                Cell.Clone(cell),
                playerObjectInfo.IsFlip
            );

            PlayerId = playerId;
            PlayerName = playerName;
            ItemInfo = itemInfo;
            CellDict = new Dictionary<long, (ItemInfo, int)>();
        }

        [IgnoreMember] public GameObjectInfo ObjectInfo { get; set; }

        [Key("playerId")] public long PlayerId { get; set; }

        [Key("playerName")] public string PlayerName { get; set; }

        [Key("itemInfo")] public ItemInfo ItemInfo { get; set; }

        [Key("cellDict")] public Dictionary<long, (ItemInfo, int)> CellDict { get; set; }
        
        private Cell CalculatePortal(bool isFlip)
        {
            return isFlip
                ? new Cell(ObjectInfo.CurrentCell.X, ObjectInfo.CurrentCell.Y + 1)
                : new Cell(ObjectInfo.CurrentCell.X + 1, ObjectInfo.CurrentCell.Y);
        }

        private (Cell start, Cell end) CalculateBound(bool isFlip)
        {
            if (isFlip)
            {
                return (
                    new Cell(ObjectInfo.CurrentCell.X + 1, ObjectInfo.CurrentCell.Y),
                    new Cell(ObjectInfo.CurrentCell.X + 5, ObjectInfo.CurrentCell.Y + 4)
                );
            }

            return (
                new Cell(ObjectInfo.CurrentCell.X, ObjectInfo.CurrentCell.Y + 1),
                new Cell(ObjectInfo.CurrentCell.X + 4, ObjectInfo.CurrentCell.Y + 5)
            );
        }
    }
}