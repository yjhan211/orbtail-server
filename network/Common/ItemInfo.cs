using MessagePack;

namespace network.common
{
    [MessagePackObject]
    public class ItemInfo : IMessagePackObject
    {
        [IgnoreMember]
        public GameObjectInfo ObjectInfo { get; set; }

        [Key("itemUid")]
        public long ItemUid { get; set; } // 유니크 아이디

        [Key("itemId")]
        public int ItemId { get; set; } // 아이템 종류

        [Key("count")]
        public int Count { get; set; } // 수량

        [Key("isWear")]
        public bool IsWear { get; set; } // 착용 여부

        [Key("durability")]
        public int Durability { get; set; } // 내구도

        [Key("skillId")]
        public int SkillId { get; set; } // 장비에 붙어있는 스킬

        // 이거 없애면 안됨 MessagePack에서 씀
        public ItemInfo()
        {
            ItemUid = 0;
            ItemId = 0;
            Count = 0;
            IsWear = false;
            Durability = 0;
            SkillId = 0;
            ObjectInfo = new();
        }

        public ItemInfo(long itemUid, int itemId, int count)
        {
            ItemUid = itemUid;
            ItemId = itemId;
            Count = count;
            IsWear = false;
            Durability = 100;
            SkillId = 0;
            ObjectInfo = new(itemUid);
        }
    }
}
