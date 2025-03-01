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
    public class CommonMapControllerTests
    {
        private Mock<ILogger> _mockLogger;
        private Mock<INatsClient> _mockNatsClient;
        private Mock<ICacheHelper> _mockCacheHelper;
        private CancellationTokenSource _cts;
        private IServerConfig _serverConfig;
        private CommonMapController _controller;
        private readonly int _testServerId = 1;
        private readonly MapId _testMapId = MapId.Library;
        private Cell[]? _validCells; // 서버가 관리하는 셀 좌표 배열

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
            
            // 서버가 관리하는 유효한 셀 좌표 가져오기
            InitializeValidCells();

            // CommonMapController 인스턴스 생성
            _controller = new CommonMapController(
                _mockLogger.Object,
                _mockNatsClient.Object,
                _cts,
                _mockCacheHelper.Object,
                _serverConfig,
                _testMapId
            );
        }

        private void InitializeValidCells()
        {
            // PositionListByMapPart에서 실제 서버가 담당하는 영역의 좌표 가져오기
            var managePartList = MapHelper.GetManagePartList(_testServerId);
            
            if (managePartList.TryGetValue(_testMapId, out var positionList) && positionList.Count > 0)
            {
                _validCells = positionList
                    .Select(MapHelper.CreateCell)
                    .ToArray();
                
                Console.WriteLine($"Found {_validCells.Length} valid cells for server {_testServerId}, map {_testMapId}");
                
                if (_validCells.Length == 0)
                {
                    Assert.Inconclusive($"No valid cells found for server {_testServerId}, map {_testMapId}");
                }
            }
            else
            {
                // 서버가 해당 맵을 관리하지 않는 경우 테스트 스킵
                Assert.Inconclusive($"Server {_testServerId} does not manage map {_testMapId}");
            }
        }

        [Test]
        public void Initialize_SubscribesToCorrectSubjects()
        {
            // Act
            _controller.Initialize();

            // Assert
            // 필요한 이벤트 핸들러 등록 확인
            _mockNatsClient.Verify(
                x => x.Subscribe(
                    It.Is<string>(s => s.Contains("update_info")),
                    It.IsAny<Action<string, byte[]>>()
                ),
                Times.Once
            );

            _mockNatsClient.Verify(
                x => x.Subscribe(
                    It.Is<string>(s => s.Contains("social_action")),
                    It.IsAny<Action<string, byte[]>>()
                ),
                Times.Once
            );

            // 다른 Subscribe 확인 생략...
        }

        [Test]
        public async Task MoveManageObjectAsync_UpdatesPositionAndBroadcasts()
        {
            Assume.That(_validCells is { Length: >= 2 }, "Need at least 2 valid cells for this test");
            
            // Arrange
            _controller.Initialize(); // 필요한 초기화 수행

            // 테스트에 필요한 객체 정보
            var objectInfo = new GameObjectInfo
            {
                ObjectType = ObjectType.PLAYER,
                ObjectId = 1001,
                MapId = _testMapId,
                CurrentCell = _validCells[1], // 두 번째 유효한 셀
                TargetCell = _validCells[1]
            };

            // 서버가 실제로 관리하는 위치 키 사용
            var lastPositionKey = MapHelper.CreatePartKey(_testMapId, _validCells[0]); // 첫 번째 유효한 셀
            var message = MessagePackSerializer.Serialize((lastPositionKey, objectInfo));

            // GetMoveManageObjectMethodAsync 메서드 접근
            var method = typeof(CommonMapController).GetMethod("UpdateManageObjectAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);

            // Act
            if (method != null)
            {
                await ((Task)method.Invoke(_controller, [message])!)!;

                // Assert
                // 객체 위치 업데이트 및 브로드캐스트 확인
                _mockNatsClient.Verify(
                    x => x.Publish(
                        It.Is<string>(s => s.Contains("broadcast_update")),
                        It.IsAny<byte[]>()
                    ),
                    Times.AtLeastOnce
                );
            }
            else
            {
                Assert.Fail("UpdateManageObjectAsync method not found");
            }
        }

        [Test]
        public async Task LeaveManageObjectAsync_RemovesObjectFromPosition()
        {
            Assume.That(_validCells is { Length: >= 1 }, "Need at least 1 valid cell for this test");
            
            // Arrange
            _controller.Initialize();

            // 서버가 관리하는 위치 키 사용
            var positionKey = MapHelper.CreatePartKey(_testMapId, _validCells[0]); // 첫 번째 유효한 셀
            var objectKey = "1_1001"; // ObjectType_ObjectId 형식
            var message = MessagePackSerializer.Serialize((positionKey, objectKey));

            // 객체 위치 정보 설정 (private 필드에 접근)
            var field = typeof(CommonMapController).GetField("_objectPositionDict",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (field != null)
            {
                var dict = (ConcurrentDictionary<string, HashSet<string>>)field.GetValue(_controller)!;
                if (!dict.ContainsKey(positionKey))
                {
                    // Initialize가 호출되지 않았거나 해당 키가 없는 경우 추가
                    dict[positionKey] = [objectKey];
                }
                else if (dict.TryGetValue(positionKey, out var set))
                {
                    set.Add(objectKey);
                }
            }

            // LeaveManageObjectAsync 메서드 접근
            var method = typeof(CommonMapController).GetMethod("LeaveManageObjectAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);

            // Act
            if (method != null)
            {
                await ((Task)method.Invoke(_controller, [message])!)!;

                // Assert
                // 객체가 제거되었는지 확인
                if (field != null)
                {
                    var dict = (ConcurrentDictionary<string, HashSet<string>>)field.GetValue(_controller)!;
                    if (dict.TryGetValue(positionKey, out var set))
                    {
                        Assert.That(set.Contains(objectKey), Is.False, "Object should be removed from position");
                    }
                }
            }
            else
            {
                Assert.Fail("LeaveManageObjectAsync method not found");
            }
        }

        [Test]
        public async Task SpawnManageObjectAsync_SendsSpawnListToUser()
        {
            Assume.That(_validCells is { Length: >= 2 }, "Need at least 2 valid cells for this test");
            
            // Arrange
            _controller.Initialize();

            var userSubject = "user_subject";
            // 서버가 관리하는 위치 키 목록 사용
            var positionKeyList = new List<string> 
            { 
                MapHelper.CreatePartKey(_testMapId, _validCells[0]), // 첫 번째 유효한 셀
                MapHelper.CreatePartKey(_testMapId, _validCells[1])  // 두 번째 유효한 셀
            };
            
            // 제거할 셀 목록 - 다른 서버의 셀을 찾기 어려우므로 일단 존재하지 않는 셀 사용
            var cellsToRemove = new List<Cell> { new Cell(9999, 9999) };
            var message = MessagePackSerializer.Serialize((userSubject, positionKeyList, cellsToRemove));

            // 객체 위치 정보 설정
            var field = typeof(CommonMapController).GetField("_objectPositionDict",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (field != null)
            {
                var dict = (ConcurrentDictionary<string, HashSet<string>>)field.GetValue(_controller)!;
                // 첫 번째 위치에 객체 추가
                if (!dict.ContainsKey(positionKeyList[0]))
                {
                    dict[positionKeyList[0]] = new HashSet<string>();
                }
                dict[positionKeyList[0]].Add("1_1001"); // 플레이어 추가
                dict[positionKeyList[0]].Add("3_2001"); // 탐험 대상 추가

                // 두 번째 위치에 객체 추가
                if (!dict.ContainsKey(positionKeyList[1]))
                {
                    dict[positionKeyList[1]] = new HashSet<string>();
                }
                dict[positionKeyList[1]].Add("5_3001"); // 캠프 추가
            }

            // SpawnManageObjectAsync 메서드 접근
            var method = typeof(CommonMapController).GetMethod("SpawnManageObjectAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);

            // Act
            if (method != null)
            {
                await (Task)method.Invoke(_controller, [message])!;

                // Assert
                // G_TO_U_SPAWN 패킷이 전송되었는지 확인
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
                Assert.Fail("SpawnManageObjectAsync method not found");
            }
        }

        [Test]
        public async Task DestroyManageObjectAsync_RemovesObjectAndBroadcasts()
        {
            Assume.That(_validCells is { Length: >= 1 }, "Need at least 1 valid cell for this test");
            
            // Arrange
            _controller.Initialize();

            // 서버가 관리하는 위치 키 사용
            var positionKey = MapHelper.CreatePartKey(_testMapId, _validCells[0]); // 첫 번째 유효한 셀
            var objectKey = "1_1001";
            var message = MessagePackSerializer.Serialize((positionKey, objectKey));

            // 객체 위치 정보 설정
            var field = typeof(CommonMapController).GetField("_objectPositionDict",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (field != null)
            {
                var dict = (ConcurrentDictionary<string, HashSet<string>>)field.GetValue(_controller)!;
                foreach (var key in dict.Keys.ToList())
                {
                    if (!dict.TryGetValue(key, out var set))
                    {
                        dict[key] = new HashSet<string>();
                    }
                    dict[key].Add(objectKey);
                }
            }

            // DestroyManageObjectAsync 메서드 접근
            var method = typeof(CommonMapController).GetMethod("DestroyManageObjectAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);

            // Act
            if (method != null)
            {
                await ((Task)method.Invoke(_controller, [message])!);

                // Assert
                // 모든 위치에서 객체가 제거되었는지 확인
                if (field != null)
                {
                    var dict = (ConcurrentDictionary<string, HashSet<string>>)field.GetValue(_controller)!;
                    foreach (var set in dict.Values)
                    {
                        Assert.That(set, Does.Not.Contain(objectKey), "Object should be removed from all positions");
                    }
                }

                // 객체 제거 브로드캐스트 확인
                _mockNatsClient.Verify(
                    x => x.Publish(
                        It.Is<string>(s => s.Contains("broadcast_destroy")),
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
        public void BroadcastUpdateObject_SendsUpdateObjectPacket()
        {
            Assume.That(_validCells is { Length: >= 1 }, "Need at least 1 valid cell for this test");
            
            // Arrange
            _controller.Initialize();

            // 서버가 관리하는 위치 키 사용
            var positionKey = MapHelper.CreatePartKey(_testMapId, _validCells[0]); // 첫 번째 유효한 셀
            var objectInfo = new GameObjectInfo
            {
                ObjectType = ObjectType.PLAYER,
                ObjectId = 1001,
                MapId = _testMapId,
                CurrentCell = _validCells[0],
                TargetCell = _validCells[0]
            };
            var message = MessagePackSerializer.Serialize((positionKey, objectInfo));

            // 테스트를 위해 객체 위치 정보에 채널 추가
            var field = typeof(CommonMapController).GetField("_objectPositionDict",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (field != null)
            {
                var dict = (ConcurrentDictionary<string, HashSet<string>>)field.GetValue(_controller)!;
                if (!dict.TryGetValue(positionKey, out HashSet<string>? value))
                {
                    value = new HashSet<string>();
                    dict[positionKey] = value;
                }

                value.Add("channel1");
                value.Add("channel2");
            }

            // BroadcastUpdateObject 메서드 접근 및 호출
            var method = typeof(CommonMapController).GetMethod("BroadcastUpdateObject",
                BindingFlags.NonPublic | BindingFlags.Instance);

            // Act
            if (method != null)
            {
                method.Invoke(_controller, ["subject", message]);

                // Assert
                // 채널별로 패킷이 전송되었는지 확인
                _mockNatsClient.Verify(
                    x => x.Publish(
                        "channel1",
                        It.IsAny<byte[]>()
                    ),
                    Times.Once
                );

                _mockNatsClient.Verify(
                    x => x.Publish(
                        "channel2",
                        It.IsAny<byte[]>()
                    ),
                    Times.Once
                );
            }
            else
            {
                Assert.Fail("BroadcastUpdateObject method not found");
            }
        }

        [Test]
        public async Task BroadcastObjectMove_SendsUpdateToAffectedCells()
        {
            Assume.That(_validCells is { Length: >= 2 }, "Need at least 2 valid cells for this test");
            
            // Arrange
            _controller.Initialize();

            // 서버가 관리하는 위치 키 사용
            var positionKey = MapHelper.CreatePartKey(_testMapId, _validCells[0]); // 첫 번째 유효한 셀
            var objectInfo = new GameObjectInfo
            {
                ObjectType = ObjectType.PLAYER,
                ObjectId = 1001,
                MapId = _testMapId,
                CurrentCell = _validCells[1], // 두 번째 유효한 셀로 이동
                TargetCell = _validCells[1]
            };

            // BroadcastObjectMove 메서드 접근
            var method = typeof(CommonMapController).GetMethod("BroadcastObjectMove",
                BindingFlags.NonPublic | BindingFlags.Instance);

            // Act
            if (method != null)
            {
                await (Task)method.Invoke(_controller, [positionKey, objectInfo])!;

                // Assert
                // 브로드캐스트 확인 - 새 위치와 이전 위치에 대한 업데이트
                _mockNatsClient.Verify(
                    x => x.Publish(
                        It.Is<string>(s => s.Contains("broadcast_update")),
                        It.IsAny<byte[]>()
                    ),
                    Times.AtLeastOnce
                );
            }
            else
            {
                Assert.Fail("BroadcastObjectMove method not found");
            }
        }

        [Test]
        public async Task UpdateObjectPositionAsync_UpdatesPositionCorrectly()
        {
            Assume.That(_validCells is { Length: >= 2 }, "Need at least 2 valid cells for this test");
            
            // Arrange
            _controller.Initialize();

            // 서버가 관리하는 위치 키 사용
            var lastPositionKey = MapHelper.CreatePartKey(_testMapId, _validCells[0]); // 첫 번째 유효한 셀
            var currentPositionKey = MapHelper.CreatePartKey(_testMapId, _validCells[1]); // 두 번째 유효한 셀
            var objectKey = "1_1001"; // ObjectType_ObjectId 형식

            // 객체 위치 정보 설정
            var field = typeof(CommonMapController).GetField("_objectPositionDict",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (field != null)
            {
                var dict = (ConcurrentDictionary<string, HashSet<string>>)field.GetValue(_controller)!;
                if (!dict.TryGetValue(lastPositionKey, out HashSet<string>? value))
                {
                    value = new HashSet<string>();
                    dict[lastPositionKey] = value;
                }

                value.Add(objectKey);
                    
                if (!dict.ContainsKey(currentPositionKey))
                {
                    dict[currentPositionKey] = new HashSet<string>();
                }
            }

            // UpdateObjectPositionAsync 메서드 접근
            var method = typeof(CommonMapController).GetMethod("UpdateObjectPositionAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);

            // Act
            if (method != null)
            {
                await (Task)method.Invoke(_controller, [lastPositionKey, currentPositionKey, objectKey])!;

                // Assert
                // 이전 위치에서 객체가 제거되었는지 확인
                if (field != null)
                {
                    var dict = (ConcurrentDictionary<string, HashSet<string>>)field.GetValue(_controller)!;
                    Assert.That(dict[lastPositionKey], Does.Not.Contain(objectKey),
                        "Object should be removed from last position");
                    Assert.That(dict[currentPositionKey], Does.Contain(objectKey),
                        "Object should be added to current position");
                }
            }
            else
            {
                Assert.Fail("UpdateObjectPositionAsync method not found");
            }
        }
        
        [TearDown]
        public void TearDown()
        {
            // Dispose 가능한 리소스 정리
            _cts.Dispose();
        }
    }
}