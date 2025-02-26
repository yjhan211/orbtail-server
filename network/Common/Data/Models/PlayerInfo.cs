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
            InventoryInfo = new InventoryInfo();
            CraftInfo = new CraftInfo();
            CampInfo = new CampInfo();
            LabId = 0;
            LabName = "";
            Gold = 0;
            TutorialIndex = 0;
            Hp = 0;
            Stamina = 0;
            Boosts = new();
            IsTutorial = true;
            LastMapId = MapId.None;
            LastMapSubId = 0;
            LastCell = new(0, 0);
        }

        public PlayerInfo(long playerId, bool isDummy)
        {
            var name = isDummy ? $"더미{playerId}" : string.Empty;
            
            // TODO 공통맵 추가 이후 활성화
            // var initCell = isDummy ? CommonMapData.GetRandomCell() : GameRuleData.StartPosition;
            var initCell = GameRuleData.StartPosition;
            PlayerId = playerId;
            Name = name;
            Grade = PlayerGrade.COMMONER;
            WearItemIdList = new();
            State = PlayerState.NONE;
            ObjectInfo = new GameObjectInfo(ObjectType.PLAYER, PlayerId, MapId.Classroom, 1, initCell);
            InventoryInfo = new InventoryInfo(InventoryOwnerType.PLAYER, PlayerId);
            CraftInfo = new CraftInfo(PlayerId);
            CampInfo = new CampInfo(PlayerId, Name, new(), new(), new(0, 0));
            LabId = 0;
            LabName = "";
            Gold = 1000;
            TutorialIndex = 0;
            Hp = 10000;
            Stamina = 100;
            Boosts = new();
            IsTutorial = false;
            LastMapId = MapId.None;
            LastMapSubId = 0;
            LastCell = new(0, 0);
            IsNew = true;
        }

        [IgnoreMember] public GameObjectInfo ObjectInfo { get; set; }
        [IgnoreMember] public InventoryInfo InventoryInfo { get; set; }
        [IgnoreMember] public CraftInfo CraftInfo { get; set; }
        [IgnoreMember] public CampInfo CampInfo { get; set; }
        [Key("playerId")] public long PlayerId { get; set; }

        [Key("name")] public string Name { get; set; }

        [Key("grade")] public PlayerGrade Grade { get; set; }

        [Key("wearItemIdList")] public List<int> WearItemIdList { get; set; }

        [Key("state")] public PlayerState State { get; set; }

        [Key("labId")] public long LabId { get; set; }

        [Key("labName")] public string LabName { get; set; }

        [Key("gold")] public long Gold { get; set; }
        
        [Key("tutorialIndex")] public int TutorialIndex { get; set; }
        
        [Key("hp")] public int Hp { get; set; }
        
        [Key("stamina")] public int Stamina { get; set; }

        [Key("boostList")] public HashSet<BoostType> Boosts { get; set; }
        [Key("isTutorial")] public bool IsTutorial { get; set; }
        
        [Key("mapId")] public MapId LastMapId { get; set; }
        
        [Key("mapSubId")] public long LastMapSubId { get; set; }
        
        [Key("lastCell")] public Cell LastCell { get; set; }
        [IgnoreMember] public bool IsNew { get; set; }

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