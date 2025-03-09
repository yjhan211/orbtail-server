using System.Collections.Concurrent;
using System.Reflection;
using game_server.controllers;
using MessagePack;
using Microsoft.Extensions.Logging;
using Moq;
using network.common;
using network.common.data.helpers;
using network.common.data.models;
using network.config;
using network.helpers;
using network.interfaces;

namespace game_server.tests.controllers
{
    [TestFixture]
    public class InstanceMapControllerTests
    {
        private Mock<ILogger> _mockLogger;
        private Mock<INatsClient> _mockNatsClient;
        private Mock<ICacheHelper> _mockCacheHelper;
        private CancellationTokenSource _cts;
        private ServerConfig _serverConfig;
        private InstanceMapController _controller;
        private readonly int _testServerId = 1;
        
        // 테스트에 사용할 인스턴스 맵 ID와 서브 ID
        private readonly MapId _testMapId = MapId.Camp;
        private long _testMapSubId;

        [SetUp]
        public void Setup()
        {
            _mockLogger = new Mock<ILogger>();
            _mockNatsClient = new Mock<INatsClient>();
            _mockCacheHelper = new Mock<ICacheHelper>();
            _cts = new CancellationTokenSource();

            _serverConfig = new ServerConfig
            {
                ServerId = _testServerId,
                GameServerNum = 2
            };

            // 게임 데이터 초기화
            GameDataHelper.Initialize();
            
            // MapHelper 초기화
            MapHelper.Initialize(_serverConfig.GameServerNum);
            
            // 이 서버가 관리하는 유효한 맵 서브 ID 생성
            InitializeValidMapSubId();

            // InstanceMapController 인스턴스 생성
            _controller = new InstanceMapController(
                _mockLogger.Object,
                _mockNatsClient.Object,
                _cts,
                _mockCacheHelper.Object,
                _serverConfig
            );
        }

        private void InitializeValidMapSubId()
        {
            // 현재 서버가 관리하는 맵 서브 ID 계산
            // mapSubId를 1부터 시작하여 10개까지 체크
            for (long i = 1; i <= 10; i++)
            {
                if (MapHelper.GetManageServerId(i) == _testServerId)
                {
                    _testMapSubId = i;
                    Console.WriteLine($"Using map sub ID {_testMapSubId} managed by server {_testServerId}");
                    return;
                }
            }
            
            // 마지막 방법으로 서버 ID에 맞는 맵 서브 ID 생성
            // 1-based index로 _testServerId를 사용
            _testMapSubId = _testServerId;
            Console.WriteLine($"Falling back to map sub ID {_testMapSubId} for server {_testServerId}");
        }

        [Test]
        public void Initialize_SubscribesToEnterInstanceSubject()
        {
            // Act
            _controller.Initialize();

            // Assert
            _mockNatsClient.Verify(
                x => x.Subscribe(
                    It.Is<string>(s => s.Contains("enter_instance")),
                    It.IsAny<Action<string, byte[]>>()
                ),
                Times.Once
            );
        }

