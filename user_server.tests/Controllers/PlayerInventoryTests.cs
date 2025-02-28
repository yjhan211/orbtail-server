using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using network.common;
using network.common.data;
using network.common.data.helpers;
using network.common.data.models;
using network.helpers;
using network.interfaces;
using network.packets;
using NUnit.Framework;
using RedLockNet;
using user_server.players;
using user_server.tests.components;

namespace user_server.tests.controllers
{
    [TestFixture]
    public class PlayerInventoryTests
    {
        // 의존성 모킹
        private Mock<ICacheHelper> _mockCacheHelper;
        private Mock<IRedLockFactory> _mockRedLockFactory;
        private Mock<IRedLock> _mockRedLock;
        private Mock<ILogger> _mockLogger;
        
        private TestNatsClient _testNatsClient;
        private TestUserToken _userToken;
        private TestGameUser _gameUser;
        
        private PlayerInfo _playerInfo;
        private PlayerInventory _playerInventory;
        private TestPlayerQuest _testPlayerQuest;
        
        [SetUp]
        public void Setup()
        {
            // 게임 데이터 초기화
            GameDataHelper.Initialize();
            
            // 개별 의존성 모킹
            _mockCacheHelper = new Mock<ICacheHelper>();
            _mockRedLockFactory = new Mock<IRedLockFactory>();
            _mockRedLock = new Mock<IRedLock>();
            _mockLogger = new Mock<ILogger>();
            
            _testNatsClient = new TestNatsClient();
            _userToken = new TestUserToken();
            
            // 테스트 PlayerInfo 생성
            _playerInfo = CreateTestPlayerInfo();
            
            // RedLock 설정
            _mockRedLockFactory
                .Setup(x => x.CreateLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>()))
                .ReturnsAsync(_mockRedLock.Object);
            
            // CacheHelper 설정
            _mockCacheHelper
                .Setup(x => x.HashSetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<int>()))
                .ReturnsAsync(true);
                
