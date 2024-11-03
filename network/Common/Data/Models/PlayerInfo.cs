// ReSharper disable All

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using MessagePack;
using network.common.data;

namespace network.common.data.models
{
    [MessagePackObject]
    public partial class PlayerInfo : IMessagePackObject
    {
        [IgnoreMember] public const string HashKey = "PlayerInfo";

        // 이거 없애면 안됨 MessagePack에서 씀
        public PlayerInfo()
        {
            PlayerId = 0;
            Name = "";
            Grade = PlayerGrade.NONE;
            WearItemIdList = new();
            State = PlayerState.NONE;
            ObjectInfo = new GameObjectInfo();
            JobInfo = new JobInfo();
            InventoryInfo = new InventoryInfo();
            LabId = 0;
            LabName = "";
            Gold = 0;
            TutorialIndex = 0;
        }

        public PlayerInfo(long playerId, bool isDummy)
        {
            var name = isDummy ? $"더미{playerId}" : $"플레이어{playerId}";

            var initCell = isDummy ? CommonMapData.GetRandomCell() : GameRuleData.StartPosition;
            PlayerId = playerId;
            Name = name;
            Grade = PlayerGrade.COMMONER;
            WearItemIdList = new();
            State = PlayerState.NONE;
            ObjectInfo = new GameObjectInfo(ObjectType.PLAYER, PlayerId, MapId.LIBRARY, 1, initCell);
            JobInfo = new JobInfo(PlayerId);
            InventoryInfo = new InventoryInfo(InventoryOwnerType.PLAYER, PlayerId);
            LabId = 0;
            LabName = "";
            Gold = 1000;
            TutorialIndex = 0;
        }

        [IgnoreMember] public GameObjectInfo ObjectInfo { get; set; }

        [IgnoreMember] public JobInfo JobInfo { get; set; }

        [IgnoreMember] public InventoryInfo InventoryInfo { get; set; }

        [Key("playerId")] public long PlayerId { get; set; }

        [Key("name")] public string Name { get; set; }

        [Key("grade")] public PlayerGrade Grade { get; set; }

        // ReSharper disable once CollectionNeverUpdated.Global
        [Key("wearItemIdList")] public List<int> WearItemIdList { get; set; }

        [Key("state")] public PlayerState State { get; set; }

        [Key("labId")] public long LabId { get; set; }

        [Key("labName")] public string LabName { get; set; }

        [Key("gold")] public long Gold { get; set; }

        [Key("tutorialIndex")] public int TutorialIndex { get; set; }

        public string GetLockKey()
        {
            return $"player_lock_{PlayerId}";
        }

        public static string GetLockKey(long playerId)
        {
            return $"player_lock_{playerId}";
        }
    }
}