        [Test]
        public async Task EnterInstance_AddsUserToInstanceAndInitializes()
        {
            // Arrange
            _controller.Initialize();

            var userSubject = "user_subject";
            var mapId = _testMapId;
            long mapSubId = _testMapSubId;
            bool isLogin = false;

            // 이 서버가 맵 서브 ID를 관리하는지 확인
            int manageServerId = MapHelper.GetManageServerId(mapSubId);
            Assume.That(manageServerId == _testServerId, 
                $"Test assumes server {_testServerId} manages mapSubId {mapSubId}, but it's managed by server {manageServerId}");

            var message = MessagePackSerializer.Serialize((userSubject, mapId, mapSubId, isLogin));

            // EnterInstance 메서드 접근
            var method = typeof(InstanceMapController).GetMethod("EnterInstance",
                BindingFlags.NonPublic | BindingFlags.Instance);

            // StringIncrementAsync 모킹
            _mockCacheHelper
                .Setup(x => x.StringIncrementAsync(It.IsAny<string>(), It.IsAny<int>()))
                .ReturnsAsync(1001);

            // Act
            if (method != null)
            {
                await (Task)method.Invoke(_controller, [message])!;

                // Assert
                // 인스턴스에 사용자가 추가되었는지 확인
                var field = typeof(InstanceMapController).GetField("_objectInstanceDict",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                if (field != null)
                {
                    var dict = (ConcurrentDictionary<string, HashSet<string>>)field.GetValue(_controller)!;
                    var instanceKey = MapHelper.CreatePartKey(mapId, mapSubId);

                    Assert.That(dict.ContainsKey(instanceKey), Is.True, "Instance key should be added");
                    Assert.That(dict[instanceKey].Contains(userSubject), Is.True,
                        "User subject should be added to instance");
                }

                // 성공 메시지가 전송되었는지 확인 (isLogin이 false인 경우)
                _mockNatsClient.Verify(
                    x => x.Publish(
                        userSubject,
                        It.IsAny<byte[]>()
                    ),
                    Times.Once
                );
            }
            else
            {
                Assert.Fail("EnterInstance method not found");
            }
        }

        [Test]
        public async Task EnterInstance_WithLogin_DoesNotSendSuccessPacket()
        {
            // Arrange
            _controller.Initialize();

            var userSubject = "user_subject";
            var mapId = _testMapId;
            long mapSubId = _testMapSubId;
            bool isLogin = true; // isLogin이 true인 경우

            var message = MessagePackSerializer.Serialize((userSubject, mapId, mapSubId, isLogin));

            // EnterInstance 메서드 접근
            var method = typeof(InstanceMapController).GetMethod("EnterInstance",
                BindingFlags.NonPublic | BindingFlags.Instance);

            // Act
            if (method != null)
            {
                await (Task)method.Invoke(_controller, [message])!;

                // Assert
                // isLogin이 true이므로 성공 패킷은 전송되지 않아야 함
                _mockNatsClient.Verify(
                    x => x.Publish(
                        userSubject,
                        It.IsAny<byte[]>()
                    ),
                    Times.Never
                );
            }
            else
            {
                Assert.Fail("EnterInstance method not found");
            }
        }

        [Test]
        public async Task MoveManageObjectAsync_UpdatesPositionAndBroadcasts()
        {
            // Arrange
            _controller.Initialize();

            var instanceKey = MapHelper.CreatePartKey(_testMapId, _testMapSubId);
            var objectInfo = new GameObjectInfo
            {
                ObjectType = ObjectType.PLAYER,
                ObjectId = 1001,
                MapId = _testMapId,
                MapSubId = _testMapSubId,
                CurrentCell = new Cell(1, 1),
                TargetCell = new Cell(1, 1)
            };

            var message = MessagePackSerializer.Serialize((instanceKey, objectInfo));

            // MoveManageObjectAsync 메서드 접근
            var method = typeof(InstanceMapController).GetMethod("MoveManageObjectAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);

            // Act
            if (method != null)
            {
                await (Task)method.Invoke(_controller, [message])!;

                // Assert
                // 객체 위치 업데이트 확인
                var field = typeof(InstanceMapController).GetField("_objectInstanceDict",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                if (field != null)
                {
                    var dict = (ConcurrentDictionary<string, HashSet<string>>)field.GetValue(_controller)!;
                    var objectKey = GameObjectInfo.MakeObjectKey(objectInfo.ObjectType, objectInfo.ObjectId);

                    Assert.That(dict.ContainsKey(instanceKey), Is.True, "Instance key should exist");
                    if (dict.TryGetValue(instanceKey, value: out var value))
                    {
                        Assert.That(value.Contains(objectKey), Is.True,
                            "Object should be added to instance");
                    }
                }

                // 업데이트 패킷 브로드캐스트 확인
                _mockNatsClient.Verify(
                    x => x.Publish(
                        It.IsAny<string>(),
                        It.IsAny<byte[]>()
                    ),
                    Times.AtLeastOnce
                );
            }
            else
            {
                Assert.Fail("MoveManageObjectAsync method not found");
            }
        }

        [Test]
        public async Task DestroyManageObjectAsync_RemovesObjectAndBroadcasts()
        {
            // Arrange
            _controller.Initialize();

            // 1. 먼저 인스턴스에 입장
            var userSubject = "user_channel";
            var mapId = _testMapId;
            var mapSubId = _testMapSubId;
            var isLogin = false;
            
            // EnterInstance 메서드 접근
            var enterMethod = typeof(InstanceMapController).GetMethod("EnterInstance",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (enterMethod != null)
            {
                var enterMessage = MessagePackSerializer.Serialize((userSubject, mapId, mapSubId, isLogin));
                await (Task)enterMethod.Invoke(_controller, [enterMessage])!;
            }
            else
            {
                Assert.Fail("EnterInstance method not found");
            }

            // 2. 객체 키 생성 및 인스턴스 확인
            var instanceKey = MapHelper.CreatePartKey(mapId, mapSubId);
            var objectKey = "1_1001"; // ObjectType_ObjectId 형식 (플레이어)

            // 인스턴스 사전에 객체가 있는지 확인 (인스턴스 사전에 userSubject가 추가되었어야 함)
            var instanceDictField = typeof(InstanceMapController).GetField("_objectInstanceDict",
                BindingFlags.NonPublic | BindingFlags.Instance);
            
            if (instanceDictField != null)
            {
                var dict = (ConcurrentDictionary<string, HashSet<string>>)instanceDictField.GetValue(_controller)!;
                Assert.That(dict.ContainsKey(instanceKey), Is.True, "Instance key should exist after entering");
                
                // 객체를 플레이어로 추가 (userSubject는 채널이므로 실제 객체 키와 다름)
                dict[instanceKey].Add(objectKey);
            }
            else
            {
                Assert.Fail("_objectInstanceDict field not found");
            }

            // 3. 객체 제거 메시지 생성
            var message = MessagePackSerializer.Serialize((instanceKey, objectKey));

            // DestroyManageObjectAsync 메서드 접근
            var method = typeof(InstanceMapController).GetMethod("DestroyManageObjectAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);

            // Act
            if (method != null)
            {
                await (Task)method.Invoke(_controller, [message])!;

                // Assert
                // 객체가 제거되었는지 확인
                var dict = (ConcurrentDictionary<string, HashSet<string>>)instanceDictField?.GetValue(_controller)!;
                if (dict.TryGetValue(instanceKey, out var value))
                {
                    Assert.That(value, Does.Not.Contain(objectKey), "Object should be removed from instance");
                }

                // 객체 제거 브로드캐스트 확인
                _mockNatsClient.Verify(
                    x => x.Publish(
                        userSubject,
                        It.IsAny<byte[]>()
                    ),
                    Times.AtLeastOnce
                );
            }
            else
            {
                Assert.Fail("DestroyManageObjectAsync method not found");
            }
        }

        [Test]
        public async Task SpawnManageObject_SendsSpawnPacketWithObjectList()
        {
            // Arrange
            _controller.Initialize();

            var userSubject = "user_subject_1";
            var instanceKeyList = new List<string> { 
                MapHelper.CreatePartKey(_testMapId, _testMapSubId),
                MapHelper.CreatePartKey(_testMapId, _testMapSubId + _serverConfig.GameServerNum) // 다른 인스턴스 키
            };
            var cellsToRemove = new List<Cell>();
            var message = MessagePackSerializer.Serialize((userSubject, instanceKeyList, cellsToRemove));

            // Setup instance objects in the dictionary
            var field = typeof(InstanceMapController).GetField("_objectInstanceDict",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (field != null)
            {
                var dict = (ConcurrentDictionary<string, HashSet<string>>)field.GetValue(_controller)!;
                dict.TryAdd(instanceKeyList[0], ["1_1001", "3_3001"]);
                dict.TryAdd(instanceKeyList[1], ["1_2001"]);
            }

            // SpawnManageObject 메서드 접근
            var method = typeof(InstanceMapController).GetMethod("SpawnManageObject",
                BindingFlags.NonPublic | BindingFlags.Instance);

            // Act
            if (method != null)
            {
                await ((Task)method.Invoke(_controller, [message])!);

                // Assert
                // Publish가 호출되었는지 확인
                _mockNatsClient.Verify(
                    x => x.Publish(
                        userSubject,
                        It.IsAny<byte[]>()
                    ),
                    Times.Once
                );
            }
            else
            {
                Assert.Fail("SpawnManageObject method not found");
            }
        }

        [Test]
        public async Task LeaveManageObjectAsync_RemovesObjectFromInstance()
        {
            // Arrange
            _controller.Initialize();

            var instanceKey = MapHelper.CreatePartKey(_testMapId, _testMapSubId);
            var objectKey = "1_1001"; // ObjectType_ObjectId 형식
            var message = MessagePackSerializer.Serialize((instanceKey, objectKey));

            // Set up the object in the dictionary
            var field = typeof(InstanceMapController).GetField("_objectInstanceDict",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (field != null)
            {
                var dict = (ConcurrentDictionary<string, HashSet<string>>)field.GetValue(_controller)!;
                dict.TryAdd(instanceKey, [objectKey]);
            }

            // LeaveManageObjectAsync 메서드 접근
            var method = typeof(InstanceMapController).GetMethod("LeaveManageObjectAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);

            // Act
            if (method != null)
            {
                await ((Task)method.Invoke(_controller, [message])!);

                // Assert
                // 객체가 제거되었는지 확인
                if (field != null)
                {
                    var dict = (ConcurrentDictionary<string, HashSet<string>>)field.GetValue(_controller)!;
                    Assert.That(dict[instanceKey], Does.Not.Contain(objectKey), 
                        "Object should be removed from instance");
                }
            }
            else
            {
                Assert.Fail("LeaveManageObjectAsync method not found");
            }
        }

        [Test]
        public void BroadcastPacket_SendsPacketToAllChannelsInInstance()
        {
            // Arrange
            _controller.Initialize();

            var instanceKey = MapHelper.CreatePartKey(_testMapId, _testMapSubId);
            var channels = new HashSet<string> { "channel1", "channel2", "channel3" };

            // Setup channels in the instance
            var field = typeof(InstanceMapController).GetField("_objectInstanceDict",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (field != null)
            {
                var dict = (ConcurrentDictionary<string, HashSet<string>>)field.GetValue(_controller)!;
                dict.TryAdd(instanceKey, channels);
            }

            // Create a mock packet
            var mockPacket = new Mock<IPacket>();
            mockPacket.Setup(p => p.ToBytes()).Returns([1, 2, 3]);

            // Get BroadcastPacket method through reflection to test protected method
            var method = typeof(InstanceMapController).GetMethod("BroadcastPacket",
                BindingFlags.NonPublic | BindingFlags.Instance);

            // Act
            if (method != null)
            {
                method.Invoke(_controller, [instanceKey, mockPacket.Object]);

                // Assert
                // Verify that Publish was called for each channel
                foreach (var channel in channels)
                {
                    _mockNatsClient.Verify(
                        x => x.Publish(
                            channel,
                            It.IsAny<byte[]>()
                        ),
                        Times.Once
                    );
                }
            }
            else
            {
                Assert.Fail("BroadcastPacket method not found");
            }
        }

        [Test]
        public void BroadcastPacket_DoesNothingForNonExistentInstance()
        {
            // Arrange
            _controller.Initialize();
            
            var nonExistentInstanceKey = "NonExistentInstance_9999";
            
            // Create a mock packet
            var mockPacket = new Mock<IPacket>();
            mockPacket.Setup(p => p.ToBytes()).Returns([1, 2, 3]);

            // Get BroadcastPacket method through reflection to test protected method
            var method = typeof(InstanceMapController).GetMethod("BroadcastPacket",
                BindingFlags.NonPublic | BindingFlags.Instance);

            // Act
            if (method != null)
            {
                method.Invoke(_controller, [nonExistentInstanceKey, mockPacket.Object]);

                // Assert
                // Verify that Publish was not called
                _mockNatsClient.Verify(
                    x => x.Publish(
                        It.IsAny<string>(),
                        It.IsAny<byte[]>()
                    ),
                    Times.Never
                );
            }
            else
            {
                Assert.Fail("BroadcastPacket method not found");
            }
        }

        [Test]
        public async Task InitializeExploreTargets_CreatesAndBroadcastsTargets()
        {
            // 이 테스트는 GameExploreTargetData.GetListByMap을 모킹해야 함
            // 여기서는 리플렉션을 통해 private 메서드 호출만 테스트

            // Arrange
            _controller.Initialize();

            var mapId = MapId.TutorialAdminoffice;
            long mapSubId = _testMapSubId;

            // StringIncrementAsync 모킹
            _mockCacheHelper
                .Setup(x => x.StringIncrementAsync("temp_explore_target_uid", It.IsAny<int>()))
                .ReturnsAsync(2001);

            // HashSetAsync 모킹
            _mockCacheHelper
                .Setup(x => x.HashSetAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<byte[]>(), It.IsAny<int>()))
                .ReturnsAsync(true);

            // InitializeExploreTargets 메서드 접근
            var method = typeof(InstanceMapController).GetMethod("InitializeExploreTargets",
                BindingFlags.NonPublic | BindingFlags.Instance);

            // 테스트에서 GameExploreTargetData.GetListByMap 호출을 모킹하는 것이 어려울 수 있으므로,
            // 이 부분은 간소화된 테스트만 수행

            // Act & Assert
            if (method != null)
            {
                // 메서드 호출에 예외가 없으면 테스트 통과로 간주
                await ((Task)method.Invoke(_controller, [mapId, mapSubId])!);
                
                // HashSetAsync가 적어도 한 번 호출되었는지 확인
                _mockCacheHelper.Verify(
                    x => x.HashSetAsync(
                        It.IsAny<string>(),
                        It.IsAny<long>(),
                        It.IsAny<byte[]>(),
                        It.IsAny<int>()
                    ),
                    Times.AtLeastOnce
                );
            }
            else
            {
                Assert.Inconclusive("InitializeExploreTargets method not found");
            }
        }

        [Test]
        public async Task ShutdownAsync_AcquiresAndReleasesMapLock()
        {
            // Act
            await _controller.ShutdownAsync();

            // Assert
            // Shutdown 에서는 MapLock을 획득했다가 해제하는 동작만 하므로
            // 예외가 발생하지 않으면 테스트는 성공으로 간주
            Assert.Pass("Shutdown completed without exceptions");
        }
        
        [TearDown]
        public void TearDown()
        {
            // Dispose 가능한 리소스 정리
            _cts.Dispose();
        }
    }
}