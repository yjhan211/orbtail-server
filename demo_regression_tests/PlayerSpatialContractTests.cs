using System.Buffers;
using game_server.players;
using MessagePack;
using network.common;
using network.common.data.models;
using network.packets;

namespace demo_regression_tests;

public sealed class PlayerSpatialContractTests
{
    // 위치·셀 변환은 맵 정보가 있어야 한다 — 실행 순서와 무관하게 게임 데이터를 먼저 올린다.
    public PlayerSpatialContractTests() => UserServerMatchingTestData.EnsureGameDataLoaded();

    [Fact]
    public void AreaIsDerivedFromMapAndCellAndNeverSerialized()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var map = Config.SWARM_MATCH_MAP;
        var cell = network.common.data.GameMapData.GetAreaSpawnCell(map, AreaType.S2Corridor9).Clone();
        var info = new GameObjectInfo(ObjectType.PLAYER, 1, map, cell);
        var before = new game_server.matches.MatchObjectSnapshot(info);
        var destination = network.common.data.GameMapData.GetAreaSpawnCell(map, AreaType.S2Library1);
        info.Cell.X = destination.X;
        info.Cell.Y = destination.Y;
        Assert.Equal(AreaType.S2Library1, info.Area);
        Assert.Equal(AreaType.S2Corridor9, before.Area);
        Assert.Null(typeof(Player).GetProperty("CurrentArea"));
        Assert.Null(typeof(GameObjectInfo).GetProperty(nameof(GameObjectInfo.Area))!.SetMethod);

        var bytes = MessagePackSerializer.Serialize(info);
        Assert.DoesNotContain("\"area\"", MessagePackSerializer.ConvertToJson(bytes));
        var copy = MessagePackSerializer.Deserialize<GameObjectInfo>(bytes);
        Assert.Equal(AreaType.S2Library1, copy.Area);

