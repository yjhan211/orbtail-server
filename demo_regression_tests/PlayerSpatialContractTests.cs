using System.Buffers;
using game_server.players;
using MessagePack;
using network.common;
using network.common.data.models;
using network.packets;

namespace demo_regression_tests;

public sealed class PlayerSpatialContractTests
{
    [Fact]
    public async Task RedisSave_ExcludesRuntimeStateWithoutChangingLiveModel()
    {
        var redis = new InMemoryRedisOperations();
        var player = CreatePlayerObject();
        player.InventoryInfo = new InventoryInfo(InventoryOwnerType.PLAYER, player.PlayerId);
        var objectInfo = player.ObjectInfo;
        var state = player.State;
        await player.Save(redis);
        var stored = await redis.HashGetAsync(PlayerInfo.HashKey, player.PlayerId);
        string json = MessagePackSerializer.ConvertToJson((byte[])stored!);
        Assert.DoesNotContain("objectInfo", json);
        Assert.DoesNotContain("\"state\"", json);
        Assert.Same(objectInfo, player.ObjectInfo);
        Assert.Equal(state, player.State);
        var loaded = await PlayerInfo.Load(redis, player.PlayerId);
        Assert.NotNull(loaded);
        Assert.Equal(player.Name, loaded.Name);
        Assert.Null(loaded.ObjectInfo);
        Assert.Equal(PlayerState.IDLE, loaded.State);
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
        Assert.Null(login.PlayerInfo.ObjectInfo);
    }

    [Fact]
    public void PlayerInfoSerialization_ContainsCommonSpatialAndActionState()
    {
        var player = CreatePlayerObject();
        Assert.NotNull(player.ObjectInfo);
        string json = MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(player));
        Assert.DoesNotContain("lastMap", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lastCell", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("objectInfo", json);
        Assert.Contains("position", json);
        AssertPlayerObject(MessagePackSerializer.Deserialize<PlayerInfo>(MessagePackSerializer.Serialize(player)));
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
        Assert.Null(player.ObjectInfo);
    }

    [Fact]
    public void ObjectPresence_RoundTripsProfileSpatialAndActionState()
    {
        var bytes = MessagePackSerializer.Serialize(new G_TO_C_OBJECT_INFO { Players = [CreatePlayerObject()] });
        var copy = MessagePackSerializer.Deserialize<G_TO_C_OBJECT_INFO>(bytes);
        AssertPlayerObject(Assert.Single(copy.Players));
        string json = MessagePackSerializer.ConvertToJson(bytes);
        Assert.DoesNotContain("playerInfo", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wearItemIdList", json);
        Assert.Contains("gold", json);
    }

    [Fact]
    public void AreaEntry_RoundTripsSameSpatialContract()
    {
        var bytes = MessagePackSerializer.Serialize(new G_TO_C_AREA_PLAYER_ENTER { Player = CreatePlayerObject() });
        var copy = MessagePackSerializer.Deserialize<G_TO_C_AREA_PLAYER_ENTER>(bytes);
        AssertPlayerObject(copy.Player);
    }

    private static PlayerInfo CreatePlayerObject() => new()
    {
        PlayerId = 42,
        Name = "TestPlayer",
        WearItemIdList = [101000003],
        ObjectInfo = new GameObjectInfo(ObjectType.PLAYER, 42, MapId.Camp, 123, new Cell(3, 4))
        {
            Position = new Vector3f(3.25f, 4.75f, 0),
            Velocity = new Vector3f(1.5f, -0.5f, 0),
            Rotation = 75f
        },
        State = PlayerState.SLEEP
    };

    // 공간 정보는 GameObjectInfo가, 행동 상태는 플레이어 전송 단위가 든다.
    private static void AssertPlayerObject(PlayerInfo player)
    {
        var info = player.ObjectInfo;
        Assert.Equal(42, info.ObjectId);
        Assert.Equal(123, info.MapSubId);
        Assert.Equal(3.25f, info.Position.X);
        Assert.Equal(4.75f, info.Position.Y);
        Assert.Equal(1.5f, info.Velocity.X);
        Assert.Equal(75f, info.Rotation);
        Assert.Null(typeof(GameObjectInfo).GetProperty("State"));
        Assert.Equal(PlayerState.SLEEP, player.State);
    }

    // 행동 상태만 먼저 바뀐 플레이어는 아직 스폰 전이다. 위치는 없고 전송 스냅샷도 만들 수 없어야 한다.
    [Fact]
    public void PlayerStateBeforeSpawn_KeepsPositionEmptyAndBlocksSnapshot()
    {
        var player = new Player { Profile = new PlayerInfo { PlayerId = 7 } };
        player.State = PlayerState.SLEEP;
        Assert.Null(player.Position);
        Assert.Throws<InvalidOperationException>(() => player.CreateGameObjectInfo());

        player.Position = new Vector3f(1f, 2f, 0f);
        var snapshot = player.CreatePlayerObjectInfo();
        Assert.Equal(PlayerState.SLEEP, snapshot.State);
        Assert.Equal(7, snapshot.ObjectInfo.ObjectId);
        snapshot.ObjectInfo.Position.X = 99f;
        Assert.Equal(1f, player.Position!.X);

        player.Position = null;
        Assert.Null(player.Position);
    }
}