            _mockCacheHelper
                .Setup(x => x.HashSetAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<byte[]>(), It.IsAny<int>()))
                .ReturnsAsync(true);
                
            // ItemInfo 생성 모킹
            _mockCacheHelper
                .Setup(x => x.StringIncrementAsync(It.IsAny<string>(), It.IsAny<int>()))
                .ReturnsAsync(2001);
            
            // GameUser 직접 생성 (테스트용 하위 클래스 사용)
            _gameUser = new TestGameUser(
                _userToken,
                _mockRedLockFactory.Object,
                _testNatsClient,
                _mockLogger.Object,
                _mockCacheHelper.Object,
                user => { },  // 콜백 함수
                new TestChatController()
            );
            
            // 추적 상태 초기화
            _gameUser.ResetTrackers();
            
            // 테스트를 위한 PlayerQuest 생성
            _testPlayerQuest = new TestPlayerQuest(_gameUser, _playerInfo);
            
            // PlayerInventory 인스턴스 생성
            _playerInventory = new PlayerInventory(_gameUser, _playerInfo, _testPlayerQuest);
            
            // 테스트 데이터 설정
            SetupTestData();
        }
        
        private void SetupTestData()
        {
            // GameItemData 및 GameBuffData 설정
            SetupGameItemAndBuffData();
            
            // 테스트용 아이템 추가
            // 장비 아이템
            _playerInfo.InventoryInfo.AddItem(new ItemInfo(1001, 101000001, 1) { IsWear = false, Durability = 100 });
            _playerInfo.InventoryInfo.AddItem(new ItemInfo(1002, 101000002, 1) { IsWear = false, Durability = 100 });
            
            // 소비 아이템
            _playerInfo.InventoryInfo.AddItem(new ItemInfo(1003, 201000001, 5) { IsWear = false });
            _playerInfo.InventoryInfo.AddItem(new ItemInfo(1004, 202000001, 3) { IsWear = false });
        }
        
        private void SetupGameItemAndBuffData()
        {
            try
            {
                // 리플렉션을 사용하여 GameItemData와 GameBuffData 설정
                
                // 1. GameItemData 설정
                SetupGameItemData();
                
                // 2. GameBuffData 설정
                SetupGameBuffData();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Game data setup failed: {ex.Message}");
                // 테스트 실패 시 로그 메시지
            }
        }
        
        private void SetupGameItemData()
        {
            // GameItemData의 Items 필드에 직접 접근
            var itemsField = typeof(GameItemData).GetField("Items", 
                BindingFlags.NonPublic | BindingFlags.Static);
            
            if (itemsField != null)
            {
                var dict = new Dictionary<int, ItemInfoData>();
                
                // 장비 아이템 (101000001)
                var equipItem1 = new Mock<ItemInfoData>();
                equipItem1.Setup(x => x.Id).Returns(101000001);
                equipItem1.Setup(x => x.Type).Returns(ItemType.EQUIPMENT);
                equipItem1.Setup(x => x.IsEquipment).Returns(true);
                equipItem1.Setup(x => x.MaxDurability).Returns(100);
                equipItem1.Setup(x => x.BuffList).Returns(new List<(int id, int value1, int value2)>());
                dict[101000001] = equipItem1.Object;
                
                // 장비 아이템 (101000002)
                var equipItem2 = new Mock<ItemInfoData>();
                equipItem2.Setup(x => x.Id).Returns(101000002);
                equipItem2.Setup(x => x.Type).Returns(ItemType.EQUIPMENT);
                equipItem2.Setup(x => x.IsEquipment).Returns(true);
                equipItem2.Setup(x => x.MaxDurability).Returns(100);
                equipItem2.Setup(x => x.BuffList).Returns(new List<(int id, int value1, int value2)>());
                dict[101000002] = equipItem2.Object;
                
                // 소비 아이템 (201000001)
                var consumableItem1 = new Mock<ItemInfoData>();
                consumableItem1.Setup(x => x.Id).Returns(201000001);
                consumableItem1.Setup(x => x.Type).Returns(ItemType.CONSUMABLE);
                consumableItem1.Setup(x => x.IsConsumable).Returns(true);
                consumableItem1.Setup(x => x.ConsumableBuffList).Returns(new List<(int id, int value)> { (1, 20) });
                dict[201000001] = consumableItem1.Object;
                
                // 소비 아이템 (201000002)
                var consumableItem2 = new Mock<ItemInfoData>();
                consumableItem2.Setup(x => x.Id).Returns(201000002);
                consumableItem2.Setup(x => x.Type).Returns(ItemType.CONSUMABLE);
                consumableItem2.Setup(x => x.IsConsumable).Returns(true);
                consumableItem2.Setup(x => x.ConsumableBuffList).Returns(new List<(int id, int value)> { (2, 10) });
                dict[201000002] = consumableItem2.Object;
                
                // Items 필드에 모킹된 Dictionary 설정
                itemsField.SetValue(null, dict);
            }
        }
        
        private void SetupGameBuffData()
        {
            // GameBuffData의 Buffs 필드에 직접 접근
            var buffsField = typeof(GameBuffData).GetField("Buffs", 
                BindingFlags.NonPublic | BindingFlags.Static);
            
            if (buffsField != null)
            {
                var dict = new Dictionary<int, BuffInfoData>();
                
                // HP 회복 버프
                var hpBuff = new Mock<BuffInfoData>();
                hpBuff.Setup(x => x.Id).Returns(1);
                hpBuff.Setup(x => x.Type).Returns(BuffType.INSTANT);
                hpBuff.Setup(x => x.SubType).Returns(BuffSubType.CONDITION_ADD);
                hpBuff.Setup(x => x.Comment).Returns("HP를 {value1}% 회복합니다.");
                dict[1] = hpBuff.Object;
                
                // 크래프트 매뉴얼 추가 버프
                var craftBuff = new Mock<BuffInfoData>();
                craftBuff.Setup(x => x.Id).Returns(2);
                craftBuff.Setup(x => x.Type).Returns(BuffType.INSTANT);
                craftBuff.Setup(x => x.SubType).Returns(BuffSubType.CRAFT_ADD);
                craftBuff.Setup(x => x.Comment).Returns("크래프트 매뉴얼 {value1}을 얻습니다.");
                dict[2] = craftBuff.Object;
                
                // Buffs 필드에 모킹된 Dictionary 설정
                buffsField.SetValue(null, dict);
            }
        }
        
        [Test]
        public async Task CreateItem_ReturnsNewItemInfo()
        {
            // Arrange
            int itemId = 101000001;
            int count = 1;
            
            // Act
            var result = await PlayerInventory.CreateItem(_mockCacheHelper.Object, itemId, count);
            
            // Assert
            Assert.That(result, Is.Not.Null);
            Assert.That(result.ItemId, Is.EqualTo(itemId));
            Assert.That(result.Count, Is.EqualTo(count));
            Assert.That(result.ItemUid, Is.EqualTo(2001)); // 모킹된 값
            Assert.That(result.Durability, Is.EqualTo(100)); // 기본 내구도
            Assert.That(result.IsWear, Is.False); // 기본값은 착용하지 않음
            Assert.That(result.SkillId, Is.EqualTo(0)); // 기본 스킬 없음
            Assert.That(result.ObjectInfo, Is.Not.Null); // GameObjectInfo가 초기화되어야 함
            Assert.That(result.ObjectInfo.ObjectId, Is.EqualTo(2001)); // ItemUid와 동일한 값으로 설정
            
            // StringIncrementAsync 호출 확인
            _mockCacheHelper.Verify(
                x => x.StringIncrementAsync("item_uid_key", It.IsAny<int>()), 
                Times.Once
            );
        }
        
        [Test]
        public async Task RequestWearItem_EquipsItem_WhenItemIsNotWorn()
        {
            // Arrange
            var request = new C_TO_U_WEAR_ITEM();
            request.ItemUid = 1001;
            
            // Act
            await _playerInventory.RequestWearItem(request);
            
            // Assert
            // 아이템이 착용 상태로 변경되었는지 확인
            Assert.That(_playerInfo.InventoryInfo.ItemDict[1001].IsWear, Is.True, "아이템이 착용 상태로 변경되어야 함");
            Assert.That(_playerInfo.WearItemIdList.Contains(101000001), Is.True, "착용 아이템 목록에 추가되어야 함");
            
            // 패킷이 전송되었는지 확인
            Assert.That(_userToken.PacketWasSent, Is.True, "패킷이 전송되어야 함");
            
            // PlayerInfo.Save가 호출되었는지 검증
            _mockCacheHelper.Verify(
                x => x.HashSetAsync(
                    "PlayerInfo", 
                    It.IsAny<long>(), 
                    It.IsAny<byte[]>(), 
                    It.IsAny<int>()
                ), 
                Times.Once
            );
            
            // 브로드캐스트가 호출되었는지 확인
            Assert.That(_gameUser.BroadcastUpdateInfoCalled, Is.True, "플레이어 정보가 브로드캐스트되어야 함");
        }
        
        [Test]
        public async Task RequestWearItem_UnequipsItem_WhenItemIsWorn()
        {
            // Arrange
            // 먼저 아이템을 착용 상태로 설정
            _playerInfo.InventoryInfo.ItemDict[1001].IsWear = true;
            _playerInfo.WearItemIdList.Add(101000001);
            
            var request = new C_TO_U_WEAR_ITEM { ItemUid = 1001 }; // 이미 착용된 장비 아이템
            
            // Act
            await _playerInventory.RequestWearItem(request);
            
            // Assert
            // 아이템이 착용 해제 상태로 변경되었는지 확인
            Assert.That(_playerInfo.InventoryInfo.ItemDict[1001].IsWear, Is.False, "아이템이 착용 해제 상태로 변경되어야 함");
            Assert.That(_playerInfo.WearItemIdList.Contains(101000001), Is.False, "착용 아이템 목록에서 제거되어야 함");
            
            // 패킷이 전송되었는지 확인
            Assert.That(_userToken.PacketWasSent, Is.True, "패킷이 전송되어야 함");
            
            // PlayerInfo.Save가 호출되었는지 검증
            _mockCacheHelper.Verify(
                x => x.HashSetAsync(
                    "PlayerInfo", 
                    It.IsAny<long>(), 
                    It.IsAny<byte[]>(), 
                    It.IsAny<int>()
                ), 
                Times.Once
            );
            
            // 브로드캐스트가 호출되었는지 확인
            Assert.That(_gameUser.BroadcastUpdateInfoCalled, Is.True, "플레이어 정보가 브로드캐스트되어야 함");
        }
        
        [Test]
        public async Task RequestWearItem_ReplacesExistingItem_WhenSameEquipTypeIsWorn()
        {
            // Arrange
            // 먼저 첫 번째 아이템을 착용 상태로 설정
            _playerInfo.InventoryInfo.ItemDict[1001].IsWear = true;
            _playerInfo.WearItemIdList.Add(101000001);
            
            var request = new C_TO_U_WEAR_ITEM { ItemUid = 1002 }; // 같은 장비 타입의 다른 아이템
            
            // Act
            await _playerInventory.RequestWearItem(request);
            
            // Assert
            // 기존 아이템이 착용 해제되었는지 확인
            Assert.That(_playerInfo.InventoryInfo.ItemDict[1001].IsWear, Is.False, "기존 아이템이 착용 해제되어야 함");
            Assert.That(_playerInfo.WearItemIdList.Contains(101000001), Is.False, "기존 아이템이 착용 목록에서 제거되어야 함");
            
            // 새 아이템이 착용되었는지 확인
            Assert.That(_playerInfo.InventoryInfo.ItemDict[1002].IsWear, Is.True, "새 아이템이 착용되어야 함");
            Assert.That(_playerInfo.WearItemIdList.Contains(101000002), Is.True, "새 아이템이 착용 목록에 추가되어야 함");
            
            // 패킷이 전송되었는지 확인
            Assert.That(_userToken.PacketWasSent, Is.True, "패킷이 전송되어야 함");
            
            // PlayerInfo.Save가 호출되었는지 검증
            _mockCacheHelper.Verify(
                x => x.HashSetAsync(
                    "PlayerInfo", 
                    It.IsAny<long>(), 
                    It.IsAny<byte[]>(), 
                    It.IsAny<int>()
                ), 
                Times.Once
            );
            
            // 브로드캐스트가 호출되었는지 확인
            Assert.That(_gameUser.BroadcastUpdateInfoCalled, Is.True, "플레이어 정보가 브로드캐스트되어야 함");
        }
        
        [Test]
        public void RequestWearItem_ThrowsException_WhenItemNotFound()
        {
            // Arrange
            var request = new C_TO_U_WEAR_ITEM { ItemUid = 9999 }; // 존재하지 않는 아이템
            
            // Act & Assert
            var exception = Assert.ThrowsAsync<Exception>(async () => await _playerInventory.RequestWearItem(request));
            Assert.That(exception.Message, Does.Contain("not found"));
        }
        
        [Test]
        public void RequestWearItem_ThrowsException_WhenItemIsNotEquipment()
        {
            // Arrange
            var request = new C_TO_U_WEAR_ITEM { ItemUid = 1003 }; // 소비 아이템
            
            // Act & Assert
            var exception = Assert.ThrowsAsync<Exception>(async () => await _playerInventory.RequestWearItem(request));
            Assert.That(exception.Message, Does.Contain("not wearable"));
        }
        
        [Test]
        public async Task RequestUseItem_ConsumesItem_WithHpBuff()
        {
            // Arrange
            var request = new C_TO_U_USE_ITEM { ItemUid = 1003 }; // HP 회복 소비 아이템
            int initialHp = _playerInfo.Hp;
            int initialCount = _playerInfo.InventoryInfo.ItemDict[1003].Count;
            
            // Act
            await _playerInventory.RequestUseItem(request);
            
            // Assert
            // 아이템이 소비되었는지 확인
            Assert.That(_playerInfo.InventoryInfo.ItemDict[1003].Count, Is.EqualTo(initialCount - 1), "아이템이 1개 소비되어야 함");
            
            // HP가 증가했는지 확인
            Assert.That(_playerInfo.Hp, Is.GreaterThan(initialHp), "HP가 증가해야 함");
            
            // 패킷이 전송되었는지 확인
            Assert.That(_userToken.PacketWasSent, Is.True, "패킷이 전송되어야 함");
            
            // PlayerInfo.Save가 호출되었는지 검증
            _mockCacheHelper.Verify(
                x => x.HashSetAsync(
                    "PlayerInfo", 
                    It.IsAny<long>(), 
                    It.IsAny<byte[]>(), 
                    It.IsAny<int>()
                ), 
                Times.Once
            );
        }
        
        [Test]
        public async Task RequestUseItem_ConsumesItem_WithCraftBuff()
        {
            // Arrange
            var request = new C_TO_U_USE_ITEM { ItemUid = 1004 }; // 크래프트 매뉴얼 추가 소비 아이템
            int initialCount = _playerInfo.InventoryInfo.ItemDict[1004].Count;
            int initialManualCount = _playerInfo.CraftInfo.Manuals.Count;
            
            // Act
            await _playerInventory.RequestUseItem(request);
            
            // Assert
            // 아이템이 소비되었는지 확인
            Assert.That(_playerInfo.InventoryInfo.ItemDict[1004].Count, Is.EqualTo(initialCount - 1), "아이템이 1개 소비되어야 함");
            
            // 크래프트 매뉴얼이 추가되었는지 확인
            Assert.That(_playerInfo.CraftInfo.Manuals.Count, Is.GreaterThan(initialManualCount), "크래프트 매뉴얼이 추가되어야 함");
            
            // 패킷이 전송되었는지 확인
            Assert.That(_userToken.PacketWasSent, Is.True, "패킷이 전송되어야 함");
            
            // PlayerInfo.Save가 호출되었는지 검증
            _mockCacheHelper.Verify(
                x => x.HashSetAsync(
                    "PlayerInfo", 
                    It.IsAny<long>(), 
                    It.IsAny<byte[]>(), 
                    It.IsAny<int>()
                ), 
                Times.Once
            );
        }
        
        [Test]
        public void RequestUseItem_ThrowsException_WhenItemNotFound()
        {
            // Arrange
            var request = new C_TO_U_USE_ITEM { ItemUid = 9999 }; // 존재하지 않는 아이템
            
            // Act & Assert
            var exception = Assert.ThrowsAsync<Exception>(async () => await _playerInventory.RequestUseItem(request));
            Assert.That(exception.Message, Does.Contain("not found"));
        }
        
        [Test]
        public void RequestUseItem_ThrowsException_WhenItemIsNotConsumable()
        {
            // Arrange
            var request = new C_TO_U_USE_ITEM { ItemUid = 1001 }; // 장비 아이템
            
            // Act & Assert
            var exception = Assert.ThrowsAsync<Exception>(async () => await _playerInventory.RequestUseItem(request));
            Assert.That(exception.Message, Does.Contain("not consumable"));
        }
        
        [Test]
        public void RequestUseItem_ThrowsException_WhenItemCountNotEnough()
        {
            // Arrange
            // 아이템 개수를 0으로 설정
            _playerInfo.InventoryInfo.ItemDict[1003].Count = 0;
            
            var request = new C_TO_U_USE_ITEM { ItemUid = 1003 }; // 소비 아이템
            
            // Act & Assert
            var exception = Assert.ThrowsAsync<Exception>(async () => await _playerInventory.RequestUseItem(request));
            Assert.That(exception.Message, Does.Contain("count not enough"));
        }
        
        [Test]
        public void SendCurrentItems_SendsAllItems_WhenInventoryHasItems()
        {
            // Arrange
            _userToken.PacketWasSent = false;
            
            // Act
            _playerInventory.SendCurrentItems();
            
            // Assert
            // 패킷이 전송되었는지 확인
            Assert.That(_userToken.PacketWasSent, Is.True, "패킷이 전송되어야 함");
        }
        
        [Test]
        public void SendCurrentItems_SendsEmptyPacket_WhenInventoryIsEmpty()
        {
            // Arrange
            _playerInfo.InventoryInfo.ItemDict.Clear();
            _userToken.PacketWasSent = false;
            
            // Act
            _playerInventory.SendCurrentItems();
            
            // Assert
            // 패킷이 전송되었는지 확인
            Assert.That(_userToken.PacketWasSent, Is.True, "빈 패킷이 전송되어야 함");
        }
        
        [Test]
        public void SendUpdateItems_SendsUpdatePackets()
        {
            // Arrange
            var updateItems = new List<ItemInfo>
            {
                new ItemInfo(1001, 101000001, 1),
                new ItemInfo(1002, 101000002, 1)
            };
            
            _userToken.PacketWasSent = false;
            
            // Act
            _playerInventory.SendUpdateItems(updateItems);
            
            // Assert
            // 패킷이 전송되었는지 확인
            Assert.That(_userToken.PacketWasSent, Is.True, "업데이트 패킷이 전송되어야 함");
        }
        
        [Test]
        public void SendUpdateItems_DoesNotSendPacket_WhenNoItems()
        {
            // Arrange
            var updateItems = new List<ItemInfo>();
            _userToken.PacketWasSent = false;
            
            // Act
            _playerInventory.SendUpdateItems(updateItems);
            
            // Assert
            // 패킷이 전송되지 않았는지 확인
            Assert.That(_userToken.PacketWasSent, Is.False, "아이템이 없으면 패킷이 전송되지 않아야 함");
        }
        
        private PlayerInfo CreateTestPlayerInfo()
        {
            var playerId = 1001;
            var playerInfo = new PlayerInfo(playerId, false)
            {
                Name = "TestPlayer",
                State = PlayerState.IDLE,
                Hp = 5000,
                ObjectInfo =
                {
                    MapId = MapId.Library,
                    ObjectType = ObjectType.PLAYER,
                    CurrentCell = new Cell(10, 10),
                    TargetCell = new Cell(10, 10)
                },
                // 필요한 정보 초기화
                InventoryInfo = new InventoryInfo(InventoryOwnerType.PLAYER, playerId),
                CraftInfo = new CraftInfo(playerId),
                WearItemIdList = new List<int>()
            };
            
            return playerInfo;
        }
    }
}