        info.MapId = MapId.None;
        Assert.Equal(AreaType.None, info.Area);
        info.MapId = map;
        info.Cell = new Cell(-10000, -10000);
        Assert.Equal(AreaType.None, info.Area);
        Assert.Equal(AreaType.S2Library1, copy.Area);
    }

    [Fact]
    public async Task RedisSave_ExcludesRuntimeStateWithoutChangingLiveModel()
    {
        var redis = new InMemoryRedisOperations();
        var presence = CreatePlayerObject();
        var player = new PlayerInfo { PlayerId = 42, Name = presence.Name, WearItemIdList = new(presence.WearItemIdList) };
        player.InventoryInfo = new InventoryInfo(InventoryOwnerType.PLAYER, player.PlayerId);
        var objectInfo = presence.ObjectInfo;
        var state = presence.State;
        await player.Save(redis);
        var stored = await redis.HashGetAsync(PlayerInfo.HashKey, player.PlayerId);
        string json = MessagePackSerializer.ConvertToJson((byte[])stored!);
        Assert.DoesNotContain("objectInfo", json);
        Assert.DoesNotContain("\"state\"", json);
        Assert.Same(objectInfo, presence.ObjectInfo);
        Assert.Equal(state, presence.State);
        var loaded = await PlayerInfo.Load(redis, player.PlayerId);
        Assert.NotNull(loaded);
        Assert.Equal(player.Name, loaded.Name);
        Assert.Null(typeof(PlayerInfo).GetProperty("ObjectInfo"));
        Assert.Null(typeof(PlayerInfo).GetProperty("State"));
    }

    [Fact]
    public void LoginPacket_CarriesProfileAndCredentialWithoutLobbySpatialData()
    {
        var profile = new PlayerInfo(42, false) { Name = "ProfileOnly", Gold = 999 };
        using var packet = PacketMaker.U_TO_C_LOGIN(profile, "test-credential");
        using var wire = Packet.Create(packet.ToBytes());
        Assert.Equal(Protocol.U_TO_C_LOGIN, (Protocol)wire.PopProtocolId());
        wire.PopPlayerId();
        var login = MessagePackSerializer.Deserialize<U_TO_C_LOGIN>(wire.PopBody());
        Assert.Equal("ProfileOnly", login.PlayerInfo.Name);
        Assert.Equal(999, login.PlayerInfo.Gold);
        Assert.Equal("test-credential", login.AccountToken);
        Assert.Null(typeof(U_TO_C_LOGIN).GetProperty("ObjectInfo"));
        string json = MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(login));
        Assert.DoesNotContain("objectInfo", json);
    }

    [Fact]
    public void PlayerInfoSerialization_ExcludesGameState()
    {
        var presence = CreatePlayerObject();
        var player = new PlayerInfo { PlayerId = 42, Name = presence.Name, WearItemIdList = new(presence.WearItemIdList) };
        Assert.NotNull(presence.ObjectInfo);
        string json = MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(player));
        Assert.DoesNotContain("lastMap", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lastCell", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("objectInfo", json);
        Assert.DoesNotContain("position", json);
        var copy = MessagePackSerializer.Deserialize<PlayerInfo>(MessagePackSerializer.Serialize(player));
        Assert.Null(typeof(PlayerInfo).GetProperty("ObjectInfo"));
        Assert.DoesNotContain("\"state\"", json);
        Assert.NotNull(presence.ObjectInfo);
        Assert.Contains("playerId", json);
    }

    [Fact]
    public void LegacyPlayerInfo_IgnoresSavedPositionWithoutLosingProfile()
    {
        // 기존 Redis 값의 위치 키가 남아 있어도 새 모델로 그대로 읽을 수 있다.
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteMapHeader(6);
        writer.Write("playerId"); writer.Write(42L);
        writer.Write("name"); writer.Write("ExistingPlayer");
        writer.Write("gold"); writer.Write(999L);
        writer.Write("lastMapId"); writer.Write(123);
        writer.Write("lastMapSubId"); writer.Write(456L);
        writer.Write("lastCell"); writer.WriteMapHeader(2);
        writer.Write("x"); writer.Write(100);
        writer.Write("y"); writer.Write(200);
        writer.Flush();

        var player = MessagePackSerializer.Deserialize<PlayerInfo>(buffer.WrittenMemory);
        Assert.Equal(42, player.PlayerId);
        Assert.Equal("ExistingPlayer", player.Name);
        Assert.Equal(999, player.Gold);
        Assert.Null(typeof(PlayerInfo).GetProperty("ObjectInfo"));
    }

    [Fact]
    public void ObjectPresence_RoundTripsProfileSpatialAndActionState()
    {
        using var packet = PacketMaker.G_TO_C_PLAYER_INFO([CreatePlayerObject()]);
        using var wire = Packet.Create(packet.ToBytes());
        wire.PopProtocolId();
        wire.PopPlayerId();
        var bytes = wire.PopBody();
        var copy = MessagePackSerializer.Deserialize<G_TO_C_PLAYER_INFO>(bytes);
        var presence = Assert.Single(copy.Players);
        AssertPlayerObject(presence);
        string json = MessagePackSerializer.ConvertToJson(bytes);
        Assert.DoesNotContain("playerInfo", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wearItemIdList", json);
        Assert.DoesNotContain("gold", json);
        Assert.DoesNotContain("isNew", json);
    }

    [Fact]
    public void ObjectEntry_RoundTripsSameSpatialContract()
    {
        var body = new G_TO_C_OBJECT_ENTER();
        body.Players.Add(CreatePlayerObject());
        var bytes = MessagePackSerializer.Serialize(body);
        var copy = MessagePackSerializer.Deserialize<G_TO_C_OBJECT_ENTER>(bytes);
        AssertPlayerObject(Assert.Single(copy.Players));
    }

    [Fact]
    public void AreaUsesTheSharedSpatialObjectAndSnapshotIsIndependent()
    {
        UserServerMatchingTestData.EnsureGameDataLoaded();
        var player = new Player(new PlayerInfo { PlayerId = 17 });
        player.InitializeSpawn(network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Corridor9)));
        Assert.Equal(AreaType.S2Corridor9, player.GameInfo.ObjectInfo.Area);
        player.InitializeSpawn(network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Library1)));
        Assert.Equal(AreaType.S2Library1, player.GameInfo.ObjectInfo.Area);
        var snapshot = player.GameInfo.ObjectInfo.Clone();
        player.InitializeSpawn(network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.None)));
        Assert.Equal(AreaType.S2Library1, snapshot.Area);

        var monster = new network.common.data.models.MonsterInfo
        {
            ObjectInfo = new GameObjectInfo(ObjectType.MONSTER, 1, Config.SWARM_MATCH_MAP,
                network.common.data.GameMapData.GetAreaSpawnCell(Config.SWARM_MATCH_MAP, AreaType.S2Corridor9))
        };
        Assert.Equal(AreaType.S2Corridor9, monster.ObjectInfo.Area);
        monster.ObjectInfo.Cell = network.common.data.GameMapData.GetAreaSpawnCell(network.common.Config.SWARM_MATCH_MAP, (network.common.AreaType)(AreaType.S2Library1));
        Assert.Equal(AreaType.S2Library1, monster.AreaType);
        var copy = MessagePackSerializer.Deserialize<network.common.data.models.MonsterInfo>(
            MessagePackSerializer.Serialize(monster));
        Assert.Equal(AreaType.S2Library1, copy.ObjectInfo.Area);
    }
    private static GamePlayerInfo CreatePlayerObject() => new()
    {
        Name = "TestPlayer",
        WearItemIdList = [101000003],
        ObjectInfo = new GameObjectInfo(ObjectType.PLAYER, 42, MapId.Camp, new Cell(3, 4))
        {
            MapId = network.common.Config.SWARM_MATCH_MAP,
            Position = new Vector3f(3.25f, 4.75f, 0),
            Velocity = new Vector3f(1.5f, -0.5f, 0),
            Rotation = 75f
        },
        State = PlayerState.SLEEP, Health = 73, Status = PlayerMatchStatus.ACTIVE
    };

    [Fact]
    public void MatchAppearanceAndPublishedSnapshot_DoNotShareProfileLists()
    {
        var profile = new PlayerInfo { PlayerId = 7, Name = "MatchName", WearItemIdList = [101000003] };
        var player = new Player(profile) { Position = new Vector3f(1, 2, 0) };
        profile.Name = "LobbyName";
        profile.WearItemIdList.Clear();
        var snapshot = player.CreatePlayerObjectInfo();
        Assert.Equal("MatchName", snapshot.Name);
        Assert.Equal(new[] { 101000003 }, snapshot.WearItemIdList);
        snapshot.WearItemIdList.Clear();
        Assert.Single(player.GameInfo.WearItemIdList);
    }
    // 공간 정보는 GameObjectInfo가, 행동 상태는 플레이어 전송 단위가 든다.
    private static void AssertPlayerObject(GamePlayerInfo player)
    {
        Assert.Equal("TestPlayer", player.Name);
        Assert.Equal(new[] { 101000003 }, player.WearItemIdList);
        var info = player.ObjectInfo;
        Assert.Equal(42, info.ObjectId);
        Assert.Equal(network.common.data.GameMapData.GetCurrentArea(info.MapId, info.Cell), info.Area);
        Assert.Null(typeof(GameObjectInfo).GetProperty("MapSubId"));
        Assert.Equal(3.25f, info.Position.X);
        Assert.Equal(4.75f, info.Position.Y);
        Assert.Equal(1.5f, info.Velocity.X);
        Assert.Equal(75f, info.Rotation);
        Assert.Null(typeof(GameObjectInfo).GetProperty("State"));
        Assert.Equal(PlayerState.SLEEP, player.State);
        Assert.Equal(73, player.Health);
        Assert.Equal(PlayerMatchStatus.ACTIVE, player.Status);
    }

    // 행동 상태만 먼저 바뀐 플레이어는 아직 스폰 전이다. 위치는 없고 전송 스냅샷도 만들 수 없어야 한다.
    [Fact]
    public void PlayerStateBeforeSpawn_KeepsPositionEmptyAndBlocksSnapshot()
    {
        var player = new Player(new PlayerInfo { PlayerId = 7 });
        player.State = PlayerState.SLEEP;
        Assert.False(player.IsSpawned);
        Assert.Null(player.Position);
        Assert.Null(player.Cell);
        Assert.Throws<InvalidOperationException>(() => player.CreateGameObjectInfo());

        player.Position = new Vector3f(1f, 2f, 0f);
        Assert.True(player.IsSpawned);
        Assert.NotNull(player.Cell);
        var snapshot = player.CreatePlayerObjectInfo();
        Assert.Equal(PlayerState.SLEEP, snapshot.State);
        Assert.Equal(7, snapshot.ObjectInfo.ObjectId);
        player.Health = 51;
        Assert.Equal(51, player.GameInfo.Health);
        player.GameInfo.Status = PlayerMatchStatus.ACTIVE;
        Assert.Equal(PlayerMatchStatus.ACTIVE, player.Status);
        Assert.NotEqual(player.Health, snapshot.Health);
        snapshot.ObjectInfo.Position.X = 99f;
        Assert.Equal(1f, player.Position!.X);

        player.Position = null;
        Assert.False(player.IsSpawned);
        Assert.Null(player.Position);
        Assert.Null(player.Cell);
        Assert.Throws<InvalidOperationException>(() => player.CreateGameObjectInfo());

        player.Cell = new Cell(3, 4);
        Assert.True(player.IsSpawned);
        Assert.NotNull(player.Position);
        Assert.NotNull(player.Cell);
        player.Cell = null;
        Assert.False(player.IsSpawned);
        Assert.Null(player.Position);
        Assert.Null(player.Cell);
    }
}
