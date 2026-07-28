using MessagePack;
using network.common;
using network.common.data.models;
using network.packets;

namespace game_server.network;

public partial class GameClientSession
{
    private void SendMonsterSnapshot()
    {
        if (CurrentMapSubId <= 0)
            return;

        foreach (var monsterChunk in _emotionAfterimageMonsterManager.GetSnapshot(CurrentMapSubId).Chunk(10))
        {
            using var packet = Packet.Create((int)Protocol.G_TO_C_MONSTER_SNAPSHOT);
            packet.SetBody(MessagePackSerializer.Serialize(new G_TO_C_MONSTER_SNAPSHOT
            {
                Monsters = monsterChunk.ToList()
            }));
            Send(packet);
        }
    }
}
