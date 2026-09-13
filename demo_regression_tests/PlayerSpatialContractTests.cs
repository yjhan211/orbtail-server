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
        Assert.DoesNotContain("objectInfo", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlayerInfoSerialization_ContainsNoSpatialData()
    {
        var player = new PlayerInfo(42, false);
        Assert.Null(typeof(PlayerInfo).GetProperty("ObjectInfo"));
        string json = MessagePackSerializer.ConvertToJson(MessagePackSerializer.Serialize(player));
        Assert.DoesNotContain("lastMap", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lastCell", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("objectInfo", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("position", json, StringComparison.OrdinalIgnoreCase);
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
    public void ObjectPresence_RoundTripsExactPositionWithoutPlayerProfile()
    {
        var bytes = MessagePackSerializer.Serialize(new G_TO_C_OBJECT_INFO { Objects = [CreateObject()] });
        var copy = MessagePackSerializer.Deserialize<G_TO_C_OBJECT_INFO>(bytes);
        AssertSpatialData(Assert.Single(copy.Objects));
        string json = MessagePackSerializer.ConvertToJson(bytes);
        Assert.DoesNotContain("playerInfo", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wearItemIdList", json);
        Assert.DoesNotContain("gold", json);
    }

    [Fact]
    public void AreaEntry_RoundTripsSameSpatialContract()
    {
        var bytes = MessagePackSerializer.Serialize(new G_TO_C_AREA_PLAYER_ENTER { ObjectInfo = CreateObject() });
        var copy = MessagePackSerializer.Deserialize<G_TO_C_AREA_PLAYER_ENTER>(bytes);
        AssertSpatialData(copy.ObjectInfo);
    }

    private static GameObjectInfo CreateObject() => new(ObjectType.PLAYER, 42, MapId.Camp, 123, new Cell(3, 4))
    {
        Position = new Vector3f(3.25f, 4.75f, 0),
        Velocity = new Vector3f(1.5f, -0.5f, 0),
        Rotation = 75f,
        State = PlayerState.SLEEP
    };

    private static void AssertSpatialData(GameObjectInfo info)
    {
        Assert.Equal(42, info.ObjectId);
        Assert.Equal(123, info.MapSubId);
        Assert.Equal(3.25f, info.Position.X);
        Assert.Equal(4.75f, info.Position.Y);
        Assert.Equal(1.5f, info.Velocity.X);
        Assert.Equal(75f, info.Rotation);
        Assert.Equal(PlayerState.SLEEP, info.State);
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
        var snapshot = player.CreateGameObjectInfo();
        Assert.Equal(PlayerState.SLEEP, snapshot.State);
        Assert.Equal(7, snapshot.ObjectId);
        snapshot.Position.X = 99f;
        Assert.Equal(1f, player.Position!.X);

        player.Position = null;
        Assert.Null(player.Position);
    }
}
